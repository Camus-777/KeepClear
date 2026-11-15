using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace KeepClear
{
    internal static class ReliabilityTests
    {
        private static object Field(object target, string name)
        {
            return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        }

        private static void Set(object target, string name, object value)
        {
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        }

        internal static void Run(Action<bool, string> check)
        {
            Settings(check);
            Devices(check);
            Cleanup(check);
        }

        private static void Settings(Action<bool, string> check)
        {
            string folder = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "artifacts", "settings-errors", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "settings.xml");
            File.WriteAllText(path, "<AppSettings xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:nil=\"true\" />");
            check(AppSettings.Load(path).Language == "en", "null XML settings fall back to defaults without crashing");
            File.WriteAllText(path, "<AppSettings><Language>");
            check(AppSettings.Load(path).Language == "en", "malformed XML settings still fall back to defaults");
            File.WriteAllText(path, "<AppSettings><Language>zh</Language><InputId>preserve this profile</InputId></AppSettings>");
            using (var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                check(AppSettings.Load(path).Language == "en", "locked settings fall back to defaults without crashing");
            FileSecurity original = File.GetAccessControl(path), denied = File.GetAccessControl(path);
            denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.ReadData, AccessControlType.Deny));
            try
            {
                File.SetAccessControl(path, denied);
                bool refused = false;
                try
                {
                    using (var file = File.OpenRead(path))
                    {
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    refused = true;
                }

                check(refused && File.Exists(path), "permission regression fixture is present but genuinely unreadable");
                check(AppSettings.Load(path).Language == "en", "read access denied to settings no longer aborts startup");
            }
            finally
            {
                var restored = new FileSecurity();
                restored.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                File.SetAccessControl(path, restored);
            }

            check(AppSettings.Load(path).InputId == "preserve this profile" && AppSettings.Load(path).Language == "zh", "fallback reads do not rewrite the protected settings file");
        }

        private static void Devices(Action<bool, string> check)
        {
            foreach (string name in new[]
            {
                "VB-Audio Voicemeeter VAIO",
                "VB-Audio Cable A",
                "Cable Matters USB Audio",
                "USB Audio Device",
                "CABLE Input (VB-Audio Virtual Cable)"
            })
                check(!new AudioDevice { Name = name }.IsCable, "endpoint label cannot misclassify another adapter as base VB-CABLE: " + name);
            var node = DriverChecks.Node("base");
            var capture = new AudioDevice
            {
                Name = "Renamed microphone",
                Capture = true,
                Driver = node,
                State = 1
            };
            var render = new AudioDevice
            {
                Name = "Renamed speakers",
                Capture = false,
                Driver = node,
                State = 1
            };
            check(capture.IsCable && render.IsCable, "renamed base VB-CABLE endpoints retain their adapter identity");
            check(!new AudioDevice { Name = "CABLE Input" }.IsCable, "missing adapter metadata does not use a broad display-name fallback");
            var devices = new[]
            {
                capture,
                render
            };
            check(VbCableInstaller.Classify(new[] { node }, devices) == CableState.Ready, "startup and route selection share the renamed-device classification");
        }

        private static void Cleanup(Action<bool, string> check)
        {
            using (var release = new ManualResetEvent(false))
            using (var finished = new ManualResetEvent(false))
            using (var engine = new AudioEngine(new EngineOptions()))
            {
                var cancellation = (CancellationTokenSource)Field(engine, "cancellation");
                var blocked = new Thread(() => release.WaitOne())
                {
                    IsBackground = true,
                    Name = "simulated blocked capture"
                };
                var cooperative = new Thread(() =>
                {
                    cancellation.Token.WaitHandle.WaitOne();
                    finished.Set();
                })
                {
                    IsBackground = true,
                    Name = "simulated cooperative render"
                };
                var monitor = new MonitorOutput("invalid", cancellation.Token);
                Set(engine, "captureThread", blocked);
                Set(engine, "renderThread", cooperative);
                Set(engine, "monitor", monitor);
                blocked.Start();
                cooperative.Start();
                try
                {
                    bool timedOut = false;
                    try
                    {
                        engine.Dispose();
                    }
                    catch (AggregateException ex)
                    {
                        timedOut = ex.Flatten().InnerExceptions.Any(e => e is TimeoutException);
                    }

                    check(timedOut && (int)Field(engine, "disposed") == 0, "stop timeout does not mark engine fully disposed");
                    check(finished.WaitOne(1000) && Field(engine, "monitor") == null, "a blocked capture does not skip the other thread or monitor cleanup");
                    ((AutoResetEvent)Field(engine, "inputReady")).Set();
                    check(cancellation.IsCancellationRequested && !String.IsNullOrEmpty(engine.Snapshot().Error), "stop timeout retains safe wait handles and an actionable fault state");
                    release.Set();
                    blocked.Join();
                    engine.Dispose();
                    bool handlesClosed = false;
                    try
                    {
                        ((AutoResetEvent)Field(engine, "inputReady")).Set();
                    }
                    catch (ObjectDisposedException)
                    {
                        handlesClosed = true;
                    }

                    check(handlesClosed && (int)Field(engine, "disposed") == 1, "retry finishes cleanup after blocked thread exits");
                    engine.Dispose();
                    engine.Stop();
                    check(true, "repeated stop and dispose remain safe after successful cleanup");
                    monitor.Dispose();
                }
                finally
                {
                    release.Set();
                    blocked.Join(1000);
                    cooperative.Join(1000);
                }
            }

            using (var engine = new AudioEngine(new EngineOptions()))
            {
                var cancellation = (CancellationTokenSource)Field(engine, "cancellation");
                using (cancellation.Token.Register(() =>
                {
                    throw new InvalidOperationException("simulated cancellation callback failure");
                }))
                {
                    bool failed = false;
                    try
                    {
                        engine.Dispose();
                    }
                    catch (AggregateException)
                    {
                        failed = true;
                    }

                    check(failed && (int)Field(engine, "disposed") == 0, "cancellation callback failure preserves disposal retry");
                    engine.Dispose();
                    check((int)Field(engine, "disposed") == 1, "cleanup can recover from a cancellation callback exception");
                }
            }

            using (var engine = new AudioEngine(new EngineOptions()))
            {
                Task.WaitAll(Task.Run((Action)engine.Dispose), Task.Run((Action)engine.Dispose));
                bool rejected = false;
                try
                {
                    engine.Start();
                }
                catch (ObjectDisposedException)
                {
                    rejected = true;
                }

                check(rejected, "concurrent disposal completes once and disposed engine cannot start");
            }
        }
    }
}

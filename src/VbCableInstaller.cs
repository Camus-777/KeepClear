using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace KeepQuiet
{
    internal enum CableState
    {
        Ready,
        Missing,
        Incomplete,
        Disabled,
        RestartRequired,
        InstallBusy,
        DetectionFailed,
        Pending
    }

    internal static class VbCableInstaller
    {
        internal const string ArchiveHash = "bc44e8974191a5a2427d6c979e2dab08dbb09925f3f435dfa2f0ba3e45b6eb90";
        private const string SetupHash = "734c35dfa6d98f48782a451633ceb471166ec70d60482fd89a1123d0ee3c4f41";
        internal static string Hash(Stream stream)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static CableState Probe()
        {
            var nodes = DriverCatalog.BaseCableNodes();
            var endpoints = NativeAudio.Devices(true, 3).Concat(NativeAudio.Devices(false, 3)).ToArray();
            foreach (var node in nodes)
                Diagnostics.Write("driver", node.ToString());
            foreach (var endpoint in endpoints)
                Diagnostics.Write("endpoint", endpoint.Id + " state=" + endpoint.State + " driver=" + (endpoint.Driver == null ? endpoint.IdentityError : endpoint.Driver.Id));
            CableState result = Classify(nodes.ToArray(), endpoints);
            Diagnostics.Write("driver-state", result.ToString());
            return result;
        }

        internal static CableState Classify(DriverNode[] nodes, AudioDevice[] endpoints)
        {
            var cable = nodes.Where(n => n.IsBaseCable).ToArray();
            foreach (var node in cable.Where(n => String.IsNullOrEmpty(n.IdentityError) && n.ServiceMatches && n.Started && !n.NeedsRestart))
            {
                var active = endpoints.Where(e => e.IsCable && e.State == 1 && e.Driver.Id == node.Id);
                if (active.Any(e => e.Capture) && active.Any(e => !e.Capture))
                    return CableState.Ready;
            }

            if (nodes.Any(n => !String.IsNullOrEmpty(n.IdentityError) && (n.IsBaseCable || endpoints.Any(e => e.IsCable && String.Equals(e.Driver.Id, n.Id, StringComparison.OrdinalIgnoreCase)))))
                return CableState.DetectionFailed;
            if (cable.Any(n => n.Disabled) || endpoints.Any(e => e.IsCable && e.State == 2))
                return CableState.Disabled;
            if (cable.Any(n => n.NeedsRestart))
                return CableState.RestartRequired;
            if (cable.Length == 0)
                return CableState.Missing;
            // An unknown endpoint identity must not trigger a potentially unnecessary reinstall.
            if (endpoints.Any(e => !String.IsNullOrEmpty(e.IdentityError) && cable.Any(n => String.Equals(n.Id, e.AdapterId, StringComparison.OrdinalIgnoreCase))))
                return CableState.DetectionFailed;
            return CableState.Incomplete;
        }

        internal static CableState Ensure(bool allowInstall, Action<string> progress)
        {
            string record = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeepQuiet", "driver-setup", "install-state.xml");
            if (!allowInstall)
                return Ensure(false, progress, Probe, Install, 0, new DriverInstallState(record, DriverInstallState.BootUtc));
            using (var mutex = new Mutex(false, @"Global\KeepQuiet.VBCABLE.Install.v1"))
            {
                bool held = false;
                try
                {
                    try
                    {
                        held = mutex.WaitOne(0);
                    }
                    catch (AbandonedMutexException)
                    {
                        held = true;
                    }

                    if (!held)
                        return CableState.InstallBusy;
                    return Ensure(true, progress, Probe, Install, 20000, new DriverInstallState(record, DriverInstallState.BootUtc), delegate
                    {
                        var running = Process.GetProcessesByName("VBCABLE_Setup_x64");
                        bool busy = running.Length != 0;
                        foreach (var process in running)
                            process.Dispose();
                        return busy;
                    });
                }
                finally
                {
                    if (held)
                        mutex.ReleaseMutex();
                }
            }
        }

        private static bool Repairable(CableState state)
        {
            return state == CableState.Missing || state == CableState.Incomplete;
        }

        internal static string Extract(string parent)
        {
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("KeepQuiet.VBCable.zip"))
            {
                if (resource == null || Hash(resource) != ArchiveHash)
                    throw new InvalidDataException("Bundled VB-CABLE archive is missing or damaged.");
                resource.Position = 0;
                string directory = Path.Combine(parent, "VB-CABLE-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string prefix = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
                using (var archive = new ZipArchive(resource, ZipArchiveMode.Read))
                    foreach (var entry in archive.Entries)
                    {
                        string target = Path.GetFullPath(Path.Combine(directory, entry.FullName));
                        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Unsafe driver archive path.");
                        if (String.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(target);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var source = entry.Open())
                        using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                            source.CopyTo(destination);
                    }

                string setup = Path.Combine(directory, "VBCABLE_Setup_x64.exe");
                using (var file = File.OpenRead(setup))
                    if (Hash(file) != SetupHash)
                        throw new InvalidDataException("Bundled driver setup checksum mismatch.");
                return setup;
            }
        }

        internal static int Install(string parent, Func<ProcessStartInfo, int> launch)
        {
            string setup = Extract(parent);
            var info = new ProcessStartInfo(setup, "-i -h")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(setup),
                WindowStyle = ProcessWindowStyle.Hidden
            };
            return launch(info);
        }

        internal static int Install()
        {
            string parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeepQuiet", "driver-setup");
            return Install(parent, delegate (ProcessStartInfo info)
            {
                using (var process = Process.Start(info))
                {
                    // Do not terminate a driver installer mid-installation or auto-reboot Windows.
                    process.WaitForExit();
                    return process.ExitCode;
                }
            });
        }

        internal static CableState Ensure(bool allowInstall, Action<string> progress, Func<CableState> probe, Func<int> install, int readinessWaitMs = 0, DriverInstallState tracking = null, Func<bool> installerBusy = null)
        {
            CableState state = probe();
            if (tracking != null && tracking.Restart)
                return CableState.RestartRequired;
            if (state == CableState.Ready && tracking != null && allowInstall)
                tracking.Clear();
            if (Repairable(state) && tracking != null && tracking.Pending)
            {
                if (installerBusy != null && installerBusy())
                    return CableState.InstallBusy;
                state = allowInstall ? WaitForEndpoints(state, probe, readinessWaitMs, progress) : CableState.Pending;
                if (allowInstall && state == CableState.Ready)
                    tracking.Clear();
                if (allowInstall && state == CableState.RestartRequired)
                    tracking.Save(true);
                return state;
            }

            // Older records were also written before setup started. Observe once, then allow repair.
            if (Repairable(state) && tracking != null && tracking.Unconfirmed && allowInstall)
            {
                if (installerBusy != null && installerBusy())
                    return CableState.InstallBusy;
                state = WaitForEndpoints(state, probe, readinessWaitMs, progress);
                if (state != CableState.Pending)
                {
                    if (state == CableState.Ready)
                        tracking.Clear();
                    if (state == CableState.RestartRequired)
                        tracking.Save(true);
                    return state;
                }

                tracking.Clear();
                state = probe();
            }

            if (!Repairable(state) || !allowInstall)
                return state;
            // A second read avoids reinstalling a driver which appeared during startup.
            state = probe();
            if (!Repairable(state))
                return state;
            if (installerBusy != null && installerBusy())
                return CableState.InstallBusy;
            progress("installing-driver");
            int code;
            try
            {
                code = install();
            }
            catch
            {
                if (tracking != null)
                    tracking.Clear();
                throw;
            }

            Diagnostics.Write("driver-install", "exit_code=" + code);
            if (tracking != null)
            {
                if (code == 0 || code == 3010 || code == 1641)
                    tracking.Save(code == 3010 || code == 1641);
                else
                    tracking.Clear();
            }

            try
            {
                state = probe();
            }
            catch (Exception ex)
            {
                Diagnostics.Failure("driver-post-install", ex);
                if (code == 3010 || code == 1641)
                    return CableState.RestartRequired;
                if (code != 0)
                    throw new InvalidOperationException("VB-CABLE setup exit code: " + code, ex);
                throw;
            }

            if (code == 3010 || code == 1641)
                return CableState.RestartRequired;
            if (code != 0)
                throw new InvalidOperationException("VB-CABLE setup exit code: " + code);
            state = WaitForEndpoints(state, probe, readinessWaitMs, progress);
            if (state == CableState.RestartRequired)
            {
                if (tracking != null)
                    tracking.Save(true);
                return state;
            }

            if (state == CableState.Ready && tracking != null)
                tracking.Clear();
            return state;
        }

        private static CableState WaitForEndpoints(CableState state, Func<CableState> probe, int milliseconds, Action<string> progress)
        {
            if (!Repairable(state))
                return state;
            progress("waiting-driver");
            var wait = Stopwatch.StartNew();
            while (Repairable(state) && wait.ElapsedMilliseconds < milliseconds)
            {
                Thread.Sleep(200);
                progress("waiting-driver");
                state = probe();
            }

            return Repairable(state) ? CableState.Pending : state;
        }
    }
}

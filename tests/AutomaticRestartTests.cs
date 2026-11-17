using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KeepClear
{
    internal static class AutomaticRestartTests
    {
        private static object Field(MainForm form, string name)
        {
            return typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        }
        private static Task Call(MainForm form, string name)
        {
            return (Task)typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
        }
        private static void Tick(MainForm form)
        {
            typeof(MainForm).GetMethod("TickUi", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
        }
        private static void Pump(Func<bool> complete)
        {
            var watch = Stopwatch.StartNew();
            while (!complete() && watch.ElapsedMilliseconds < 4000)
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }
            if (!complete()) throw new Exception("Automatic restart fixture timed out");
        }
        private static void Await(Task task)
        {
            Pump(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }

        private sealed class Session : IAudioSession
        {
            internal EngineSnapshot Current = new EngineSnapshot { State = "运行中", CaptureFrames = 160, ProcessedRenderFrames = 480 };
            internal volatile bool Disposed;
            internal bool FailCleanup, FailMonitor;
            internal string MonitorId;
            internal ManualResetEvent CleanupEntered, CleanupRelease;
            public void Start() { }
            public EngineSnapshot Snapshot() { return Current; }
            public void SetMonitor(string id)
            {
                if (FailMonitor) throw new Exception("fixture monitor unavailable");
                MonitorId = id;
                Current.MonitorActive = !String.IsNullOrEmpty(id);
            }
            internal void Fault(AudioFailureKind kind)
            {
                Current = new EngineSnapshot { State = "已停止：发生错误", Error = "fixture " + kind, FailureKind = kind };
            }
            public void Dispose()
            {
                if (CleanupEntered != null) CleanupEntered.Set();
                if (CleanupRelease != null) CleanupRelease.WaitOne();
                if (FailCleanup) throw new Exception("fixture cleanup incomplete");
                Disposed = true;
            }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly List<Session> Sessions = new List<Session>();
            internal readonly List<int> Delays = new List<int>();
            internal readonly MainForm Form;
            internal int Notifications;
            internal bool SerialOwnership = true, ExactDevices = true;
            internal Fixture(Func<int, Session> factory = null, bool holdBackoff = false)
            {
                Form = new MainForm(new AppSettings { Language = "en" }, s => { }, () => { }, options =>
                {
                    if (Sessions.Count > 0 && !Sessions.Last().Disposed) SerialOwnership = false;
                    ExactDevices &= options.InputId == "confirmed-mic" && options.OutputId == "confirmed-cable";
                    var session = factory == null ? new Session() : factory(Sessions.Count);
                    Sessions.Add(session);
                    return session;
                }, (milliseconds, token) =>
                {
                    if (milliseconds >= 2000) Delays.Add(milliseconds);
                    return Task.Delay(holdBackoff && milliseconds >= 2000 ? Timeout.Infinite : 1, token);
                }, () => Notifications++);
                var handle = Form.Handle;
                var input = (ComboBox)Field(Form, "input");
                var output = (ComboBox)Field(Form, "output");
                var monitor = (ComboBox)Field(Form, "monitorDevice");
                input.Items.Add(new AudioDevice { Id = "confirmed-mic", Name = "Fixture microphone" });
                output.Items.Add(new AudioDevice { Id = "confirmed-cable", Name = "Fixture cable", Driver = new DriverNode { HardwareIds = new[] { "VBAudioVACWDM" }, Service = "VBAudioVACMME" } });
                monitor.Items.Add(new AudioDevice { Id = "phones", Name = "Fixture headphones" });
                input.SelectedIndex = output.SelectedIndex = monitor.SelectedIndex = 0;
                Await(Call(Form, "ToggleEngine"));
            }
            internal void Fail(AudioFailureKind kind = AudioFailureKind.Slow)
            {
                Sessions.Last().Fault(kind);
                Tick(Form);
            }
            internal void CompleteRecovery()
            {
                Await((Task)Field(Form, "recoveryTask"));
            }
            public void Dispose()
            {
                if (!Form.IsDisposed && (bool)Field(Form, "recovering")) Await(Call(Form, "ToggleEngine"));
                foreach (var session in Sessions) { session.FailCleanup = false; session.Dispose(); }
                Form.Dispose();
            }
        }

        internal static void Run(Action<bool, string> check)
        {
            var policy = new AutomaticRestart();
            int delay;
            check(policy.TryReserve(AudioFailureKind.Slow, out delay) && delay == 2000, "first automatic retry waits 2 seconds");
            check(policy.TryReserve(AudioFailureKind.InputTimeout, out delay) && delay == 5000, "second automatic retry waits 5 seconds");
            check(policy.TryReserve(AudioFailureKind.OutputTimeout, out delay) && delay == 15000, "third automatic retry waits 15 seconds");
            check(!policy.TryReserve(AudioFailureKind.Slow, out delay), "fourth automatic retry is refused");
            policy.ObserveHealthy(0);
            policy.ObserveHealthy(119);
            check(policy.Attempts == 3, "briefly successful restart does not replenish retry budget");
            policy.ObserveHealthy(120);
            check(policy.Attempts == 0, "two uninterrupted healthy minutes replenish retry budget");
            bool rejects = true;
            foreach (AudioFailureKind kind in Enum.GetValues(typeof(AudioFailureKind)))
                if (kind != AudioFailureKind.Slow && kind != AudioFailureKind.InputTimeout && kind != AudioFailureKind.OutputTimeout && kind != AudioFailureKind.ModelTimeout)
                    rejects &= !policy.TryReserve(kind, out delay);
            check(rejects, "permissions, missing devices/models and processing errors cannot trigger automatic retries");
            check(AutomaticRestart.CanRetry(AudioFailureKind.ModelTimeout), "only an identified model watchdog timeout permits model restart");

            using (var fixture = new Fixture())
            {
                Await(Call(fixture.Form, "ToggleMonitor"));
                fixture.Fail();
                fixture.CompleteRecovery();
                check(fixture.Sessions.Count == 2 && fixture.SerialOwnership && fixture.ExactDevices, "real main-form restart path releases old session before reopening exact confirmed devices");
                check(fixture.Sessions.Last().MonitorId == "phones" && (bool)Field(fixture.Form, "monitorWanted"), "successful recovery restores the selected monitor when it was enabled");
                check(fixture.Notifications == 0 && (string)Field(fixture.Form, "statusKey") == "running", "successful recovery avoids an error popup and resumes running state");
                for (int i = 0; i < 3; i++) { fixture.Fail(); fixture.CompleteRecovery(); }
                check(fixture.Sessions.Count == 4 && fixture.Delays.SequenceEqual(new[] { 2000, 5000, 15000 }), "short successive running failures share one three-attempt budget");
                check(fixture.Notifications == 1 && (string)Field(fixture.Form, "statusKey") == "error"
                    && ((Label)Field(fixture.Form, "state")).Text.Contains("Start to retry"), "exhausted retries produce a single notification and offer manual Start");
                Await(Call(fixture.Form, "ToggleEngine"));
                check(fixture.Sessions.Count == 5 && (bool)Field(fixture.Form, "processingWanted"), "explicit manual Start resets the retry budget");
            }
            using (var fixture = new Fixture(index =>
            {
                var session = new Session();
                if (index > 0) session.Fault(AudioFailureKind.OutputTimeout);
                return session;
            }))
            {
                fixture.Fail(AudioFailureKind.ModelTimeout); fixture.CompleteRecovery();
                check(fixture.Sessions.Count == 4 && fixture.Sessions.All(s => s.Disposed) && fixture.Notifications == 1,
                    "repeated failed reopen attempts stop after three and dispose every session");
            }
            using (var fixture = new Fixture(holdBackoff: true))
            {
                fixture.Fail();
                Pump(() => fixture.Delays.Count == 1);
                Await(Call(fixture.Form, "ToggleEngine"));
                check(fixture.Sessions.Count == 1 && fixture.Sessions[0].Disposed && fixture.Notifications == 0
                    && (string)Field(fixture.Form, "statusKey") == "stopped", "Stop cancels backoff and never reopens the microphone");
            }
            using (var fixture = new Fixture(holdBackoff: true))
            {
                fixture.Fail(); Pump(() => fixture.Delays.Count == 1);
                Await(Call(fixture.Form, "ExitApplication"));
                check(fixture.Sessions.Count == 1 && fixture.Sessions[0].Disposed && (bool)Field(fixture.Form, "closing"), "tray Exit cancels pending recovery before completing shutdown");
            }
            using (var fixture = new Fixture())
            {
                fixture.Sessions[0].FailCleanup = true;
                fixture.Fail(); fixture.CompleteRecovery();
                Tick(fixture.Form); Tick(fixture.Form);
                check(fixture.Sessions.Count == 1 && Object.ReferenceEquals(Field(fixture.Form, "engine"), fixture.Sessions[0])
                    && fixture.Notifications == 1, "failed cleanup prevents new session and preserves old session for manual cleanup retry");
                fixture.Sessions[0].FailCleanup = false;
                Await(Call(fixture.Form, "ToggleEngine"));
                check(fixture.Sessions[0].Disposed && !(bool)Field(fixture.Form, "recoveryCleanupFailed"), "manual Reset completes retained cleanup and clears the cleanup-failure latch");
            }
            using (var fixture = new Fixture())
            {
                fixture.Fail(AudioFailureKind.Permission);
                check(fixture.Sessions.Count == 1 && fixture.Delays.Count == 0 && fixture.Notifications == 1,
                    "nonrecoverable failure follows the normal error path without an automatic restart");
            }
            using (var fixture = new Fixture(index => new Session { Current = index == 0 ? new Session().Current : new EngineSnapshot { State = "正在加载本地模型…" } }))
            {
                fixture.Fail(); Pump(() => fixture.Sessions.Count == 2);
                Await(Call(fixture.Form, "ToggleEngine"));
                check(fixture.Sessions.Count == 2 && fixture.Sessions.All(s => s.Disposed) && fixture.Notifications == 0,
                    "Stop during replacement initialization disposes the replacement without another reopen");
            }
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var fixture = new Fixture())
            {
                fixture.Sessions[0].CleanupEntered = entered;
                fixture.Sessions[0].CleanupRelease = release;
                try
                {
                    fixture.Fail(); Pump(() => entered.WaitOne(0));
                    Tick(fixture.Form); Tick(fixture.Form);
                    var stop = Call(fixture.Form, "ToggleEngine");
                    check(fixture.Sessions.Count == 1 && !stop.IsCompleted, "repeated UI ticks and Stop cannot overlap pending cleanup or open another session");
                    release.Set(); Await(stop);
                    check(fixture.Sessions.Count == 1 && fixture.Sessions[0].Disposed, "cancelled cleanup finishes before recovery releases ownership");
                }
                finally { release.Set(); }
            }
            using (var fixture = new Fixture(index =>
            {
                var session = new Session(); if (index > 0) session.Fault(AudioFailureKind.NoMicrophone); return session;
            }))
            {
                fixture.Fail(); fixture.CompleteRecovery();
                check(fixture.Sessions.Count == 2 && fixture.Notifications == 1, "missing original microphone during reopen stops recovery without switching devices");
            }
            using (var fixture = new Fixture(index => new Session { FailMonitor = index > 0 }))
            {
                Await(Call(fixture.Form, "ToggleMonitor")); fixture.Fail(); fixture.CompleteRecovery();
                check(fixture.Sessions.Count == 2 && (string)Field(fixture.Form, "statusKey") == "running"
                    && !(bool)Field(fixture.Form, "monitorWanted") && fixture.Notifications == 0,
                    "monitor restore failure remains isolated from successfully recovered virtual output");
            }
        }
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace KeepClear
{
    internal static class StartupOptimizationTests
    {
        private static EngineSnapshot Flow()
        {
            return new EngineSnapshot
            {
                State = "运行中",
                CaptureFrames = 1,
                ProcessedSamples = 1,
                ProcessedRenderFrames = 1
            };
        }

        internal static void Run(Action<bool, string> check)
        {
            var flow = Flow();
            flow.Underruns = 100;
            flow.Discontinuities = 50;
            flow.ProcessMs = 1500;
            flow.InputQueueMs = 512;
            flow.OutputQueueMs = 768;
            check(AudioStartupTest.HasFlow(flow, 1), "startup accepts a working route regardless of underruns, gaps, timing or queue levels");
            flow = Flow(); flow.CaptureFrames = 0;
            check(!AudioStartupTest.HasFlow(flow, 1), "startup waits until microphone capture actually progresses");
            flow = Flow(); flow.ProcessedSamples = 0;
            check(!AudioStartupTest.HasFlow(flow, 1), "startup waits until the dual-model chain returns samples");
            check(!AudioStartupTest.HasFlow(Flow(), 0), "startup waits for matching VB-CABLE capture callbacks");
            flow = Flow(); flow.Error = "device disconnected";
            check(!AudioStartupTest.HasFlow(flow, 1), "a real stream fault cannot pass merely because old counters are positive");
            check(AudioFailure.Classify("microphone", new COMException("localized OS message", unchecked((int)0x80070005))) == AudioFailureKind.Permission, "permission failure uses HRESULT, not OS message language");
            check(AudioFailure.Classify("microphone", new COMException("occupied", unchecked((int)0x8889000A))) == AudioFailureKind.Busy, "exclusive device use has a distinct error category");
            check(AudioFailure.Classify("microphone", new COMException("gone", unchecked((int)0x88890004))) == AudioFailureKind.Disconnected, "device invalidation has a distinct error category");
            check(AudioFailure.Classify("model-load", new UnauthorizedAccessException()) == AudioFailureKind.ModelLoad, "model file access failure is not mislabeled as microphone permissions");
            foreach (AudioFailureKind kind in Enum.GetValues(typeof(AudioFailureKind)))
                check(AudioFailure.Title(kind, true) != AudioFailure.Title(kind, false) && AudioFailure.Advice(kind, true) != AudioFailure.Advice(kind, false), "bilingual error title and action: " + kind);
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "artifacts", "install-state-tests", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(folder, "state.xml");
            DateTime boot = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);
            var tracking = new DriverInstallState(path, boot);
            int launches = 0;
            var state = VbCableInstaller.Ensure(true, s =>
            {
            }, () => CableState.Incomplete, () =>
            {
                launches++;
                return 0;
            }, 0, tracking);
            check(state == CableState.Pending && launches == 1, "incomplete installation returns pending after one setup attempt");
            tracking = new DriverInstallState(path, boot.AddSeconds(1));
            state = VbCableInstaller.Ensure(true, s =>
            {
            }, () => CableState.Incomplete, () =>
            {
                launches++;
                return 0;
            }, 0, tracking);
            check(state == CableState.Pending && launches == 1, "pending attempt survives process recreation without launching setup again");
            state = VbCableInstaller.Ensure(false, s =>
            {
            }, () => CableState.Incomplete, () =>
            {
                launches++;
                return 0;
            }, 0, tracking);
            check(state == CableState.Pending && launches == 1, "read-only diagnostics preserve pending installation and never launch setup");
            state = VbCableInstaller.Ensure(true, s =>
            {
            }, () => CableState.Ready, () =>
            {
                launches++;
                return 0;
            }, 0, tracking);
            check(state == CableState.Ready && !File.Exists(path), "endpoints becoming ready clear pending state and continue");
            tracking = new DriverInstallState(path, boot);
            tracking.Save(true);
            state = VbCableInstaller.Ensure(true, s =>
            {
            }, () => CableState.Ready, () =>
            {
                launches++;
                return 0;
            }, 0, tracking);
            check(state == CableState.RestartRequired && launches == 1, "explicit setup reboot requirement persists even if endpoints appear before reboot");
            tracking = new DriverInstallState(path, boot.AddHours(1));
            check(!tracking.Pending && !tracking.Restart, "installation guard expires after a different estimated Windows boot");
            tracking.Save(false);
            state = VbCableInstaller.Ensure(true, s =>
            {
            }, () => CableState.Incomplete, () =>
            {
                launches++;
                return 0;
            }, 0, tracking, () => true);
            check(state == CableState.InstallBusy && launches == 1, "a still-running official installer never starts a second setup");
        }
    }
}

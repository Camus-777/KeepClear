using System;
using System.IO;
using System.Runtime.InteropServices;

namespace KeepQuiet
{
    internal static class StartupOptimizationTests
    {
        private static EngineSnapshot Progress(EngineSnapshot previous, bool glitch = false)
        {
            return new EngineSnapshot
            {
                Blocks = previous.Blocks + 156,
                ProcessedSamples = previous.ProcessedSamples + 39936,
                CaptureFrames = previous.CaptureFrames + 120000,
                RenderFrames = previous.RenderFrames + 120000,
                TotalProcessMs = previous.TotalProcessMs + 1000,
                InputQueueMs = 16,
                OutputQueueMs = 48,
                Underruns = previous.Underruns + (glitch ? 1 : 0)
            };
        }

        internal static void Run(Action<bool, string> check)
        {
            var zero = new EngineSnapshot();
            var healthy = Progress(zero);
            var observation = new AudioObservation(zero, 0, 0);
            check(observation.Observe(healthy, 120000, 48000, 2.5) == ObservationResult.Passed && !observation.RecoveryUsed, "healthy silent route completes one observation window");
            observation = new AudioObservation(zero, 0, 0);
            var glitch = Progress(zero, true);
            check(observation.Observe(glitch, 120000, 48000, 2.5) == ObservationResult.Recovering, "one transient underrun enters bounded recovery instead of immediate failure");
            check(observation.Observe(glitch, 120000, 48000, 2.9) == ObservationResult.Observing, "recovery gives the existing route a short settling period");
            observation.Observe(glitch, 120000, 48000, 3.0);
            check(observation.Observe(Progress(glitch), 240000, 48000, 5.5) == ObservationResult.Passed && observation.RecoveryUsed, "stable second window passes without reloading models");
            observation = new AudioObservation(zero, 0, 0);
            observation.Observe(glitch, 120000, 48000, 2.5);
            observation.Observe(glitch, 120000, 48000, 3);
            check(observation.Observe(Progress(glitch, true), 240000, 48000, 5.5) == ObservationResult.Failed, "repeated underrun fails after one recovery window, with no retry loop");
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

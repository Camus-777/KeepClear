using System;

namespace KeepClear
{
    internal static class DriverChecks
    {
        internal static DriverNode Node(string id)
        {
            return new DriverNode
            {
                Id = id,
                HardwareIds = new[]
                {
                    DriverCatalog.Identity.Value.HardwareId
                },
                Service = DriverCatalog.Identity.Value.Service,
                Status = 8,
                Version = "different-but-healthy"
            };
        }

        internal static void Run(Action<bool, string> check)
        {
            check(DriverCatalog.Identity.Value.HardwareId == "VBAudioVACWDM" && DriverCatalog.Identity.Value.Service == "VBAudioVACMME", "identity is read from the pinned bundled INF");
            var node = Node("one");
            var other = Node("two");
            var capture = new AudioDevice
            {
                Driver = node,
                Capture = true,
                State = 1
            };
            var render = new AudioDevice
            {
                Driver = node,
                State = 1
            };
            check(VbCableInstaller.Classify(new[] { node }, new[] { capture, render }) == CableState.Ready, "healthy driver versions are accepted without a downgrade");
            check(VbCableInstaller.Classify(new DriverNode[0], new AudioDevice[0]) == CableState.Missing, "absent PnP nodes are missing regardless of registry remnants");
            check(VbCableInstaller.Classify(new DriverNode[0], new[] { new AudioDevice { IdentityError = "unrelated Bluetooth topology" } }) == CableState.Missing, "unrelated endpoint metadata does not prevent a first installation when no base driver exists");
            check(VbCableInstaller.Classify(new[] { node }, new AudioDevice[0]) == CableState.Incomplete, "residual media node without endpoints is repairable");
            check(VbCableInstaller.Classify(new[] { node }, new[] { capture }) == CableState.Incomplete, "missing render endpoint is repairable");
            render.Driver = other;
            check(VbCableInstaller.Classify(new[] { node, other }, new[] { capture, render }) == CableState.Incomplete, "capture and render must belong to the same adapter");
            render.Driver = node;
            render.State = 2;
            check(VbCableInstaller.Classify(new[] { node }, new[] { capture, render }) == CableState.Disabled, "disabled endpoint is not missing");
            render.State = 1;
            node.Status = 0x400;
            node.Problem = 22;
            check(VbCableInstaller.Classify(new[] { node }, new[] { capture, render }) == CableState.Disabled, "disabled media node is not repaired automatically");
            node.Problem = 14;
            check(VbCableInstaller.Classify(new[] { node }, new[] { capture, render }) == CableState.RestartRequired, "PnP restart-required state is preserved");
            node.Status = 8;
            node.Problem = 0;
            node.Service = "wrong-service";
            check(VbCableInstaller.Classify(new[] { node }, new[] { capture, render }) == CableState.Incomplete, "hardware ID without the matching service is incomplete");
            check(!new AudioDevice { Name = "CABLE Input (VB-Audio Virtual Cable)" }.IsCable, "copied friendly labels alone never establish driver identity");
            check(VbCableInstaller.Classify(new[] { node }, new[] { new AudioDevice { AdapterId = node.Id, IdentityError = "access denied" } }) == CableState.DetectionFailed, "related identity query failure must not launch a reinstall");
            check(VbCableInstaller.Classify(new[] { node }, new[] { new AudioDevice { AdapterId = "unrelated-bluetooth", IdentityError = "access denied" } }) == CableState.Incomplete, "unrelated identity query failure does not prevent repairing an incomplete base driver");
            int reads = 0, launches = 0;
            var state = VbCableInstaller.Ensure(true, s =>
            {
            }, () => ++reads <= 2 ? CableState.Incomplete : CableState.Ready, () =>
            {
                launches++;
                return 0;
            });
            check(state == CableState.Ready && launches == 1, "incomplete driver installs once and continues immediately when endpoints are ready");
            bool failed = false;
            check(VbCableInstaller.Ensure(true, s =>
            {
            }, () => CableState.Incomplete, () => 0) == CableState.Pending, "setup exit zero without ready endpoints remains pending, not a false success");
            reads = 0;
            failed = false;
            try
            {
                VbCableInstaller.Ensure(true, s =>
                {
                }, () => ++reads <= 2 ? CableState.Missing : CableState.Incomplete, () => 5);
            }
            catch (InvalidOperationException)
            {
                failed = true;
            }

            check(failed, "failed setup is not hidden by a residual PnP node");
            var baseline = new EngineSnapshot();
            var current = new EngineSnapshot
            {
                Blocks = 156,
                ProcessedSamples = 39000,
                CaptureFrames = 120000,
                RenderFrames = 120000,
                TotalProcessMs = 500,
                InputQueueMs = 16,
                OutputQueueMs = 48
            };
            check(AudioStartupTest.MeetsThroughput(current, baseline, 2.5), "silent microphone input does not fail audio throughput validation");
            current.Blocks = 5;
            check(!AudioStartupTest.MeetsThroughput(current, baseline, 2.5), "stalled processing cannot pass solely because device streams opened");
            var physical = new AudioDevice
            {
                Id = "default",
                IsDefault = true
            };
            var saved = new AudioDevice
            {
                Id = "saved"
            };
            check(AudioStartupTest.SelectedInput(new[] { physical, saved }, "saved") == saved && AudioStartupTest.SelectedInput(new[] { physical, saved }, "missing") == null, "self-test only opens the microphone explicitly confirmed by the user");
            check(AudioStartupTest.SelectedInput(new[] { new AudioDevice { Id = "unverified", IsDefault = true, IdentityError = "failed metadata" }, saved }, "unverified") == null, "unverified endpoint is not replaced by a different microphone");
        }
    }
}

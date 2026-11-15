using System;
using System.IO;
using System.Linq;
using System.Globalization;

namespace KeepClear
{
    internal static class MaintenanceTests
    {
        internal static void Run(Action<bool, string> check)
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "artifacts", "maintenance", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "install.xml");
            DateTime boot = DateTime.UtcNow;
            int installs = 0;
            foreach (string xml in new[]
            {
                "<install",
                "<install />",
                "<!-- empty -->",
                "<wrong bootUtc=\"2026-11-15T00:00:00Z\" />"
            })
            {
                File.WriteAllText(path, xml);
                var state = new DriverInstallState(path, boot);
                check(!state.Pending && !state.Restart, "malformed driver cache does not block startup: " + xml);
                check(VbCableInstaller.Ensure(true, s =>
                {
                }, () => CableState.Ready, () =>
                {
                    installs++;
                    return 0;
                }, 0, state) == CableState.Ready, "healthy driver remains ready despite malformed installation metadata");
            }

            File.WriteAllText(path, "locked");
            using (var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                check(!new DriverInstallState(path, boot).Pending, "locked installation metadata falls back without aborting detection");
            check(!new DriverInstallState(folder, boot).Pending, "unreadable installation metadata does not abort detection");
            string blocked = Path.Combine(folder, "blocked");
            File.WriteAllText(blocked, "not a directory");
            var unwritable = new DriverInstallState(Path.Combine(blocked, "state.xml"), boot);
            unwritable.Save(false);
            unwritable.Clear();
            check(!unwritable.Pending && VbCableInstaller.Ensure(true, s =>
            {
            }, () => CableState.Ready, () =>
            {
                installs++;
                return 0;
            }, 0, unwritable) == CableState.Ready, "cache write/cleanup failures cannot reject a ready audio driver");
            File.Delete(path);
            var tracking = new DriverInstallState(path, boot);
            var verdict = VbCableInstaller.Ensure(true, s =>
            {
            }, () => CableState.Missing, () =>
            {
                check(!File.Exists(path), "no pending-success record is written before setup actually completes");
                installs++;
                return 0;
            }, 0, tracking);
            check(verdict == CableState.Pending && installs == 1, "completed setup retains a pending observation record");
            tracking = new DriverInstallState(path, boot);
            check(tracking.Pending && !tracking.Unconfirmed, "successful installation record survives application recreation");
            verdict = VbCableInstaller.Ensure(true, s =>
            {
            }, () => CableState.Missing, () =>
            {
                installs++;
                return 0;
            }, 0, tracking);
            check(verdict == CableState.Pending && installs == 1, "confirmed completed setup is not repeated while waiting");
            File.WriteAllText(path, "<install bootUtc=\"" + boot.ToString("o", CultureInfo.InvariantCulture) + "\" archive=\"" + VbCableInstaller.ArchiveHash + "\" restart=\"false\" />");
            tracking = new DriverInstallState(path, boot);
            check(tracking.Unconfirmed && !tracking.Pending, "legacy pre-launch records are distinguished from completed setup");
            verdict = VbCableInstaller.Ensure(true, s =>
            {
            }, () => CableState.Missing, () =>
            {
                installs++;
                return 0;
            }, 0, tracking, () => true);
            check(verdict == CableState.InstallBusy && installs == 1, "legacy record still respects an active installer");
            verdict = VbCableInstaller.Ensure(true, s =>
            {
            }, () => CableState.Missing, () =>
            {
                installs++;
                return 0;
            }, 0, tracking, () => false);
            check(verdict == CableState.Pending && installs == 2, "abandoned legacy pre-launch state permits one repair after bounded observation");
            tracking.Clear();
            bool failed = false;
            try
            {
                VbCableInstaller.Ensure(true, s =>
                {
                }, () => CableState.Missing, () =>
                {
                    throw new System.ComponentModel.Win32Exception(1223);
                }, 0, tracking);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                failed = true;
            }

            check(failed && !File.Exists(path) && !tracking.Pending, "cancelled setup cannot leave a pending-success record");
            var cable = DriverChecks.Node("base");
            var endpoints = NativeAudio.CollectEndpoints(3, i =>
            {
                if (i == 1)
                    throw new IOException("unrelated endpoint disappeared during enumeration");
                return new AudioDevice
                {
                    Id = i.ToString(),
                    Driver = cable,
                    State = 1,
                    Capture = i == 0
                };
            });
            check(endpoints.Count == 2 && VbCableInstaller.Classify(new[] { cable }, endpoints.ToArray()) == CableState.Ready, "one failed endpoint does not discard the remaining verified cable pair");
            var nodes = DriverCatalog.CollectBaseNodes(new[] { "unrelated", "base" }, id =>
            {
                if (id == "unrelated")
                    throw new IOException("unrelated hardware properties unavailable");
                return cable;
            });
            check(VbCableInstaller.Classify(nodes.ToArray(), new AudioDevice[0]) == CableState.Incomplete, "failed unrelated PnP metadata does not prevent repairing a known incomplete cable");
            check(VbCableInstaller.Classify(nodes.ToArray(), endpoints.ToArray()) == CableState.Ready, "failed unrelated PnP metadata cannot reject a complete cable");
            cable.IdentityError = "base service/status query failed";
            check(VbCableInstaller.Classify(new[] { cable }, endpoints.ToArray()) == CableState.DetectionFailed, "known cable identity/status failure remains distinct from a missing driver");
        }
    }
}

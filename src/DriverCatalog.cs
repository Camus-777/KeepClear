using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace KeepQuiet
{
    internal sealed class DriverNode
    {
        internal string Id, Service, Version = "", Inf = "";
        internal string IdentityError;
        internal string[] HardwareIds = new string[0];
        internal uint Status, Problem;
        internal bool Disabled
        {
            get
            {
                return (Status & 0x400) != 0 && Problem == 22;
            }
        }

        internal bool NeedsRestart
        {
            get
            {
                return (Status & 0x100) != 0 || ((Status & 0x400) != 0 && Problem == 14);
            }
        }

        internal bool Started
        {
            get
            {
                return (Status & 8) != 0 && (Status & 0x400) == 0;
            }
        }

        internal bool IsBaseCable
        {
            get
            {
                return HardwareIds.Any(id => String.Equals(id, DriverCatalog.Identity.Value.HardwareId, StringComparison.OrdinalIgnoreCase));
            }
        }

        internal bool ServiceMatches
        {
            get
            {
                return String.Equals(Service, DriverCatalog.Identity.Value.Service, StringComparison.OrdinalIgnoreCase);
            }
        }

        public override string ToString()
        {
            return Id + " hardware=" + String.Join(",", HardwareIds) + " service=" + Service + " version=" + Version + " inf=" + Inf + " status=0x" + Status.ToString("X") + " problem=" + Problem;
        }
    }

    internal sealed class DriverIdentity
    {
        internal string HardwareId, Service;
    }

    internal static class DriverCatalog
    {
        internal static readonly Lazy<DriverIdentity> Identity = new Lazy<DriverIdentity>(ReadIdentity);
        private static DriverIdentity ReadIdentity()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KeepQuiet.VBCable.zip"))
            {
                if (stream == null || VbCableInstaller.Hash(stream) != VbCableInstaller.ArchiveHash)
                    throw new InvalidDataException("Bundled driver archive checksum mismatch.");
                stream.Position = 0;
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                using (var reader = new StreamReader(archive.GetEntry("vbMmeCable64_win10.inf").Open()))
                {
                    string inf = reader.ReadToEnd();
                    var hardware = Regex.Match(inf, "(?im)^\\s*HardwareId\\s*=\\s*\"([^\"]+)\"");
                    var service = Regex.Match(inf, "(?im)^\\s*ServiceName\\s*=\\s*\"([^\"]+)\"");
                    if (!hardware.Success || !service.Success)
                        throw new InvalidDataException("Bundled driver INF identity is incomplete.");
                    return new DriverIdentity
                    {
                        HardwareId = hardware.Groups[1].Value,
                        Service = service.Groups[1].Value
                    };
                }
            }
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
        [DllImport("cfgmgr32.dll", ExactSpelling = true)]
        private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, byte[] buffer, ref uint length, uint flags);
        private static void Check(uint code, string operation)
        {
            if (code != 0)
                throw new InvalidOperationException(operation + " failed (CONFIGRET 0x" + code.ToString("X") + ").");
        }

        private static string Property(uint node, uint property)
        {
            uint size = 0, type;
            uint code = CM_Get_DevNode_Registry_PropertyW(node, property, out type, null, ref size, 0);
            if (code == 0x25)
                return ""; // CR_NO_SUCH_VALUE: normal for a partially installed node.
            if (code != 0 && code != 0x1A)
                Check(code, "Read driver property size");
            var data = new byte[size];
            Check(CM_Get_DevNode_Registry_PropertyW(node, property, out type, data, ref size, 0), "Read driver property");
            return Encoding.Unicode.GetString(data, 0, (int)size).TrimEnd('\0');
        }

        internal static DriverNode Read(string id, bool baseOnly = false)
        {
            var result = new DriverNode
            {
                Id = id
            };
            try
            {
                uint node;
                Check(CM_Locate_DevNodeW(out node, id, 0), "Locate audio adapter");
                result.HardwareIds = Property(node, 2).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
                if (baseOnly && !result.IsBaseCable)
                    return result;
                result.Service = Property(node, 5);
                Check(CM_Get_DevNode_Status(out result.Status, out result.Problem, node, 0), "Read driver status");
                // Version/INF are diagnostic only; failed metadata cannot block a healthy route.
                try
                {
                    string driver = Property(node, 10);
                    if (!String.IsNullOrEmpty(driver))
                    {
                        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driver))
                            if (key != null)
                            {
                                result.Version = Convert.ToString(key.GetValue("DriverVersion"));
                                result.Inf = Convert.ToString(key.GetValue("InfPath"));
                            }
                    }
                }
                catch (Exception ex)
                {
                    Diagnostics.Failure("driver-version", ex);
                }
            }
            catch (Exception ex)
            {
                if (!baseOnly)
                    throw;
                result.IdentityError = ex.Message;
                Diagnostics.Failure("driver-node " + id, ex);
            }

            return result;
        }

        internal static List<DriverNode> CollectBaseNodes(IEnumerable<string> ids, Func<string, DriverNode> read)
        {
            var result = new List<DriverNode>();
            foreach (string id in ids)
                try
                {
                    var node = read(id);
                    if (node.IsBaseCable || !String.IsNullOrEmpty(node.IdentityError))
                        result.Add(node);
                }
                catch (Exception ex)
                {
                    Diagnostics.Failure("driver-node " + id, ex);
                    result.Add(new DriverNode { Id = id, IdentityError = ex.Message });
                }

            return result;
        }

        internal static List<DriverNode> BaseCableNodes()
        {
            const uint flags = 0x200 | 0x100; // Media class, present devnodes only. Registry remnants are not proof of installation.
            const string media = "{4D36E96C-E325-11CE-BFC1-08002BE10318}";
            for (int attempt = 0; attempt < 4; attempt++)
            {
                uint length;
                Check(CM_Get_Device_ID_List_SizeW(out length, media, flags), "Enumerate media size");
                var data = new char[Math.Max(1, (int)length)];
                uint code = CM_Get_Device_ID_ListW(media, data, (uint)data.Length, flags);
                if (code == 0x1A)
                    continue;
                Check(code, "Enumerate media devices");
                return CollectBaseNodes(new string (data).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries), id => Read(id, true));
            }

            throw new InvalidOperationException("Audio devices changed repeatedly during detection. Retry the check.");
        }
    }
}

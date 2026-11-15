using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml;

namespace KeepClear
{
    // Persists a successful/pending setup attempt across application exits, not across a Windows restart.
    internal sealed class DriverInstallState
    {
        private readonly string path;
        private readonly DateTime bootUtc;
        internal bool Pending, Restart, Unconfirmed;
        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();
        internal static DateTime BootUtc
        {
            get
            {
                return DateTime.UtcNow.AddMilliseconds(-(double)GetTickCount64());
            }
        }

        internal DriverInstallState(string path, DateTime bootUtc)
        {
            this.path = path;
            this.bootUtc = bootUtc;
            try
            {
                var document = new XmlDocument
                {
                    XmlResolver = null
                };
                using (var stream = File.OpenRead(path))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    document.Load(reader);
                var root = document.DocumentElement;
                DateTime previous;
                if (root == null || root.Name != "install" || !DateTime.TryParse(root.GetAttribute("bootUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out previous))
                    throw new XmlException("Invalid driver installation state.");
                if (Math.Abs((previous - bootUtc).TotalMinutes) < 2 && root.GetAttribute("archive") == VbCableInstaller.ArchiveHash)
                {
                    Restart = root.GetAttribute("restart") == "true";
                    Pending = Restart || root.GetAttribute("completed") == "true";
                    Unconfirmed = !Pending;
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (XmlException ex)
            {
                Diagnostics.Failure("driver-state-read", ex);
            }
            catch (IOException ex)
            {
                Diagnostics.Failure("driver-state-read", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Diagnostics.Failure("driver-state-read", ex);
            }
            catch (System.Security.SecurityException ex)
            {
                Diagnostics.Failure("driver-state-read", ex);
            }
        }

        internal void Save(bool restart)
        {
            Pending = true;
            Restart = restart;
            Unconfirmed = false;
            try
            {
                SaveCore(restart);
            }
            catch (IOException ex)
            {
                Diagnostics.Failure("driver-state-write", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Diagnostics.Failure("driver-state-write", ex);
            }
            catch (System.Security.SecurityException ex)
            {
                Diagnostics.Failure("driver-state-write", ex);
            }
        }

        private void SaveCore(bool restart)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true }))
            {
                writer.WriteStartElement("install");
                writer.WriteAttributeString("archive", VbCableInstaller.ArchiveHash);
                writer.WriteAttributeString("completed", "true");
                writer.WriteAttributeString("bootUtc", bootUtc.ToString("o", CultureInfo.InvariantCulture));
                writer.WriteAttributeString("restart", restart ? "true" : "false");
                writer.WriteEndElement();
            }

            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }

        internal void Clear()
        {
            Pending = Restart = Unconfirmed = false;
            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                Diagnostics.Failure("driver-state-cleanup", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Diagnostics.Failure("driver-state-cleanup", ex);
            }
            catch (System.Security.SecurityException ex)
            {
                Diagnostics.Failure("driver-state-cleanup", ex);
            }
        }
    }
}

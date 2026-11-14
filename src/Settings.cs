using System;
using System.IO;
using System.Xml.Serialization;

namespace KeepQuiet
{
    public sealed class AppSettings
    {
        public string InputId = "", OutputId = "", Python = "python.exe";
        public string MonitorId = "", Language = "en";
        public int GainDb;
        private static string Folder
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeepQuiet");
            }
        }

        public static string FindPython(string root)
        {
            if (File.Exists(Path.Combine(root, "PORTABLE.json")))
                return Path.Combine(root, "runtime", "python.exe");
            foreach (string path in new[]
            {
                Path.Combine(root, "runtime", "python.exe"),
                Path.Combine(root, ".venv", "Scripts", "python.exe"),
                Path.GetFullPath(Path.Combine(root, "..", ".venv", "Scripts", "python.exe"))
            })
                if (File.Exists(path))
                    return path;
            return "python.exe";
        }

        public static string ResolvePython(string root, string configured)
        {
            string bundled = Path.Combine(root, "runtime", "python.exe");
            if (File.Exists(bundled) || File.Exists(Path.Combine(root, "PORTABLE.json")))
                return bundled;
            return !String.IsNullOrEmpty(configured) && File.Exists(configured) ? configured : FindPython(root);
        }

        private static AppSettings Defaults()
        {
            return new AppSettings
            {
                Python = FindPython(AppDomain.CurrentDomain.BaseDirectory)
            };
        }

        public static AppSettings Load()
        {
            return Load(Path.Combine(Folder, "settings.xml"));
        }

        internal static AppSettings Load(string path)
        {
            if (!File.Exists(path))
                return Defaults();
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var saved = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(stream);
                    if (saved == null)
                        return Defaults();
                    if (saved.Language != "en" && saved.Language != "zh")
                        saved.Language = "en";
                    saved.Python = ResolvePython(AppDomain.CurrentDomain.BaseDirectory, saved.Python);
                    return saved;
                }
            }
            catch (InvalidOperationException)
            {
                return Defaults();
            }
            catch (IOException)
            {
                return Defaults();
            }
            catch (UnauthorizedAccessException)
            {
                return Defaults();
            }
            catch (System.Security.SecurityException)
            {
                return Defaults();
            }
        }

        public void Save()
        {
            Save(Path.Combine(Folder, "settings.xml"));
        }

        internal void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + ".tmp";
            using (var stream = File.Create(temp))
                new XmlSerializer(typeof(AppSettings)).Serialize(stream, this);
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
    }
}

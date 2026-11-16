using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Reflection;
using Microsoft.Win32;

namespace KeepClear
{
    internal static class Diagnostics
    {
        private static readonly object gate = new object();
        internal static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeepClear", "logs");
        private static readonly string file = Path.Combine(Folder, "KeepClear-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Process.GetCurrentProcess().Id + ".log");
        internal static void Write(string stage, string message)
        {
            // Diagnostics must never prevent startup, audio cleanup, or exit. No PCM is logged.
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Folder);
                    File.AppendAllText(file, DateTime.UtcNow.ToString("o") + " [" + stage + "] " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }
        }

        internal static void Failure(string stage, Exception error)
        {
            Write(stage, error.ToString());
        }

        internal static string Code(Exception error)
        {
            return "0x" + error.HResult.ToString("X8");
        }

        internal static void Session()
        {
            try
            {
                string cpu = "unknown";
                using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    if (key != null) cpu = Convert.ToString(key.GetValue("ProcessorNameString"));
                var assembly = Assembly.GetExecutingAssembly();
                Write("session", "version=" + assembly.GetName().Version + " build=" + assembly.ManifestModule.ModuleVersionId + " os=" + Environment.OSVersion + " runtime=" + Environment.Version + " cpu=" + cpu.Trim() + " logical_processors=" + Environment.ProcessorCount);
            }
            catch (Exception ex) { Failure("session-info", ex); }
        }
    }
}

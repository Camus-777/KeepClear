using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace KeepQuiet
{
    internal static class Diagnostics
    {
        private static readonly object gate = new object();
        internal static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeepQuiet", "logs");
        private static readonly string file = Path.Combine(Folder, "KeepQuiet-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Process.GetCurrentProcess().Id + ".log");
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
    }
}

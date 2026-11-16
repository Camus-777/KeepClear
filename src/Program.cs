using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace KeepClear
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Diagnostics.Session();
            if (args.Length == 2 && args[0] == "--self-test")
                return SelfTest.Run(args[1]);
            if ((args.Length == 2 && args[0] == "--check-startup") || ((args.Length == 2 || args.Length == 3) && args[0] == "--check-audio"))
            {
                try
                {
                    var result = StartupCheck.Run(false, delegate (string step)
                    {
                    }, CancellationToken.None, args[0] == "--check-audio", args.Length == 3 ? args[2] : null);
                    File.WriteAllText(args[1], "ready=" + result.Ready + "; status=" + result.Status + "; detail=" + result.Detail + "; input_muted=" + result.InputMuted + "; logs=" + Diagnostics.Folder);
                    return result.Ready ? 0 : 1;
                }
                catch (Exception ex)
                {
                    File.WriteAllText(args[1], ex.ToString());
                    return 1;
                }
            }

            if (args.Length == 2 && args[0] == "--devices")
            {
                try
                {
                    var lines = NativeAudio.Devices(true).Concat(NativeAudio.Devices(false)).Select(d => (d.Capture ? "CAPTURE" : "RENDER") + "\t" + d.Id + "\t" + d.Name + "\tbase_cable=" + d.IsCable + "\t" + (d.Driver == null ? d.IdentityError : d.Driver.ToString()));
                    File.WriteAllLines(args[1], lines.ToArray());
                    return 0;
                }
                catch (Exception ex)
                {
                    File.WriteAllText(args[1], ex.ToString());
                    return 1;
                }
            }

            using (var instance = new SingleInstance())
            {
                if (!instance.IsPrimary)
                {
                    instance.SignalPrimary();
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate (object sender, System.Threading.ThreadExceptionEventArgs e)
                {
                    MessageBox.Show(e.Exception.Message, "KeepClear：界面错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                using (var context = new StartupContext())
                {
                    instance.Listen(context.RestoreWindow);
                    Application.Run(context);
                }

                return 0;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace KeepClear
{
    internal static class DpiTests
    {
        private static IEnumerable<Control> All(Control parent)
        {
            yield return parent;
            foreach (Control child in parent.Controls)
                foreach (var control in All(child)) yield return control;
        }

        // Exercise layout/painters without changing Windows display settings.
        // This does not emulate Windows' complete WM_DPICHANGED message sequence.
        private static void Simulate(DpiFixedForm form, int dpi)
        {
            var field = typeof(Control).GetField("deviceDpi", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new Exception(".NET Framework DPI field unavailable");
            int previous = form.DeviceDpi;
            var fonts = All(form).ToDictionary(c => c, c => c.Font);
            form.MinimumSize = form.MaximumSize = Size.Empty;
            form.Scale(new SizeF((float)dpi / previous, (float)dpi / previous));
            foreach (var control in All(form)) field.SetValue(control, dpi);
            foreach (var pair in fonts)
                pair.Key.Font = new Font(pair.Value.FontFamily, pair.Value.Size * dpi / previous, pair.Value.Style, pair.Value.Unit);
            typeof(DpiFixedForm).GetMethod("FixLayout", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
            Application.DoEvents();
            // Match the form's deferred layout pass after child DPI/handle notifications.
            typeof(DpiFixedForm).GetMethod("FixLayout", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
            Application.DoEvents();
        }

        private static void Verify(DpiFixedForm form, Size logical, int dpi)
        {
            var expected = new Size((int)Math.Round(logical.Width * dpi / 96.0), (int)Math.Round(logical.Height * dpi / 96.0));
            if (form.ClientSize != expected || form.MinimumSize != form.MaximumSize)
                throw new Exception("DPI client/fixed bounds mismatch: " + form.ClientSize + " expected=" + expected);
            var picker = form.Controls.OfType<ComboBox>().FirstOrDefault(c => c.Visible);
            if (picker != null)
            {
                var refresh = form.Controls.OfType<Button>().Single(c => c.Name == "refreshMicrophone");
                if (picker.Width != (int)Math.Round(344 * dpi / 96.0) || picker.Top != refresh.Top || picker.Height != refresh.Height)
                    throw new Exception("DPI selector bounds/alignment mismatch: " + picker.Bounds + " refresh=" + refresh.Bounds);
            }
            foreach (var control in All(form).Where(c => c.Visible && (c is Button || c is ComboBox || c is Label)))
            {
                Rectangle rect = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                // The header's deliberate two-pixel top overhang is inside its padded parent.
                if (!form.ClientRectangle.Contains(rect)) throw new Exception("Clipped DPI control: " + control.Text + " " + rect);
                if (control is Button && TextRenderer.MeasureText(control.Text, control.Font).Width > control.Width - 4)
                    throw new Exception("Clipped DPI button: " + control.Text);
                var combo = control as ComboBox;
                if (combo != null && (combo.ItemHeight < TextRenderer.MeasureText("Ag国", combo.Font).Height || combo.Height < combo.ItemHeight))
                    throw new Exception("DPI selector clips its text: " + combo.Bounds + " item=" + combo.ItemHeight + " font=" + combo.Font);
                var label = control as Label;
                if (label != null && !label.AutoSize && TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, 1000), TextFormatFlags.WordBreak).Height > label.Height)
                    throw new Exception("Clipped DPI label: " + label.Text);
            }
        }

        private static void VerifyTrayText(string directory)
        {
            var report = new List<string>();
            foreach (string text in new[] { "Exit", "退出" })
            foreach (int dpi in new[] { 96, 120, 144, 168, 192, 96 })
            foreach (bool enabled in new[] { true, false })
            using (var menu = new ContextMenuStrip())
            using (var font = new Font("Microsoft YaHei UI", 9 * dpi / 96f, FontStyle.Regular, GraphicsUnit.Point))
            {
                var item = new ToolStripMenuItem(text) { AutoSize = false, Enabled = enabled, Font = font };
                menu.Items.Add(item);
                item.Padding = new Padding(8 * dpi / 96, 4 * dpi / 96, 8 * dpi / 96, 4 * dpi / 96);
                item.Size = new Size(96 * dpi / 96, 36 * dpi / 96);
                using (var bitmap = new Bitmap(item.Width, item.Height))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.White);
                    // Deliberately supply stale top-left bounds to reproduce the reported symptom.
                    var args = new ToolStripItemTextRenderEventArgs(graphics, item, text,
                        new Rectangle(0, 0, 10, 10), Color.Black, font, TextFormatFlags.Left | TextFormatFlags.Top);
                    new TrayMenuRenderer().DrawItemText(args);
                    int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
                    for (int y = 0; y < bitmap.Height; y++)
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        Color pixel = bitmap.GetPixel(x, y);
                        if (pixel.R >= 220 && pixel.G >= 220 && pixel.B >= 220) continue;
                        left = Math.Min(left, x); right = Math.Max(right, x);
                        top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    }
                    if (right < left || Math.Abs((left + right + 1) / 2.0 - bitmap.Width / 2.0) > 4 * dpi / 96.0
                        || Math.Abs((top + bottom + 1) / 2.0 - bitmap.Height / 2.0) > 4 * dpi / 96.0
                        || left < item.Padding.Left || top < item.Padding.Top
                        || right >= bitmap.Width - item.Padding.Right || bottom >= bitmap.Height - item.Padding.Bottom)
                        throw new Exception("Tray text not centered or clipped: " + text + " dpi=" + dpi);
                    string name = (text == "Exit" ? "en" : "zh") + "-tray-text-" + dpi + "-" + enabled;
                    bitmap.Save(Path.Combine(directory, name + ".png"));
                    report.Add("PASS " + name + " ink=" + Rectangle.FromLTRB(left, top, right + 1, bottom + 1));
                }
            }
            File.WriteAllLines(Path.Combine(directory, "tray-text-layout.txt"), report);
        }

        internal static void Run(string directory)
        {
            Directory.CreateDirectory(directory);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            VerifyTrayText(directory);
            var report = new List<string>();
            foreach (bool english in new[] { false, true })
            foreach (string mode in new[] { "main", "auto-restart", "select-microphone", "restart-required", "driver-pending", "audio-failed" })
            using (DpiFixedForm form = mode == "main" || mode == "auto-restart"
                ? (DpiFixedForm)new MainForm(new AppSettings { Language = english ? "en" : "zh" }, s => { }, () => { })
                : new StartupForm((p, t) => new StartupResult { Status = mode, ChooseMicrophone = mode == "select-microphone" || mode == "audio-failed", FailureKind = mode == "audio-failed" ? AudioFailureKind.Slow : AudioFailureKind.None, CanRetry = true }, english,
                    () => false, () => { throw new Exception("Unexpected restart"); }, s => { },
                    (id, p, t) => { throw new Exception("Unexpected audio check"); },
                    () => new[] { new AudioDevice { Id = "fixture", Name = "Microphone (DPI fixture)" } }, () => "fixture", s => { }))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.Show();
                bool mainMode = mode == "main" || mode == "auto-restart";
                CancellationTokenSource recoverySource = null;
                if (mode == "auto-restart")
                {
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    recoverySource = new CancellationTokenSource();
                    typeof(MainForm).GetField("recoveryCancellation", flags).SetValue(form, recoverySource);
                    typeof(MainForm).GetField("recovering", flags).SetValue(form, true);
                    typeof(MainForm).GetField("busy", flags).SetValue(form, true);
                    int delay;
                    ((AutomaticRestart)typeof(MainForm).GetField("automaticRestart", flags).GetValue(form)).TryReserve(AudioFailureKind.Slow, out delay);
                    typeof(MainForm).GetMethod("UpdateControls", flags).Invoke(form, null);
                }
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (!mainMode && ((StartupForm)form).Result == null && wait.ElapsedMilliseconds < 1500)
                { Application.DoEvents(); Thread.Sleep(10); }
                if (!mainMode)
                {
                    typeof(StartupForm).GetField("typingDuration", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, 0.001);
                    Thread.Sleep(350);
                    Application.DoEvents();
                }
                foreach (int dpi in new[] { 96, 120, 144, 168, 192, 96 })
                {
                    string popupMetrics = "";
                    Simulate(form, dpi);
                    Verify(form, new Size(480, mainMode ? 456 : 280), dpi);
                    if (mainMode)
                    {
                        var main = (MainForm)form;
                        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                        var input = (ComboBox)typeof(MainForm).GetField("input", flags).GetValue(main);
                        var refresh = (Button)typeof(MainForm).GetField("refresh", flags).GetValue(main);
                        if (input.Height != refresh.Height) throw new Exception("Main DPI refresh height is not aligned");
                        var menu = (ContextMenuStrip)typeof(MainForm).GetField("trayMenu", flags).GetValue(main);
                        var item = (ToolStripMenuItem)typeof(MainForm).GetField("trayExit", flags).GetValue(main);
                        var apply = typeof(MainForm).GetMethod("UpdateTrayMetrics", flags);
                        using (var oversized = new Font(main.Font.FontFamily, 40))
                        {
                            for (int open = 0; open < 3; open++)
                            {
                                menu.Font = item.Font = oversized;
                                apply.Invoke(main, null);
                                if (Math.Abs(menu.Font.SizeInPoints - 9) > .01 || Math.Abs(item.Font.SizeInPoints - 9) > .01)
                                    throw new Exception("Tray menu font grew after DPI changes or repeated openings");
                            }
                        }
                        // Render our own popup control, without a screen capture.
                        menu.Show(new Point(-30000, -30000));
                        Application.DoEvents();
                        if (item.Height < TextRenderer.MeasureText(item.Text, item.Font).Height || item.Height > 80)
                            throw new Exception("Native tray menu item is clipped or excessively tall: " + item.Size);
                        popupMetrics = " native_tray_dpi=" + menu.DeviceDpi + " tray_font_pt=" + item.Font.SizeInPoints + " tray_item=" + item.Size;
                        using (var popup = new Bitmap(menu.Width, menu.Height))
                        {
                            menu.DrawToBitmap(popup, new Rectangle(Point.Empty, popup.Size));
                            popup.Save(Path.Combine(directory, (english ? "en" : "zh") + "-tray-" + dpi + ".png"));
                        }
                        menu.Close();
                    }
                    string name = (english ? "en" : "zh") + "-" + mode + "-" + dpi;
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(Path.Combine(directory, name + ".png"));
                    }
                    report.Add("PASS " + name + " " + form.ClientSize + popupMetrics);
                }
                if (recoverySource != null)
                {
                    typeof(MainForm).GetField("recovering", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, false);
                    typeof(MainForm).GetField("recoveryCancellation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, null);
                    recoverySource.Dispose();
                }
            }
            File.WriteAllLines(Path.Combine(directory, "dpi-layout.txt"), report);
            Console.WriteLine("PASS: " + report.Count + " simulated DPI/language/state layouts; no display settings, audio streams or real preferences changed.");
        }
    }
}

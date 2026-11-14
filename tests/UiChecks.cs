using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KeepQuiet
{
    internal static class UiChecks
    {
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        private static object Field(MainForm form, string name)
        {
            return typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        }

        private static void Set(MainForm form, string name, object value)
        {
            typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, value);
        }

        private static void Call(MainForm form, string name)
        {
            typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
        }

        private static IEnumerable<Control> Descendants(Control control)
        {
            foreach (Control child in control.Controls)
            {
                yield return child;
                foreach (var item in Descendants(child))
                    yield return item;
            }
        }

        internal static void CheckSelectorColors(ComboBox combo)
        {
            if (combo.DrawMode != DrawMode.OwnerDrawFixed)
                throw new Exception("Device selector still uses the system selection fill");
            var paint = typeof(DeviceComboBox).GetMethod("OnDrawItem", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var flags in new[]
            {
                DrawItemState.Selected | DrawItemState.Focus | DrawItemState.ComboBoxEdit,
                DrawItemState.Selected | DrawItemState.HotLight | DrawItemState.Focus
            })
                using (var bitmap = new Bitmap(320, 32))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    paint.Invoke(combo, new object[] { new DrawItemEventArgs(graphics, combo.Font, new Rectangle(0, 0, 320, 32), 0, flags) });
                    Color expected = (flags & DrawItemState.ComboBoxEdit) != 0 ? Color.White : SystemColors.Highlight;
                    if (bitmap.GetPixel(300, 16).ToArgb() != expected.ToArgb())
                        throw new Exception("Device selector has the wrong closed/list highlight color");
                    for (int x = 0; x < bitmap.Width; x++)
                        if (bitmap.GetPixel(x, 0).ToArgb() != expected.ToArgb() || bitmap.GetPixel(x, bitmap.Height - 1).ToArgb() != expected.ToArgb())
                            throw new Exception("Device selector still draws a dotted focus rectangle");
                }

            if (combo.Items.Count == 0)
                return;
            combo.Focus();
            Application.DoEvents();
            using (var bitmap = new Bitmap(combo.Width, combo.Height))
            {
                combo.DrawToBitmap(bitmap, combo.ClientRectangle);
                int blue = 0;
                for (int y = 4; y < bitmap.Height - 4; y++)
                    for (int x = 4; x < bitmap.Width - 25; x++)
                        if (bitmap.GetPixel(x, y).ToArgb() == SystemColors.Highlight.ToArgb())
                            blue++;
                if (blue > 16)
                    throw new Exception("Focused collapsed selector still has a blue selection block");
            }
        }

        internal static void Run(string directory)
        {
            Directory.CreateDirectory(directory);
            var report = new List<string>();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            foreach (bool english in new[]
            {
                false,
                true
            })
                using (var form = new MainForm(new AppSettings { Language = english ? "en" : "zh" }))
                {
                    // Render only our own controls; no desktop/screen capture session.
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-30000, -30000);
                    form.Show();
                    Application.DoEvents();
                    form.CreateControl();
                    var handle = form.Handle;
                    foreach (Control child in Descendants(form))
                    {
                        child.CreateControl();
                        var childHandle = child.Handle;
                    }

                    form.PerformLayout();
                    Call(form, "RefreshDevices");
                    Size fixedSize = form.Size;
                    form.Size = new Size(fixedSize.Width + 100, fixedSize.Height + 100);
                    if (form.Size != fixedSize || form.MinimumSize != fixedSize || form.MaximumSize != fixedSize || form.MaximizeBox)
                        throw new Exception("Window is not fixed at its current size");
                    report.Add("devices=" + String.Join(",", Descendants(form).OfType<ComboBox>().Select(c => c.Items.Count.ToString()).ToArray()));
                    var buttons = Descendants(form).OfType<Button>().ToArray();
                    if (buttons.Length != 4 || Descendants(form).OfType<ComboBox>().Count() != 3)
                        throw new Exception("Unexpected product controls");
                    foreach (var combo in Descendants(form).OfType<ComboBox>())
                    {
                        if (combo.DropDownWidth != combo.Width)
                            throw new Exception("Dropdown extends beyond device selector");
                        var fixture = new AudioDevice
                        {
                            Id = "wheel-test",
                            Name = "Wheel test device"
                        };
                        combo.Items.Add(fixture);
                        int selected = combo.SelectedIndex;
                        foreach (int message in new[]
                        {
                            0x020A,
                            0x020E
                        })
                            foreach (int delta in new[]
                            {
                                -120,
                                120
                            })
                                SendMessage(combo.Handle, message, new IntPtr(delta << 16), IntPtr.Zero);
                        if (combo.SelectedIndex != selected)
                            throw new Exception("Wheel changed an audio device");
                        combo.SelectedItem = fixture;
                        if (combo.SelectedItem != fixture)
                            throw new Exception("Explicit device selection no longer works");
                        CheckSelectorColors(combo);
                        combo.SelectedIndex = selected;
                        combo.Items.Remove(fixture);
                    }

                    report.Add("all device selectors ignore vertical/horizontal wheel; explicit selection remains available");
                    var hover = (ToolTip)Field(form, "errorDetails");
                    if (Descendants(form).OfType<Label>().Any(label => label.AutoEllipsis || !String.IsNullOrEmpty(hover.GetToolTip(label))))
                        throw new Exception("Ordinary main-window text has a hover popup");
                    report.Add("closed focused selectors are white; expanded highlighted rows retain system blue; neither state draws a dotted focus rectangle; ordinary labels have no hover popup");
                    var inputBox = (ComboBox)Field(form, "input");
                    var refreshButton = (Button)Field(form, "refresh");
                    Rectangle inputBounds = inputBox.RectangleToScreen(inputBox.ClientRectangle), refreshBounds = refreshButton.RectangleToScreen(refreshButton.ClientRectangle);
                    if (inputBounds.Top != refreshBounds.Top || inputBounds.Bottom != refreshBounds.Bottom)
                        throw new Exception("Refresh is not aligned with input selector");
                    report.Add("dropdown widths match selectors; refresh top/bottom aligned with input");
                    if (Descendants(form).Any(c => c is CheckBox || c is TextBox || c is TrackBar))
                        throw new Exception("Debug/model controls remain");
                    foreach (var control in Descendants(form).Where(c => c is Button || c is ComboBox || c is Label || c is Meter))
                    {
                        Rectangle bounds = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                        if (!form.ClientRectangle.Contains(bounds))
                            throw new Exception("Clipped " + control.GetType().Name + ": " + control.Text + " " + bounds);
                        if (control is Label && !((Label)control).AutoSize && TextRenderer.MeasureText(control.Text, control.Font, new Size(control.Width, 1000), TextFormatFlags.WordBreak).Height > control.Height)
                            throw new Exception("Text does not fit: " + control.Text);
                    }

                    if (((Button)Field(form, "start")).Text != (english ? "Start" : "启动"))
                        throw new Exception("Language mismatch");
                    Set(form, "english", !english);
                    Call(form, "ApplyLanguage");
                    if (((Button)Field(form, "start")).Text != (english ? "启动" : "Start"))
                        throw new Exception("Live language switch failed");
                    Set(form, "english", english);
                    Call(form, "ApplyLanguage");
                    var listenButton = (Button)Field(form, "listen");
                    if (listenButton.Enabled)
                        throw new Exception("Monitor enabled before processing starts");
                    var ignored = (System.Threading.Tasks.Task)typeof(MainForm).GetMethod("ToggleMonitor", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                    ignored.GetAwaiter().GetResult();
                    if ((bool)Field(form, "monitorWanted"))
                        throw new Exception("Monitor could be armed while stopped");
                    using (var lifecycle = new AudioEngine(new EngineOptions()))
                    {
                        Set(form, "engine", lifecycle);
                        foreach (string engineState in new[]
                        {
                            "准备中",
                            "正在加载本地模型…",
                            "运行中",
                            "已停止：发生错误",
                            "已停止"
                        })
                        {
                            typeof(AudioEngine).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(lifecycle, engineState);
                            Call(form, "UpdateControls");
                            if (listenButton.Enabled != (engineState == "运行中" && ((ComboBox)Field(form, "monitorDevice")).SelectedItem != null))
                                throw new Exception("Incorrect monitor availability: " + engineState);
                        }

                        Set(form, "engine", null);
                    }

                    Set(form, "monitorWanted", true);
                    Call(form, "UpdateControls");
                    if (((Label)Field(form, "hint")).Text.Contains(english ? "feedback" : "啸叫"))
                        throw new Exception("Removed monitoring hint remains");
                    Set(form, "monitorWanted", false);
                    Call(form, "UpdateControls");
                    report.Add("monitor disabled before start/loading/error/stopped; available only while running; removed hint absent");
                    form.ApplyStartupResult(new StartupResult { Ready = true, InputMuted = true });
                    var mutedHint = (Label)Field(form, "hint");
                    if (!mutedHint.Text.Contains(english ? "muted" : "静音") || TextRenderer.MeasureText(mutedHint.Text, mutedHint.Font, new Size(mutedHint.Width, 1000), TextFormatFlags.WordBreak).Height > mutedHint.Height)
                        throw new Exception("Startup mute notice is missing or clipped");
                    Set(form, "noticeKey", "");
                    Call(form, "UpdateControls");
                    form.Close();
                    Application.DoEvents();
                    if (form.Visible || form.IsDisposed || form.ShowInTaskbar || !((NotifyIcon)Field(form, "tray")).Visible)
                        throw new Exception("Closing did not hide to the system tray");
                    string instanceKey = "KeepQuiet.UiTest." + Guid.NewGuid().ToString("N");
                    string activationReport = Path.Combine(directory, (english ? "en" : "zh") + "-secondary.txt");
                    using (var instance = new SingleInstance(instanceKey))
                    {
                        instance.Listen(form, form.RestoreWindow);
                        var childInfo = new ProcessStartInfo(Application.ExecutablePath, "--instance-secondary " + instanceKey + " \"" + Path.GetFullPath(activationReport) + "\"")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using (var child = Process.Start(childInfo))
                        {
                            if (!child.WaitForExit(4000))
                            {
                                child.Kill();
                                throw new Exception("Secondary activation timed out");
                            }

                            if (child.ExitCode != 0)
                                throw new Exception("Secondary GUI instance was not rejected");
                        }

                        PumpUntil(() => form.Visible, 3000);
                    }

                    form.Close();
                    Application.DoEvents();
                    var tray = (NotifyIcon)Field(form, "tray");
                    var trayMenu = (ContextMenuStrip)Field(form, "trayMenu");
                    if (trayMenu.Items.Count != 1 || trayMenu.Items[0] != (ToolStripMenuItem)Field(form, "trayExit"))
                        throw new Exception("Tray menu must contain only Exit");
                    var click = typeof(NotifyIcon).GetMethod("OnMouseClick", BindingFlags.Instance | BindingFlags.NonPublic);
                    click.Invoke(tray, new object[] { new MouseEventArgs(MouseButtons.Right, 1, 0, 0, 0) });
                    Application.DoEvents();
                    if (form.Visible)
                        throw new Exception("Right tray click restored the window");
                    click.Invoke(tray, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0) });
                    Application.DoEvents();
                    if (!form.Visible || !form.ShowInTaskbar || form.IsDisposed)
                        throw new Exception("Single left tray click did not restore the window");
                    form.WindowState = FormWindowState.Minimized;
                    click.Invoke(tray, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0) });
                    Application.DoEvents();
                    if (form.WindowState != FormWindowState.Normal || !form.Visible)
                        throw new Exception("Single tray click did not restore a minimized window");
                    if (((ToolStripMenuItem)Field(form, "trayExit")).Text != (english ? "Exit" : "退出"))
                        throw new Exception("Tray language mismatch");
                    report.Add("fixed window size; close hides without exit; duplicate process restores hidden main form; single left tray click restores hidden/minimized window; right click does not restore; tray menu contains only localized Exit");
                    foreach (string mode in new[]
                    {
                        "ready",
                        "running",
                        "monitor",
                        "error"
                    })
                    {
                        Set(form, "statusKey", mode == "monitor" ? "running" : mode);
                        Set(form, "noticeKey", mode == "monitor" ? "monitor" : "");
                        Set(form, "monitorWanted", mode == "monitor");
                        Call(form, "UpdateControls");
                        foreach (var label in Descendants(form).OfType<Label>().Where(l => !l.AutoSize))
                            if (TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, 1000), TextFormatFlags.WordBreak).Height > label.Height)
                                throw new Exception("State text does not fit (" + mode + "): " + label.Text);
                        ((Meter)Field(form, "inputMeter")).Advance(mode == "running" ? 0.35 : 0, 1);
                        ((Meter)Field(form, "outputMeter")).Advance(mode == "running" ? 0.19 : 0, 1);
                        using (var bitmap = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                            bitmap.Save(Path.Combine(directory, (english ? "en" : "zh") + "-" + mode + ".png"));
                        }
                    }

                    report.Add((english ? "en" : "zh") + ": four buttons, three devices, compact bounds, label fit, live language switch; client=" + form.ClientSize);
                    ((ToolStripMenuItem)Field(form, "trayExit")).PerformClick();
                    Application.DoEvents();
                    if (!form.IsDisposed)
                        throw new Exception("Tray exit did not dispose the window");
                // A test render must not save or alter the real user's preferences.
                }

            SaveFailures(report);
            Maintenance(report);
            File.WriteAllLines(Path.Combine(directory, "report.txt"), report.ToArray());
            Console.WriteLine(String.Join(Environment.NewLine, report));
        }

        private static void Maintenance(List<string> report)
        {
            var click = typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (bool english in new[]
            {
                false,
                true
            })
            {
                int opened = 0;
                var preferences = new AppSettings
                {
                    Language = english ? "en" : "zh",
                    InputId = "missing-confirmed-device"
                };
                using (var form = new MainForm(preferences, null, () => opened++))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-30000, -30000);
                    form.Show();
                    Application.DoEvents();
                    var input = (ComboBox)Field(form, "input");
                    var hint = (Label)Field(form, "hint");
                    for (int pass = 0; pass < 2; pass++)
                    {
                        Call(form, "RefreshDevices");
                        if (input.SelectedIndex != -1 || ((Button)Field(form, "start")).Enabled || (string)Field(form, "noticeKey") != "devices")
                            throw new Exception("Refresh silently replaced a missing confirmed microphone");
                    }

                    // A new explicit selection must survive the next refresh.
                    if (input.Items.Count > 0)
                    {
                        input.SelectedIndex = 0;
                        string id = ((AudioDevice)input.SelectedItem).Id;
                        Call(form, "RefreshDevices");
                        if (input.SelectedItem == null || ((AudioDevice)input.SelectedItem).Id != id)
                            throw new Exception("Refresh lost an explicit replacement microphone");
                    }

                    click.Invoke(hint, new object[] { EventArgs.Empty });
                    if (opened != 0 || hint.Cursor != Cursors.Default)
                        throw new Exception("Ordinary instructions opened diagnostics");
                    var hover = (ToolTip)Field(form, "errorDetails");
                    foreach (AudioFailureKind kind in Enum.GetValues(typeof(AudioFailureKind)))
                    {
                        if (kind == AudioFailureKind.None)
                            continue;
                        Set(form, "statusKey", "error");
                        Set(form, "lastError", "fixture error detail");
                        Set(form, "lastFailureKind", kind);
                        Call(form, "UpdateControls");
                        if (TextRenderer.MeasureText(hint.Text, hint.Font, new Size(hint.Width, 1000), TextFormatFlags.WordBreak).Height > hint.Height)
                            throw new Exception("Localized main error does not fit: " + kind + " english=" + english);
                        int before = opened;
                        click.Invoke(hint, new object[] { EventArgs.Empty });
                        if (opened != before + 1 || hint.Cursor != Cursors.Hand || !hover.GetToolTip(hint).Contains("fixture error detail"))
                            throw new Exception("Error hint did not open diagnostics or preserve hover detail");
                    }

                    Set(form, "statusKey", "ready");
                    Set(form, "lastError", "");
                    Call(form, "UpdateControls");
                    int previous = opened;
                    click.Invoke(hint, new object[] { EventArgs.Empty });
                    if (opened != previous || hint.Cursor != Cursors.Default || !String.IsNullOrEmpty(hover.GetToolTip(hint)))
                        throw new Exception("Normal instructions retained an error action or tooltip");
                }
            }

            using (var form = new MainForm(new AppSettings()))
            using (var engine = new AudioEngine(new EngineOptions()))
            using (var release = new ManualResetEvent(false))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.Show();
                Application.DoEvents();
                form.Close();
                Application.DoEvents();
                var blocked = new Thread(() => release.WaitOne())
                {
                    IsBackground = true,
                    Name = "simulated system-exit capture stall"
                };
                typeof(AudioEngine).GetField("captureThread", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine, blocked);
                Set(form, "engine", engine);
                blocked.Start();
                var closing = typeof(Form).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic);
                try
                {
                    var args = new FormClosingEventArgs(CloseReason.WindowsShutDown, false);
                    closing.Invoke(form, new object[] { args });
                    Application.DoEvents();
                    if (!args.Cancel || (bool)Field(form, "closing") || !form.Visible || !((NotifyIcon)Field(form, "tray")).Visible || Field(form, "engine") != engine)
                        throw new Exception("System close lost access to an engine with unfinished cleanup");
                    release.Set();
                    blocked.Join();
                    args = new FormClosingEventArgs(CloseReason.WindowsShutDown, false);
                    closing.Invoke(form, new object[] { args });
                    Application.DoEvents();
                    if (args.Cancel || !(bool)Field(form, "closing") || ((NotifyIcon)Field(form, "tray")).Visible || Field(form, "engine") != null)
                        throw new Exception("A second system close did not finish audio cleanup");
                }
                finally
                {
                    release.Set();
                    blocked.Join(1000);
                }
            }

            using (var form = new MainForm(new AppSettings(), s =>
            {
                throw new System.Security.SecurityException("isolated save denial");
            }))
            {
                var args = new FormClosingEventArgs(CloseReason.TaskManagerClosing, false);
                typeof(Form).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { args });
                if (args.Cancel || !(bool)Field(form, "closing"))
                    throw new Exception("Preference error prevented system exit");
            }

            report.Add("PASS: missing microphone never falls back across refreshes; all localized error hints fit and open injected log action; normal hints have no action/tooltip; simulated system close preserves failed cleanup and retries successfully; preference errors do not escape shutdown; no OS restart or real settings writes");
        }

        private static void SaveFailures(System.Collections.Generic.List<string> report)
        {
            foreach (bool english in new[]
            {
                false,
                true
            })
            {
                int saves = 0;
                string requested = "";
                using (var form = new MainForm(new AppSettings { Language = english ? "en" : "zh" }, preferences =>
                {
                    saves++;
                    requested = preferences.Language;
                    if (english)
                        throw new UnauthorizedAccessException("simulated settings permission denial");
                    throw new IOException("simulated settings write failure");
                }))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-30000, -30000);
                    form.Show();
                    Application.DoEvents();
                    ((Button)Field(form, "language")).PerformClick();
                    var hint = (Label)Field(form, "hint");
                    if (saves != 1 || requested != (english ? "zh" : "en") || !hint.Text.Contains(english ? "设置未能保存" : "Could not save settings"))
                        throw new Exception("Language save failure was not immediately visible in the selected language");
                    Call(form, "TickUi");
                    if (!hint.Text.Contains(english ? "设置未能保存" : "Could not save settings"))
                        throw new Exception("Idle UI lost the settings failure notice");
                    ((ToolStripMenuItem)Field(form, "trayExit")).PerformClick();
                    Application.DoEvents();
                    if (!form.IsDisposed)
                        throw new Exception("Settings save failure prevented a clean tray exit");
                }
            }

            report.Add("I/O and permission errors saving a language choice immediately show the localized hint while idle; exit remains available; real preferences untouched");
        }

        private static void PumpUntil(Func<bool> complete, int timeout)
        {
            var watch = Stopwatch.StartNew();
            while (!complete() && watch.ElapsedMilliseconds < timeout)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }

            if (!complete())
                throw new Exception("UI action timed out");
        }

        internal static void TrayHardware(string path)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var source = NativeAudio.Devices(true).First(d => !d.IsCable);
            var output = NativeAudio.Devices(false).First(d => d.IsCable);
            var options = new EngineOptions
            {
                InputId = source.Id,
                OutputId = output.Id,
                Root = AppDomain.CurrentDomain.BaseDirectory,
                Python = AppSettings.FindPython(AppDomain.CurrentDomain.BaseDirectory),
                Alkove = true,
                Denoiser = true
            };
            using (var engine = new AudioEngine(options))
            using (var form = new MainForm(new AppSettings()))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.Show();
                Set(form, "engine", engine);
                engine.Start();
                PumpUntil(() => engine.Snapshot().State == "运行中" || !String.IsNullOrEmpty(engine.Snapshot().Error), 20000);
                if (!String.IsNullOrEmpty(engine.Snapshot().Error))
                    throw new Exception(engine.Snapshot().Error);
                long before = engine.Snapshot().Blocks;
                form.Close();
                Application.DoEvents();
                if (form.Visible || form.IsDisposed || Field(form, "engine") != engine)
                    throw new Exception("Close stopped/disposed the live route");
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 2000)
                {
                    Application.DoEvents();
                    Thread.Sleep(10);
                }

                var hidden = engine.Snapshot();
                if (hidden.Blocks <= before + 50 || !String.IsNullOrEmpty(hidden.Error))
                    throw new Exception("Audio did not continue in tray");
                form.RestoreWindow();
                Application.DoEvents();
                if (!form.Visible || Field(form, "engine") != engine)
                    throw new Exception("Restore replaced the audio engine");
                // Exit requested while async stop is in progress must be queued, not lost.
                var stopping = (Task)typeof(MainForm).GetMethod("ToggleEngine", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                ((ToolStripMenuItem)Field(form, "trayExit")).PerformClick();
                PumpUntil(() => stopping.IsCompleted && form.IsDisposed, 10000);
                stopping.GetAwaiter().GetResult();
                var stopped = engine.Snapshot();
                long stoppedBlocks = stopped.Blocks;
                Thread.Sleep(150);
                if (stopped.State != "已停止" || engine.Snapshot().Blocks != stoppedBlocks)
                    throw new Exception("Tray exit left live audio running");
                string report = String.Format("PASS; blocks_before_hide={0}; blocks_while_hidden={1}; underruns={2}; close_keeps_audio=True; restore_reuses_engine=True; queued_exit_completed=True; final_state={3}", before, hidden.Blocks, hidden.Underruns, stopped.State);
                File.WriteAllText(path, report);
                Console.WriteLine(report);
            }
        }
    }
}

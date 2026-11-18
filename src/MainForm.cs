using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KeepClear
{
    internal static class Palette
    {
        public static readonly Color Paper = Color.White, Back = Color.FromArgb(244, 247, 251), Ink = Color.FromArgb(24, 44, 58), Muted = Color.FromArgb(87, 108, 124), Blue = Color.FromArgb(22, 102, 197), Line = Color.FromArgb(214, 224, 234);
    }

    internal sealed class TrayMenuRenderer : ToolStripSystemRenderer
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // Framework text bounds can lag behind the popup's DPI layout.
            // Paint against the current item bounds, including its scaled padding.
            Padding padding = e.Item.Padding;
            e.TextRectangle = new Rectangle(padding.Left, padding.Top,
                Math.Max(1, e.Item.Width - padding.Horizontal),
                Math.Max(1, e.Item.Height - padding.Vertical));
            e.TextFormat = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            base.OnRenderItemText(e);
        }
    }

    internal sealed class Meter : Control
    {
        private readonly MeterEnvelope envelope = new MeterEnvelope();
        public Meter()
        {
            DoubleBuffered = true;
            AccessibleRole = AccessibleRole.ProgressBar;
            TabStop = false;
        }

        public void Advance(double peak, double seconds)
        {
            envelope.Advance(peak, seconds);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            float scale = DeviceDpi / 96f;
            int trackWidth = Math.Max(1, Width - (int)(65 * scale));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var rail = new Pen(Palette.Line, 6 * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            })
                e.Graphics.DrawLine(rail, 4 * scale, 14 * scale, trackWidth, 14 * scale);
            float end = (float)(4 * scale + (trackWidth - 4 * scale) * (envelope.Db + 60) / 60);
            if (end > 4.1 * scale)
                using (var fill = new Pen(envelope.Db > -1 ? Color.FromArgb(180, 69, 45) : Palette.Blue, 6 * scale)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                })
                    e.Graphics.DrawLine(fill, 4 * scale, 14 * scale, end, 14 * scale);
            TextRenderer.DrawText(e.Graphics, envelope.Db < -59.5 ? "−∞" : envelope.Db.ToString("F0") + " dB", Font, new Rectangle(trackWidth + (int)(6 * scale), (int)(3 * scale), (int)(59 * scale), (int)(24 * scale)), Palette.Muted, TextFormatFlags.Right | TextFormatFlags.NoPadding);
            AccessibleDescription = envelope.Db.ToString("F0") + " dBFS";
        }
    }

    internal sealed class DeviceComboBox : ComboBox
    {
        internal void ApplyDpiMetrics()
        {
            int height = Math.Max((int)Math.Ceiling(24 * DeviceDpi / 96.0), Font.Height + (int)Math.Ceiling(4 * DeviceDpi / 96.0));
            if (ItemHeight != height) ItemHeight = height;
        }

        internal DeviceComboBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            BackColor = Color.White;
            ForeColor = Color.Black;
            ApplyDpiMetrics();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiMetrics();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (DrawMode == DrawMode.OwnerDrawFixed) ApplyDpiMetrics();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyDpiMetrics();
        }

        protected override void OnDrawItem(DrawItemEventArgs args)
        {
            bool editing = (args.State & DrawItemState.ComboBoxEdit) != 0;
            bool highlighted = (args.State & (DrawItemState.Selected | DrawItemState.HotLight)) != 0;
            Color background = !Enabled ? SystemColors.Control : !editing && highlighted ? SystemColors.Highlight : Color.White;
            Color foreground = !Enabled ? SystemColors.GrayText : !editing && highlighted ? SystemColors.HighlightText : Color.Black;
            using (var brush = new SolidBrush(background))
                args.Graphics.FillRectangle(brush, args.Bounds);
            if (args.Index >= 0 && args.Index < Items.Count)
            {
                int padding = (int)Math.Round(8 * DeviceDpi / 96.0);
                TextRenderer.DrawText(args.Graphics, GetItemText(Items[args.Index]), args.Font, new Rectangle(args.Bounds.X + padding, args.Bounds.Y, Math.Max(1, args.Bounds.Width - padding * 2), args.Bounds.Height), foreground, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
            base.OnDrawItem(args);
        }

        protected override void WndProc(ref Message message)
        {
            // Device routing changes must be deliberate, never a wheel gesture.
            if (message.Msg == 0x020A || message.Msg == 0x020E)
            {
                message.Result = IntPtr.Zero;
                return;
            }

            base.WndProc(ref message);
        }
    }

    public sealed class MainForm : DpiFixedForm
    {
        private readonly ComboBox input = new DeviceComboBox(), output = new DeviceComboBox(), monitorDevice = new DeviceComboBox();
        private readonly Button refresh = new Button(), start = new Button(), listen = new Button(), language = new Button();
        private readonly Label inputLabel = new Label(), outputLabel = new Label(), monitorLabel = new Label(), inputLevelLabel = new Label(), outputLevelLabel = new Label(), hint = new Label(), state = new Label();
        private readonly Meter inputMeter = new Meter(), outputMeter = new Meter();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly AppSettings settings;
        private readonly Action<AppSettings> savePreferences;
        private readonly Action openLogs;
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private readonly ToolStripMenuItem trayExit = new ToolStripMenuItem();
        private readonly Font trayFont = new Font("Microsoft YaHei UI", 9, FontStyle.Regular, GraphicsUnit.Point);
        private readonly ToolTip errorDetails = new ToolTip
        {
            AutoPopDelay = 20000
        };
        private readonly string root = AppDomain.CurrentDomain.BaseDirectory;
        private IAudioSession engine;
        private readonly Func<EngineOptions, IAudioSession> createSession;
        private readonly Func<int, CancellationToken, Task> recoveryWait;
        private readonly Action streamFailureNotification;
        private readonly AutomaticRestart automaticRestart = new AutomaticRestart();
        private CancellationTokenSource recoveryCancellation;
        private Task recoveryTask;
        private EngineOptions activeOptions;
        private bool processingWanted, recovering, recoveryCleanupFailed;
        private bool busy, closing, english, monitorWanted, exitRequested;
        private double lastTick;
        private string statusKey = "ready", noticeKey = "", lastError = "";
        private AudioFailureKind lastFailureKind;
        private StartupResult startupResult;
        internal void ApplyStartupResult(StartupResult result)
        {
            if (result == null)
                return;
            startupResult = result;
            if (!String.IsNullOrEmpty(result.InputId))
            {
                input.SelectedIndex = -1;
                foreach (AudioDevice device in input.Items)
                    if (device.Id == result.InputId)
                    {
                        input.SelectedItem = device;
                        break;
                    }

                settings.InputId = result.InputId;
                if (input.SelectedItem == null)
                    noticeKey = "devices";
            }

            if (!String.IsNullOrEmpty(result.OutputId))
                SelectDevice(output, result.OutputId);
            if (result.InputMuted)
                noticeKey = "muted";
            if (result.InputSaveFailed)
                noticeKey = "save";
            UpdateControls();
        }

        internal string T(string zh, string en)
        {
            return english ? en : zh;
        }

        public MainForm() : this(AppSettings.Load(), preferences => preferences.Save())
        {
        }

        internal MainForm(AppSettings preferences) : this(preferences, null)
        {
        }

        internal MainForm(AppSettings preferences, Action<AppSettings> savePreferences, Action openLogs = null,
            Func<EngineOptions, IAudioSession> createSession = null, Func<int, CancellationToken, Task> recoveryWait = null,
            Action streamFailureNotification = null) : base(new Size(480, 456))
        {
            this.createSession = createSession ?? CreateSession;
            this.recoveryWait = recoveryWait ?? ((milliseconds, token) => Task.Delay(milliseconds, token));
            this.streamFailureNotification = streamFailureNotification;
            this.savePreferences = savePreferences;
            this.openLogs = openLogs ?? delegate
            {
                if (Directory.Exists(Diagnostics.Folder))
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Diagnostics.Folder + "\"") { UseShellExecute = true });
            };
            settings = preferences;
            english = settings.Language == "en";
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9);
            AutoScaleDimensions = new SizeF(96, 96);
            Text = "KeepClear";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            ClientSize = new Size(480, 456);
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Palette.Back;
            ForeColor = Palette.Ink;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20),
                ColumnCount = 1,
                RowCount = 8,
                Margin = new Padding(0)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[]
            {
                48,
                58,
                58,
                58,
                88,
                46,
                40,
                20
            })
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Controls.Add(layout);
            var header = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Size = new Size(440, 48)
            };
            header.Controls.Add(new PictureBox { Image = Icon.ToBitmap(), Location = new Point(0, 2), Size = new Size(32, 32), SizeMode = PictureBoxSizeMode.Zoom, TabStop = false });
            header.Controls.Add(new Label { Text = "KeepClear", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), Location = new Point(40, -2), AutoSize = true });
            StyleButton(language, false);
            language.Size = new Size(72, 30);
            language.Location = new Point(368, 2);
            language.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            language.Click += delegate
            {
                english = !english;
                ApplyLanguage();
                SaveSettings();
            };
            header.Controls.Add(language);
            layout.Controls.Add(header, 0, 0);
            layout.Controls.Add(DeviceField(inputLabel, input, true), 0, 1);
            layout.Controls.Add(DeviceField(outputLabel, output, false), 0, 2);
            layout.Controls.Add(DeviceField(monitorLabel, monitorDevice, false), 0, 3);
            monitorDevice.SelectedIndexChanged += delegate
            {
                UpdateControls();
            };
            var levels = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Margin = new Padding(0, 8, 0, 8),
                Padding = new Padding(12, 8, 12, 0),
                BackColor = Palette.Paper
            };
            levels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            levels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            levels.Controls.Add(MeterField(inputLevelLabel, inputMeter), 0, 0);
            levels.Controls.Add(MeterField(outputLevelLabel, outputMeter), 1, 0);
            layout.Controls.Add(levels, 0, 4);
            var actions = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Margin = new Padding(0, 0, 0, 4)
            };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            StyleButton(listen, false);
            listen.Dock = DockStyle.Fill;
            listen.Margin = new Padding(0, 0, 8, 0);
            StyleButton(start, true);
            start.Dock = DockStyle.Fill;
            start.Margin = new Padding(0);
            listen.Click += async delegate
            {
                await ToggleMonitor();
            };
            start.Click += async delegate
            {
                await ToggleEngine();
            };
            actions.Controls.Add(listen, 0, 0);
            actions.Controls.Add(start, 1, 0);
            layout.Controls.Add(actions, 0, 5);
            hint.Dock = DockStyle.Fill;
            hint.ForeColor = Palette.Muted;
            hint.Font = new Font(Font.FontFamily, 8);
            hint.Margin = new Padding(0, 5, 0, 0);
            layout.Controls.Add(hint, 0, 6);
            hint.Click += delegate
            {
                if (statusKey == "error" && !String.IsNullOrEmpty(lastError))
                {
                    try
                    {
                        this.openLogs();
                    }
                    catch (Exception ex)
                    {
                        ShowFailure(ex);
                    }
                }
            };
            state.Dock = DockStyle.Fill;
            state.ForeColor = Palette.Muted;
            state.Margin = new Padding(0);
            state.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(state, 0, 7);
            refresh.Click += delegate
            {
                RefreshDevices();
            };
            input.SelectedIndexChanged += delegate
            {
                UpdateControls();
            };
            output.SelectedIndexChanged += delegate
            {
                UpdateControls();
            };
            timer.Interval = 16;
            timer.Tick += delegate
            {
                TickUi();
            };
            timer.Start();
            trayMenu.Items.Add(trayExit);
            trayMenu.Renderer = new TrayMenuRenderer();
            trayExit.TextAlign = ContentAlignment.MiddleCenter;
            trayMenu.ShowImageMargin = trayMenu.ShowCheckMargin = false;
            trayMenu.Opening += delegate { UpdateTrayMetrics(); };
            trayMenu.Opened += delegate
            {
                UpdateTrayMetrics();
                Diagnostics.Write("tray-dpi", "dpi=" + trayMenu.DeviceDpi + " font_pt=" + trayExit.Font.SizeInPoints + " item=" + trayExit.Size + " menu=" + trayMenu.Size);
            };
            trayExit.Click += async delegate
            {
                await ExitApplication();
            };
            tray.Icon = Icon;
            tray.Text = "KeepClear";
            tray.ContextMenuStrip = trayMenu;
            tray.Visible = true;
            tray.MouseClick += delegate (object sender, MouseEventArgs args)
            {
                if (args.Button == MouseButtons.Left)
                    RestoreWindow();
            };
            Shown += delegate
            {
                RefreshDevices();
                ApplyStartupResult(startupResult);
            };
            FormClosing += async delegate (object sender, FormClosingEventArgs args)
            {
                if (closing)
                    return;
                if (args.CloseReason == CloseReason.WindowsShutDown || args.CloseReason == CloseReason.TaskManagerClosing)
                {
                    processingWanted = false;
                    if (recovering)
                    {
                        args.Cancel = true;
                        await ExitApplication();
                        return;
                    }
                    try
                    {
                        if (engine != null)
                        {
                            engine.Dispose();
                            engine = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Failure("system-exit-cleanup", ex);
                        args.Cancel = true;
                        exitRequested = false;
                        trayExit.Enabled = true;
                        monitorWanted = false;
                        statusKey = "error";
                        lastError = engine == null ? ex.Message : engine.Snapshot().Error;
                        if (String.IsNullOrEmpty(lastError))
                            lastError = ex.Message;
                        lastFailureKind = AudioFailureKind.None;
                        RestoreWindow();
                        UpdateControls();
                        return;
                    }

                    try
                    {
                        SaveSettings();
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Failure("system-exit-settings", ex);
                    }

                    closing = true;
                    tray.Visible = false;
                    return;
                }

                args.Cancel = true;
                if (args.CloseReason == CloseReason.UserClosing && !exitRequested)
                {
                    HideToTray();
                    SaveSettings();
                    return;
                }

                await ExitApplication();
            };
            ApplyLanguage();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                processingWanted = false;
                if (recoveryCancellation != null) recoveryCancellation.Cancel();
                timer.Dispose();
                tray.Visible = false;
                tray.Dispose();
                trayMenu.Dispose();
                trayFont.Dispose();
                errorDetails.Dispose();
            }

            base.Dispose(disposing);
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            timer.Interval = 250;
        }

        internal void RestoreWindow()
        {
            if (closing || IsDisposed)
                return;
            ShowInTaskbar = true;
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Show();
            Activate();
            timer.Interval = 16;
        }

        private async Task ExitApplication()
        {
            if (closing)
                return;
            exitRequested = true;
            processingWanted = false;
            trayExit.Enabled = false;
            if (recovering)
            {
                recoveryCancellation.Cancel();
                UpdateControls();
                await recoveryTask;
                if (closing || IsDisposed) return;
            }
            trayExit.Enabled = false;
            if (busy)
                return; // In-flight start/stop/monitor completes before exit.
            busy = true;
            UpdateControls();
            try
            {
                if (engine != null)
                {
                    await Task.Run((Action)engine.Dispose);
                    engine = null;
                    recoveryCleanupFailed = false;
                }

                monitorWanted = false;
                SaveSettings();
                closing = true;
                tray.Visible = false;
                Close();
            }
            catch (Exception ex)
            {
                busy = false;
                exitRequested = false;
                trayExit.Enabled = true;
                RestoreWindow();
                ShowFailure(ex);
                UpdateControls();
            }
        }

        private void UpdateTrayMetrics()
        {
            trayMenu.Font = trayFont;
            trayExit.Font = trayFont;
            int dpi = trayMenu.DeviceDpi;
            trayExit.Padding = new Padding((int)Math.Round(8 * dpi / 96.0), (int)Math.Round(4 * dpi / 96.0), (int)Math.Round(8 * dpi / 96.0), (int)Math.Round(4 * dpi / 96.0));
            trayMenu.PerformLayout();
        }

        protected override void ArrangeForDpi()
        {
            foreach (DeviceComboBox combo in new[] { input, output, monitorDevice })
                combo.ApplyDpiMetrics();
            refresh.Height = input.Height;
            Diagnostics.Write("device-ui-dpi", "dpi=" + DeviceDpi + " input=" + input.Size + " item_height=" + input.ItemHeight + " font_pt=" + input.Font.SizeInPoints + " refresh=" + refresh.Size);
        }

        private static void StyleButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Palette.Line;
            button.BackColor = primary ? Palette.Blue : Palette.Paper;
            button.ForeColor = primary ? Color.White : Palette.Ink;
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
        }

        private Panel DeviceField(Label label, ComboBox combo, bool withRefresh)
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0)
            };
            label.Dock = DockStyle.Top;
            label.Height = 23;
            label.ForeColor = Palette.Muted;
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 30,
                ColumnCount = withRefresh ? 2 : 1,
                Margin = new Padding(0)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            combo.Dock = DockStyle.Fill;
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.Margin = new Padding(0);
            combo.SizeChanged += delegate
            {
                combo.DropDownWidth = combo.Width;
                if (withRefresh)
                    refresh.Height = combo.Height;
            };
            row.Controls.Add(combo, 0, 0);
            if (withRefresh)
            {
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
                StyleButton(refresh, false);
                refresh.Dock = DockStyle.Top;
                refresh.Height = combo.Height;
                refresh.Margin = new Padding(8, 0, 0, 0);
                row.Controls.Add(refresh, 1, 0);
            }

            panel.Controls.Add(row);
            panel.Controls.Add(label);
            return panel;
        }

        private Panel MeterField(Label label, Meter meter)
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Padding = new Padding(0, 0, 12, 0)
            };
            label.Dock = DockStyle.Top;
            label.Height = 23;
            label.ForeColor = Palette.Muted;
            meter.Dock = DockStyle.Top;
            meter.Height = 29;
            panel.Controls.Add(meter);
            panel.Controls.Add(label);
            return panel;
        }

        private void ApplyLanguage()
        {
            trayExit.Text = T("退出", "Exit");
            inputLabel.Text = T("输入麦克风", "Microphone");
            outputLabel.Text = T("虚拟麦克风输出", "Virtual microphone output");
            monitorLabel.Text = T("监听耳机 / 扬声器", "Monitor headphones / speakers");
            inputLevelLabel.Text = T("输入电平", "Input level");
            outputLevelLabel.Text = T("输出电平", "Output level");
            input.AccessibleName = inputLabel.Text;
            output.AccessibleName = outputLabel.Text;
            monitorDevice.AccessibleName = monitorLabel.Text;
            inputMeter.AccessibleName = inputLevelLabel.Text;
            outputMeter.AccessibleName = outputLevelLabel.Text;
            refresh.Text = T("刷新", "Refresh");
            language.Text = english ? "中文" : "English";
            language.AccessibleName = T("切换为英文界面", "Switch interface to Chinese");
            UpdateControls();
        }

        private void UpdateControls()
        {
            bool running = engine != null;
            input.Enabled = output.Enabled = refresh.Enabled = !busy && !running && !recovering;
            monitorDevice.Enabled = !busy && !monitorWanted && !recovering;
            start.Enabled = recovering ? !exitRequested && !recoveryCancellation.IsCancellationRequested
                : !busy && (running || (input.SelectedItem != null && output.SelectedItem != null));
            listen.Enabled = !busy && (monitorWanted || (CanEnableMonitor && monitorDevice.SelectedItem != null));
            start.Text = recovering ? T("停止", "Stop") : busy ? T("请稍候…", "Please wait…") : running ? (String.IsNullOrEmpty(lastError) ? T("停止", "Stop") : T("复位", "Reset")) : T("启动", "Start");
            listen.Text = monitorWanted ? T("监听：开", "Monitor: on") : T("监听：关", "Monitor: off");
            listen.BackColor = monitorWanted ? Color.FromArgb(225, 237, 252) : Palette.Paper;
            listen.ForeColor = monitorWanted ? Palette.Blue : Palette.Ink;
            hint.Text = noticeKey == "recovery" ? T("等待时间持续增加，正在恢复音频…", "Recovering a growing audio backlog…") : noticeKey == "devices" ? T("未检测到麦克风或 VB-CABLE，连接设备后点击刷新。", "Microphone or VB-CABLE missing. Connect devices, then refresh.") : noticeKey == "save" ? T("设置未能保存，请检查用户配置目录权限。", "Could not save settings. Check user folder permissions.") : noticeKey == "monitor" ? T("监听设备不可用，请关闭监听后重新选择设备。", "Monitor unavailable. Turn it off and select another device.") : noticeKey == "muted" ? T("启动检查时麦克风已静音，使用前请取消静音。", "Microphone was muted at startup. Unmute it before use.") : T("游戏 / 通话的麦克风请选择 CABLE Output。", "Select CABLE Output as the microphone in your game or call.");
            state.Text = statusKey == "error" ? T("● 处理已停止，请复位后重试", "● Processing stopped. Reset to try again") : statusKey == "loading" ? T("● 正在加载模型…", "● Loading models…") : statusKey == "running" ? T("● 降噪中", "● Noise reduction active") : statusKey == "stopped" ? T("● 已停止", "● Stopped") : T("● 就绪", "● Ready");
            state.ForeColor = statusKey == "error" ? Color.FromArgb(175, 62, 45) : statusKey == "running" ? Palette.Blue : Palette.Muted;
            if (statusKey == "error" && engine == null && automaticRestart.Attempts > 0)
                state.Text = T("● 自动恢复已停止，请重新启动", "● Recovery stopped. Start to retry");
            if (statusKey == "error" && !String.IsNullOrEmpty(lastError))
            {
                hint.Text = lastFailureKind == AudioFailureKind.None ? lastError : AudioFailure.Title(lastFailureKind, english) + ". " + AudioFailure.Advice(lastFailureKind, english);
                errorDetails.SetToolTip(hint, lastError + "\n" + Diagnostics.Folder);
            }
            else
                errorDetails.SetToolTip(hint, "");
            if (recovering)
            {
                bool cancelling = recoveryCancellation.IsCancellationRequested;
                state.Text = cancelling ? T("● 正在停止…", "● Stopping audio…") : T("● 正在自动恢复…", "● Restarting audio…");
                state.ForeColor = Palette.Blue;
                hint.Text = cancelling ? T("正在释放音频设备…", "Releasing audio devices…")
                    : T("自动重试 ", "Automatic retry ") + automaticRestart.Attempts + "/3. "
                        + T("点击停止可取消。", "Stop to cancel.");
                errorDetails.SetToolTip(hint, "");
            }
            hint.Cursor = statusKey == "error" && !String.IsNullOrEmpty(lastError) ? Cursors.Hand : Cursors.Default;
        }

        private void RefreshDevices()
        {
            try
            {
                string sourceId = SelectedId(input, settings.InputId), outputId = SelectedId(output, settings.OutputId), monitorId = SelectedId(monitorDevice, settings.MonitorId);
                settings.InputId = sourceId;
                input.Items.Clear();
                output.Items.Clear();
                monitorDevice.Items.Clear();
                foreach (var device in NativeAudio.Devices(true).Where(d => !d.IsCable && String.IsNullOrEmpty(d.IdentityError)))
                    input.Items.Add(device);
                foreach (var device in NativeAudio.Devices(false))
                    if (device.IsCable)
                        output.Items.Add(device);
                    else if (String.IsNullOrEmpty(device.IdentityError))
                        monitorDevice.Items.Add(device);
                SelectDevice(input, sourceId, String.IsNullOrEmpty(sourceId));
                var preferredOutput = AudioStartupTest.ChooseOutput(output.Items.Cast<AudioDevice>().ToArray(), outputId);
                SelectDevice(output, preferredOutput == null ? "" : preferredOutput.Id, false);
                SelectDevice(monitorDevice, monitorId);
                noticeKey = input.SelectedItem == null || output.SelectedItem == null ? "devices" : "";
            }
            catch (Exception ex)
            {
                statusKey = "error";
                ShowFailure(ex);
            }

            UpdateControls();
        }

        private static string SelectedId(ComboBox combo, string fallback)
        {
            var device = combo.SelectedItem as AudioDevice;
            return device == null ? fallback : device.Id;
        }

        private static void SelectDevice(ComboBox combo, string id, bool allowFallback = true)
        {
            foreach (AudioDevice device in combo.Items)
                if (device.Id == id)
                {
                    combo.SelectedItem = device;
                    return;
                }

            if (!allowFallback)
            {
                combo.SelectedIndex = -1;
                return;
            }

            foreach (AudioDevice device in combo.Items)
                if (device.IsDefault)
                {
                    combo.SelectedItem = device;
                    return;
                }

            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private bool CanEnableMonitor
        {
            get
            {
                if (engine == null)
                    return false;
                EngineSnapshot snapshot = engine.Snapshot();
                return snapshot.State == "运行中" && String.IsNullOrEmpty(snapshot.Error);
            }
        }

        private async Task ToggleMonitor()
        {
            if (busy)
                return;
            bool enable = !monitorWanted;
            string id = SelectedId(monitorDevice, "");
            if (enable && (!CanEnableMonitor || String.IsNullOrEmpty(id)))
                return;
            busy = true;
            UpdateControls();
            try
            {
                if (engine != null)
                    await Task.Run(delegate
                    {
                        engine.SetMonitor(enable ? id : null);
                    });
                monitorWanted = enable;
                noticeKey = "";
                SaveSettings();
            }
            catch (Exception ex)
            {
                ShowFailure(ex);
            }
            finally
            {
                busy = false;
                UpdateControls();
            }

            if (exitRequested)
                await ExitApplication();
        }

        private async Task ToggleEngine()
        {
            if (recovering)
            {
                processingWanted = false;
                recoveryCancellation.Cancel();
                UpdateControls();
                await recoveryTask;
                return;
            }
            if (busy)
                return;
            busy = true;
            UpdateControls();
            bool stopping = engine != null;
            Exception failure = null;
            try
            {
                if (engine != null)
                {
                    processingWanted = false;
                    await Task.Run((Action)engine.Dispose);
                    engine = null;
                    monitorWanted = false;
                    recoveryCleanupFailed = false;
                    statusKey = "stopped";
                    noticeKey = "";
                    lastError = "";
                }
                else
                {
                    var source = input.SelectedItem as AudioDevice;
                    var destination = output.SelectedItem as AudioDevice;
                    if (source == null || destination == null || source.IsCable || !destination.IsCable)
                        throw new InvalidOperationException(T("请选择真实麦克风和 VB-CABLE 输出。", "Choose a physical microphone and VB-CABLE output."));
                    settings.Python = AppSettings.ResolvePython(root, settings.Python);
                    SaveSettings();
                    lastError = "";
                    lastFailureKind = AudioFailureKind.None;
                    statusKey = "loading";
                    automaticRestart.Reset();
                    recoveryCleanupFailed = false;
                    activeOptions = new EngineOptions { InputId = source.Id, OutputId = destination.Id, Python = settings.Python, Root = root, Alkove = true, Denoiser = true, GainDb = Math.Max(-12, Math.Min(18, settings.GainDb)) };
                    engine = createSession(activeOptions);
                    engine.Start();
                    processingWanted = true;
                }
            }
            catch (Exception ex)
            {
                processingWanted = false;
                statusKey = "error";
                lastError = ex.Message;
                failure = ex;
            }

            try
            {
                if (failure != null)
                {
                    if (!stopping && engine != null)
                    {
                        try
                        {
                            await Task.Run((Action)engine.Dispose);
                            engine = null;
                            monitorWanted = false;
                        }
                        catch (Exception cleanup)
                        {
                            failure = new AggregateException(failure, cleanup);
                            lastError = failure.Message;
                        }
                    }

                    ShowFailure(failure);
                }
            }
            finally
            {
                busy = false;
                UpdateControls();
            }

            if (exitRequested)
                await ExitApplication();
        }

        private void TickUi()
        {
            double now = clock.Elapsed.TotalSeconds, elapsed = now - lastTick;
            lastTick = now;
            EngineSnapshot snapshot = engine == null || busy ? null : engine.Snapshot();
            if (Visible)
            {
                inputMeter.Advance(snapshot == null ? 0 : snapshot.InputPeak, elapsed);
                outputMeter.Advance(snapshot == null ? 0 : snapshot.OutputPeak, elapsed);
            }

            if (snapshot == null || recoveryCleanupFailed)
                return;
            if (processingWanted && !exitRequested && !String.IsNullOrEmpty(snapshot.Error)
                && AutomaticRestart.CanRetry(snapshot.FailureKind))
            {
                recoveryTask = RecoverEngine(snapshot);
                return;
            }
            if (automaticRestart.ObserveHealthy(now, snapshot))
                Diagnostics.Write("auto-restart", "retry budget replenished after 120 seconds of progressing, stable low-queue operation");
            string nextStatus = !String.IsNullOrEmpty(snapshot.Error) ? "error" : snapshot.State == "运行中" ? "running" : "loading";
            string nextNotice = !String.IsNullOrEmpty(snapshot.MonitorError) ? "monitor" : snapshot.Recovering ? "recovery" : noticeKey == "recovery" ? "" : noticeKey;
            bool resetMonitor = monitorWanted && nextStatus != "running";
            if (resetMonitor)
                monitorWanted = false;
            bool changed = resetMonitor || statusKey != nextStatus || noticeKey != nextNotice || lastError != snapshot.Error;
            bool newFailure = !String.IsNullOrEmpty(snapshot.Error) && lastError != snapshot.Error;
            statusKey = nextStatus;
            noticeKey = nextNotice;
            lastError = snapshot.Error;
            lastFailureKind = snapshot.FailureKind;
            if (changed)
                UpdateControls();
            if (newFailure)
            {
                processingWanted = false;
                NotifyStreamFailure();
            }
        }

        private IAudioSession CreateSession(EngineOptions options)
        {
            foreach (string file in new[] { "alkove/alkove-focus-1-preview.alk", "alkove/alkove-cue-1-preview.alk", "denoiser/dns48-state.pt" })
                if (!File.Exists(Path.Combine(options.Root, "models", file)))
                    throw new AudioFailureException(AudioFailureKind.ModelLoad, T("本地模型不完整，请重新运行 setup-models.ps1。", "Local model files are missing. Run setup-models.ps1 again."));
            return new AudioEngine(options);
        }

        private async Task DisposeEngine()
        {
            var previous = engine;
            if (previous == null) return;
            await Task.Run((Action)previous.Dispose);
            engine = null;
        }

        private async Task RecoverEngine(EngineSnapshot failure)
        {
            recovering = busy = true;
            var source = new CancellationTokenSource();
            recoveryCancellation = source;
            var token = source.Token;
            bool restoreMonitor = monitorWanted;
            string monitorId = SelectedId(monitorDevice, settings.MonitorId);
            bool success = false, cleanupFailed = false;
            lastError = failure.Error;
            lastFailureKind = failure.FailureKind;
            UpdateControls();
            try
            {
                int delay;
                while (processingWanted && !exitRequested && automaticRestart.TryReserve(failure.FailureKind, out delay))
                {
                    Diagnostics.Write("auto-restart", "attempt=" + automaticRestart.Attempts + " delay_ms=" + delay
                        + " kind=" + failure.FailureKind + " input=" + activeOptions.InputId + " output=" + activeOptions.OutputId);
                    UpdateControls();
                    try { await DisposeEngine(); }
                    catch (Exception) { cleanupFailed = true; throw; }
                    token.ThrowIfCancellationRequested();
                    await recoveryWait(delay, token);
                    token.ThrowIfCancellationRequested();
                    engine = createSession(activeOptions);
                    engine.Start();
                    double deadline = clock.Elapsed.TotalSeconds + 45;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        failure = engine.Snapshot();
                        if (!String.IsNullOrEmpty(failure.Error)) break;
                        if (failure.State == "运行中" && failure.CaptureFrames > 0 && failure.ProcessedRenderFrames > 0)
                        {
                            success = true;
                            break;
                        }
                        if (clock.Elapsed.TotalSeconds >= deadline)
                            throw new AudioFailureException(AudioFailureKind.Timeout, "Automatic restart did not establish audio flow within 45 seconds.");
                        await recoveryWait(100, token);
                    }
                    if (success) break;
                    lastError = failure.Error;
                    lastFailureKind = failure.FailureKind;
                }
                if (success && restoreMonitor && !String.IsNullOrEmpty(monitorId))
                {
                    try { await Task.Run(() => engine.SetMonitor(monitorId)); }
                    catch (Exception ex)
                    {
                        Diagnostics.Failure("auto-restart-monitor", ex);
                        monitorWanted = false;
                        noticeKey = "monitor";
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                success = false;
                lastError = ex.Message;
                lastFailureKind = AudioFailure.Classify("auto-restart", ex);
                Diagnostics.Failure("auto-restart", ex);
            }

            success = success && processingWanted && !exitRequested && !token.IsCancellationRequested;
            if (!success && !cleanupFailed)
            {
                try { await DisposeEngine(); }
                catch (Exception ex)
                {
                    cleanupFailed = true;
                    lastError = ex.Message;
                    lastFailureKind = AudioFailureKind.None;
                    Diagnostics.Failure("auto-restart-cleanup", ex);
                }
            }
            bool cancelled = token.IsCancellationRequested || !processingWanted || exitRequested;
            if (success)
            {
                automaticRestart.ObserveHealthy(clock.Elapsed.TotalSeconds, engine.Snapshot());
                lastError = "";
                lastFailureKind = AudioFailureKind.None;
                statusKey = "running";
                if (noticeKey != "monitor") noticeKey = "";
                Diagnostics.Write("auto-restart", "completed attempt=" + automaticRestart.Attempts);
            }
            else
            {
                monitorWanted = false;
                processingWanted = false;
                statusKey = cancelled && !cleanupFailed ? "stopped" : "error";
                if (statusKey == "stopped") { lastError = ""; lastFailureKind = AudioFailureKind.None; noticeKey = ""; }
                Diagnostics.Write("auto-restart", cancelled ? "cancelled" : "stopped; retry budget exhausted or nonrecoverable fault");
            }
            recovering = busy = false;
            recoveryCleanupFailed = cleanupFailed;
            recoveryCancellation = null;
            source.Dispose();
            if (!IsDisposed)
            {
                UpdateControls();
                if (!success && (!cancelled || cleanupFailed)) NotifyStreamFailure();
            }
        }

        private void NotifyStreamFailure()
        {
            if (streamFailureNotification != null) { streamFailureNotification(); return; }
            if (Visible)
                MessageBox.Show(this, AudioFailure.Title(lastFailureKind, english) + "\n\n" + AudioFailure.Advice(lastFailureKind, english) + "\n\n" + T("诊断日志：", "Diagnostic logs: ") + Diagnostics.Folder, "KeepClear", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                tray.ShowBalloonTip(6000, "KeepClear", T("处理已停止，请打开窗口查看错误。", "Processing stopped. Open the window to view the error."), ToolTipIcon.Error);
        }

        private void ShowFailure(Exception ex)
        {
            Diagnostics.Failure("main-ui", ex);
            MessageBox.Show(this, T("操作未完成，请检查设备和本地模型文件。\n\n", "Could not complete the operation. Check devices and local model files.\n\n") + ex.Message + "\n\n" + T("诊断日志：", "Diagnostic logs: ") + Diagnostics.Folder, "KeepClear", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void SaveSettings()
        {
            settings.InputId = SelectedId(input, settings.InputId);
            settings.OutputId = SelectedId(output, settings.OutputId);
            settings.MonitorId = SelectedId(monitorDevice, settings.MonitorId);
            settings.Language = english ? "en" : "zh";
            if (savePreferences == null)
                return;
            try
            {
                savePreferences(settings);
            }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException))
                    throw;
                noticeKey = "save";
                UpdateControls();
            }
        }
    }
}

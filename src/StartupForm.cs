using System;
using System.ComponentModel;
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
    internal sealed class StartupForm : Form
    {
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Font brand = new Font("Segoe UI Semibold", 40, FontStyle.Regular);
        private readonly Label status = new Label(), detail = new Label();
        private readonly Button retry = new Button(), exit = new Button(), restart = new Button(), language = new Button();
        private readonly ComboBox microphone = new DeviceComboBox();
        private readonly Button refreshMicrophone = new Button();
        private readonly ToolTip errors = new ToolTip
        {
            AutoPopDelay = 20000
        };
        private readonly Func<Action<string>, CancellationToken, StartupResult> check;
        private readonly Func<bool> confirmRestart;
        private readonly Func<int> restartWindows;
        private readonly Action<string> saveLanguage;
        private readonly Func<string, Action<string>, CancellationToken, StartupResult> audioCheck;
        private readonly Func<AudioDevice[]> microphones;
        private readonly Func<string> savedInput;
        private readonly Action<string> saveInput;
        private string microphoneError = "";
        private bool selectionSaveFailed;
        private bool english;
        private Exception restartError;
        private StartupResult result;
        private double finishedAt;
        private double typingDuration = 1.8;
        private double TypingEnd
        {
            get
            {
                return 0.28 + typingDuration;
            }
        }

        private bool checking, promoted, restarting;
        internal event Action Ready;
        internal StartupResult Result
        {
            get
            {
                return result;
            }
        }

        private string T(string zh, string en)
        {
            return english ? en : zh;
        }

        internal StartupForm(Func<Action<string>, CancellationToken, StartupResult> check, bool english) : this(check, english, null, null)
        {
        }

        internal StartupForm(Func<Action<string>, CancellationToken, StartupResult> check, bool english, Func<bool> confirmRestart, Func<int> restartWindows, Action<string> saveLanguage = null, Func<string, Action<string>, CancellationToken, StartupResult> audioCheck = null, Func<AudioDevice[]> microphones = null, Func<string> savedInput = null, Action<string> saveInput = null)
        {
            this.check = check;
            this.english = english;
            this.saveLanguage = saveLanguage;
            this.audioCheck = audioCheck ?? ((id, progress, token) => StartupCheck.Run(false, progress, token, true, id));
            this.microphones = microphones ?? (() => NativeAudio.Devices(true).Where(d => !d.IsCable && String.IsNullOrEmpty(d.IdentityError)).ToArray());
            this.savedInput = savedInput ?? (() => AppSettings.Load().InputId);
            this.saveInput = saveInput ?? (id =>
            {
                var preferences = AppSettings.Load();
                preferences.InputId = id;
                preferences.Save();
            });
            this.confirmRestart = confirmRestart ?? (() => MessageBox.Show(this, T("确定现在重新启动 Windows 吗？请先保存其他程序中的工作。", "Restart Windows now? Save your work in other applications first."), "KeepClear", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes);
            this.restartWindows = restartWindows ?? delegate
            {
                // A zero timeout without /f lets Windows handle applications with unsaved work.
                using (var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 0 /d p:2:4") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    process.WaitForExit();
                    return process.ExitCode;
                }
            };
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9);
            Text = "KeepClear";
            ClientSize = new Size(480, 280);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            DoubleBuffered = true;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            status.SetBounds(16, 186, 448, 24);
            status.TextAlign = ContentAlignment.MiddleCenter;
            detail.SetBounds(16, 210, 448, 34);
            detail.Font = new Font(Font.FontFamily, 8);
            detail.TextAlign = ContentAlignment.MiddleCenter;
            detail.Click += delegate
            {
                if (result != null && !result.Ready && (result.Status != "select-microphone" || !String.IsNullOrEmpty(microphoneError)) && Directory.Exists(Diagnostics.Folder))
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Diagnostics.Folder + "\"") { UseShellExecute = true });
            };
            retry.SetBounds(146, 246, 92, 26);
            restart.SetBounds(146, 246, 92, 26);
            exit.SetBounds(246, 246, 88, 26);
            language.Name = "startupLanguage";
            language.SetBounds(380, 12, 72, 28);
            microphone.Name = "startupMicrophone";
            microphone.DropDownStyle = ComboBoxStyle.DropDownList;
            microphone.ItemHeight = 26;
            microphone.SetBounds(28, 164, 344, 32);
            microphone.DropDownWidth = microphone.Width;
            refreshMicrophone.Name = "refreshMicrophone";
            refreshMicrophone.SetBounds(382, 164, 70, microphone.Height);
            microphone.SizeChanged += delegate
            {
                microphone.DropDownWidth = microphone.Width;
                refreshMicrophone.Height = microphone.Height;
            };
            microphone.SelectedIndexChanged += delegate
            {
                if (!checking && result != null && result.ChooseMicrophone)
                    retry.Enabled = microphone.SelectedItem != null;
            };
            refreshMicrophone.Click += delegate
            {
                RefreshMicrophones();
                ApplyLanguage();
            };
            microphone.Visible = refreshMicrophone.Visible = false;
            retry.Visible = exit.Visible = restart.Visible = language.Visible = false;
            retry.Click += async delegate
            {
                await StartCheck(result != null && result.ChooseMicrophone);
            };
            exit.Click += delegate
            {
                Close();
            };
            restart.Click += async delegate
            {
                await RestartComputer();
            };
            language.Click += delegate
            {
                string selected = this.english ? "zh" : "en";
                try
                {
                    if (this.saveLanguage != null)
                        this.saveLanguage(selected);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, T("无法保存语言设置：", "Unable to save language preference: ") + ex.Message, "KeepClear", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                this.english = !this.english;
                ApplyLanguage();
            };
            Controls.AddRange(new Control[] { status, detail, retry, exit, restart, language, microphone, refreshMicrophone });
            foreach (var button in new[]
            {
                retry,
                exit,
                restart,
                language,
                refreshMicrophone
            })
                StyleButton(button, false);
            ApplyLanguage();
            timer.Interval = 16;
            timer.Tick += delegate
            {
                Invalidate();
                if (!promoted && result != null && result.Ready && clock.Elapsed.TotalSeconds >= Math.Max(TypingEnd, finishedAt) + 0.45)
                {
                    promoted = true;
                    timer.Stop();
                    if (Ready != null)
                        Ready();
                }
            };
            timer.Start();
            Shown += async delegate
            {
                MinimumSize = MaximumSize = Size;
                await StartCheck();
            };
            FormClosed += delegate
            {
                cancellation.Cancel();
            };
            Progress("checking-runtime");
        }

        internal void RestoreWindow()
        {
            if (!IsDisposed)
            {
                if (WindowState == FormWindowState.Minimized)
                    WindowState = FormWindowState.Normal;
                Show();
                Activate();
            }
        }

        private static void StyleButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Palette.Line;
            button.BackColor = primary ? Palette.Blue : Color.White;
            button.ForeColor = primary ? Color.White : Color.Black;
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
        }

        private async Task RestartComputer()
        {
            if (restarting || result == null || (result.Status != "restart-required" && result.Status != "driver-pending") || !confirmRestart())
                return;
            restarting = true;
            restartError = null;
            retry.Enabled = restart.Enabled = exit.Enabled = language.Enabled = false;
            try
            {
                int code = await Task.Run(restartWindows);
                if (IsDisposed)
                    return;
                if (code != 0)
                    throw new Win32Exception(code);
                Close();
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    restartError = ex;
                    ApplyLanguage();
                }
            }
            finally
            {
                if (!IsDisposed)
                {
                    restarting = false;
                    retry.Enabled = restart.Enabled = exit.Enabled = language.Enabled = true;
                }
            }
        }

        private void Progress(string key)
        {
            if (IsDisposed)
                return;
            status.Text = key == "checking-models" ? T("正在检查模型文件…", "Checking model files…") : key == "checking-components" ? T("正在检查推理组件…", "Checking inference components…") : key == "checking-driver" ? T("正在检查虚拟声卡…", "Checking virtual audio device…") : key == "installing-driver" ? T("正在自动安装 VB-CABLE…", "Installing VB-CABLE…") : key == "waiting-driver" ? T("正在等待虚拟声卡就绪…", "Waiting for VB-CABLE to become ready…") : key == "checking-audio" ? T("正在加载模型并打开音频链路…", "Loading models and opening audio streams…") : key == "testing-audio" ? T("正在进行静音链路自检…", "Testing audio streams with silent output…") : key == "recovering-audio" ? T("检测到短暂卡顿，正在复查…", "Checking recovery after a brief interruption…") : T("正在检查本地运行环境…", "Checking local runtime…");
            detail.Text = key == "installing-driver" ? T("VB-CABLE 来自 VB-Audio，是 Donationware。\n请确认 Windows 管理员授权。", "VB-CABLE by VB-Audio. Donationware.\nApprove the Windows administrator prompt.") : "";
            errors.SetToolTip(detail, "");
        }

        private void RefreshMicrophones()
        {
            string selected = (microphone.SelectedItem as AudioDevice) == null ? savedInput() : ((AudioDevice)microphone.SelectedItem).Id;
            microphone.Items.Clear();
            microphoneError = "";
            try
            {
                foreach (var device in microphones())
                    microphone.Items.Add(device);
                foreach (AudioDevice device in microphone.Items)
                    if (device.Id == selected)
                    {
                        microphone.SelectedItem = device;
                        break;
                    }
            }
            catch (Exception ex)
            {
                microphoneError = ex.Message;
                Diagnostics.Failure("microphone-list", ex);
            }

            retry.Enabled = microphone.SelectedItem != null;
        }

        private async Task StartCheck(bool testMicrophone = false)
        {
            if (checking || restarting)
                return;
            string selected = null;
            if (testMicrophone)
            {
                var device = microphone.SelectedItem as AudioDevice;
                if (device == null)
                    return;
                selected = device.Id;
                selectionSaveFailed = false;
                try
                {
                    saveInput(selected);
                }
                catch (Exception ex)
                {
                    Diagnostics.Failure("microphone-save", ex);
                    selectionSaveFailed = true;
                }
            }

            checking = true;
            result = null;
            restartError = null;
            retry.Visible = exit.Visible = restart.Visible = language.Visible = microphone.Visible = refreshMicrophone.Visible = false;
            status.SetBounds(16, 186, 448, 24);
            detail.SetBounds(16, 210, 448, 34);
            status.TextAlign = detail.TextAlign = ContentAlignment.MiddleCenter;
            detail.ForeColor = Color.Black;
            StyleButton(retry, false);
            retry.SetBounds(146, 246, 92, 26);
            restart.SetBounds(146, 246, 92, 26);
            exit.SetBounds(246, 246, 88, 26);
            retry.Enabled = true;
            typingDuration = testMicrophone ? 2.6 : 1.8;
            clock.Restart();
            Progress("checking-runtime");
            Action<string> report = step =>
            {
                if (IsDisposed)
                    return;
                try
                {
                    BeginInvoke((Action)(() => Progress(step)));
                }
                catch (InvalidOperationException)
                {
                }
            };
            StartupResult completed;
            try
            {
                completed = await Task.Run(() => testMicrophone ? audioCheck(selected, report, cancellation.Token) : check(report, cancellation.Token));
            }
            catch (OperationCanceledException)
            {
                checking = false;
                return;
            }
            catch (Win32Exception ex)
            {
                completed = new StartupResult
                {
                    Status = ex.NativeErrorCode == 1223 ? "cancelled" : "failed",
                    Detail = ex.Message
                };
            }
            catch (Exception ex)
            {
                completed = new StartupResult
                {
                    Status = "failed",
                    Detail = ex.Message
                };
            }

            if (IsDisposed)
                return;
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke((Action)(() => CompleteCheck(completed)));
                }
                catch (InvalidOperationException)
                {
                }
            }
            else
                CompleteCheck(completed);
        }

        private void CompleteCheck(StartupResult completed)
        {
            if (IsDisposed)
                return;
            completed.InputSaveFailed = selectionSaveFailed;
            result = completed;
            finishedAt = clock.Elapsed.TotalSeconds;
            checking = false;
            if (completed.ChooseMicrophone)
                RefreshMicrophones();
            ApplyLanguage();
            if (!completed.Ready)
            {
                restart.Visible = completed.Status == "restart-required" || completed.Status == "driver-pending";
                retry.Visible = completed.CanRetry && completed.Status != "restart-required";
                exit.Visible = language.Visible = true;
                if (completed.Status == "driver-pending")
                {
                    retry.SetBounds(96, 246, 92, 26);
                    restart.SetBounds(194, 246, 92, 26);
                    exit.SetBounds(292, 246, 88, 26);
                }
            }

            Invalidate();
        }

        private void ApplyLanguage()
        {
            retry.Text = T("重新检查", "Retry");
            exit.Text = T("退出", "Exit");
            restart.Text = T("重启电脑", "Restart PC");
            language.Text = english ? "中文" : "English";
            refreshMicrophone.Text = T("刷新", "Refresh");
            if (result == null)
                return;
            var completed = result;
            status.Text = completed.Ready ? T("检查完成", "Ready") : completed.Status == "restart-required" ? T("安装程序已结束，请重新启动 Windows", "Setup finished. Restart Windows to continue") : completed.Status == "inactive-driver" ? T("虚拟声卡已安装，但当前不可用", "VB-CABLE is installed but unavailable") : completed.Status == "incomplete-driver" ? T("VB-CABLE 安装不完整", "VB-CABLE installation is incomplete") : completed.Status == "installer-busy" ? T("VB-CABLE 安装程序正在运行", "VB-CABLE setup is already running") : completed.Status == "driver-detection-failed" ? T("无法确认虚拟声卡的设备身份", "Unable to verify virtual audio device identity") : completed.Status == "driver-pending" ? T("安装已结束，虚拟声卡尚未就绪", "Setup finished; VB-CABLE is not ready yet") : completed.Status == "audio-failed" ? AudioFailure.Title(completed.FailureKind, english) : completed.Status == "cancelled" ? T("安装未获授权，等待重新检查", "Installation cancelled. Waiting to retry") : completed.Status == "missing-driver" ? T("缺少依赖：VB-CABLE 虚拟声卡", "Missing dependency: VB-CABLE") : completed.Status == "missing-files" || completed.Status == "damaged-files" ? T("程序文件缺失或损坏", "Application files are missing or damaged") : T("依赖检查未通过", "Dependency check failed");
            detail.Text = completed.Ready ? "" : completed.Status == "restart-required" ? T("点击“重启电脑”，或退出后自行重启；重启后再打开 KeepClear。", "Choose Restart PC or restart manually, then open KeepClear.") : completed.Status == "inactive-driver" ? T("请在 Windows 声音设置或设备管理器启用 VB-CABLE。", "Enable VB-CABLE in Windows sound settings or Device Manager.") : completed.Status == "incomplete-driver" ? T("请重新检查以使用内置安装包修复。", "Retry to install the bundled driver package.") : completed.Status == "installer-busy" ? T("等待安装完成后点击重新检查。", "Wait for setup to finish, then retry.") : completed.Status == "driver-detection-failed" ? T("请重新检查；点击此处打开诊断日志。", "Retry detection. Click here to open diagnostic logs.") : completed.Status == "driver-pending" ? T("重新检查会继续等待，不会重复安装。也可重启电脑。", "Retry waits without reinstalling. You may also restart Windows.") : completed.Status == "missing-files" || completed.Status == "damaged-files" ? T("请重新解压完整运行包。缺失项：", "Extract the complete package again. File: ") + completed.Detail : completed.Status == "cancelled" ? T("可点击“重新检查”再次请求管理员授权。", "Retry to request administrator permission again.") : completed.Detail;
            if (!completed.Ready)
            {
                errors.SetToolTip(detail, completed.Status == "select-microphone" ? "" : (completed.Detail ?? "") + "\n" + Diagnostics.Folder);
                if (completed.Status == "failed" || completed.Status == "audio-failed")
                {
                    string reason = (completed.Detail ?? "").Split('\n')[0], shortReason = reason;
                    while (shortReason.Length > 0 && TextRenderer.MeasureText(shortReason + (shortReason.Length < reason.Length ? "…" : ""), detail.Font).Width > detail.Width - 8)
                        shortReason = shortReason.Substring(0, shortReason.Length - 1);
                    detail.Text = (shortReason.Length == 0 ? "" : shortReason + (shortReason.Length < reason.Length ? "…" : "") + "\n") + T("详细错误 / 日志：点击查看", "Full error / logs: click here");
                }
            }

            if (restartError != null)
                detail.Text = T("无法重启，请手动重启电脑。", "Unable to restart. Restart Windows manually.") + "\n" + restartError.Message;
            microphone.Visible = refreshMicrophone.Visible = completed.ChooseMicrophone;
            if (completed.ChooseMicrophone)
            {
                bool failed = completed.FailureKind != AudioFailureKind.None || !String.IsNullOrEmpty(microphoneError);
                status.SetBounds(28, failed ? 124 : 140, 424, failed ? 24 : 20);
                microphone.SetBounds(28, failed ? 154 : 164, 344, 32);
                refreshMicrophone.SetBounds(382, microphone.Top, 70, microphone.Height);
                detail.SetBounds(28, failed ? 186 : 200, 424, failed ? 48 : 22);
                status.TextAlign = detail.TextAlign = ContentAlignment.MiddleLeft;
                detail.ForeColor = Color.FromArgb(96, 107, 120);
                retry.SetBounds(28, failed ? 240 : 232, 306, failed ? 32 : 34);
                exit.SetBounds(344, retry.Top, 108, retry.Height);
                StyleButton(retry, true);
                if (completed.Status == "select-microphone")
                    status.Text = T("选择用于自检的麦克风", "Choose your microphone before testing");
                retry.Text = T("开始自检", "Run test");
                retry.Enabled = microphone.SelectedItem != null;
                detail.Text = (completed.FailureKind != AudioFailureKind.None ? AudioFailure.Advice(completed.FailureKind, english) + "\n" : "") + T("请确保麦克风已开启，声卡或耳机未处于静音状态。", "Make sure your microphone is on and unmuted.");
                if (microphone.Items.Count == 0)
                    detail.Text = T("未检测到麦克风，请连接或启用后点击刷新。", "No microphone found. Connect or enable one, then refresh.");
                if (!String.IsNullOrEmpty(microphoneError))
                {
                    detail.Text = T("无法读取麦克风列表，请点击刷新或查看日志。", "Cannot list microphones. Refresh or open the diagnostic log.");
                    errors.SetToolTip(detail, microphoneError + "\n" + Diagnostics.Folder);
                }

                microphone.AccessibleName = T("自检麦克风", "Microphone for startup test");
            }

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                string text = "KeepClear";
                float width = g.MeasureString(text, brand, PointF.Empty, format).Width;
                float x = (ClientSize.Width - width - 12) / 2f, y = (result != null && result.ChooseMicrophone ? 80 : ClientSize.Height * 0.47f) - brand.GetHeight(g) * 0.5f;
                double elapsed = clock.Elapsed.TotalSeconds, progress = Math.Max(0, Math.Min(1, (elapsed - 0.28) / typingDuration));
                int[] weights =
                {
                    130,
                    90,
                    110,
                    150,
                    160,
                    110,
                    85,
                    100,
                    130
                };
                double total = 1065, begin = 0;
                float reveal = 0;
                for (int i = 0; i < 9; i++)
                {
                    double duration = weights[i] / total;
                    float end = g.MeasureString(text.Substring(0, i + 1), brand, PointF.Empty, format).Width;
                    if (progress <= begin + duration || i == 8)
                    {
                        double phase = Math.Max(0, Math.Min(1, (progress - begin) / duration / 0.75));
                        reveal += (end - reveal) * (float)(1 - Math.Pow(1 - phase, 3));
                        break;
                    }

                    reveal = end;
                    begin += duration;
                }

                var saved = g.Save();
                g.SetClip(new RectangleF(x, y, reveal, brand.GetHeight(g) * 1.3f));
                using (var ink = new SolidBrush(Color.Black))
                    g.DrawString(text, brand, ink, x, y, format);
                g.Restore(saved);
                double opacity = elapsed < TypingEnd ? 1 : Math.Floor((elapsed - TypingEnd) / 0.525) % 2 == 0 ? 1 : 0;
                if (result != null && result.Ready && elapsed >= Math.Max(TypingEnd, finishedAt))
                    opacity = Math.Max(0, 1 - (elapsed - Math.Max(TypingEnd, finishedAt)) / 0.45);
                using (var blue = new SolidBrush(Color.FromArgb((int)(255 * opacity), 22, 102, 197)))
                    g.FillRectangle(blue, x + reveal + 5, y + brand.GetHeight(g) * 0.17f, 3, brand.GetHeight(g) * 0.69f);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                cancellation.Cancel();
                timer.Dispose();
                brand.Dispose();
                errors.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    internal sealed class StartupContext : ApplicationContext
    {
        private readonly StartupForm startup;
        private MainForm app;
        internal StartupContext() : this(() => new StartupForm((progress, token) => StartupCheck.Run(true, progress, token), AppSettings.Load().Language == "en", null, null, selected =>
        {
            var settings = AppSettings.Load();
            settings.Language = selected;
            settings.Save();
        }), () => new MainForm())
        {
        }

        internal StartupContext(Func<StartupForm> startupFactory, Func<MainForm> applicationFactory)
        {
            startup = startupFactory();
            MainForm = startup;
            startup.Ready += delegate
            {
                app = applicationFactory();
                app.ApplyStartupResult(startup.Result);
                MainForm = app;
                app.Show();
                startup.Close();
                startup.Dispose();
            };
            startup.Show();
        }

        internal void RestoreWindow()
        {
            if (app != null)
                app.RestoreWindow();
            else
                startup.RestoreWindow();
        }
    }
}

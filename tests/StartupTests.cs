using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace KeepQuiet
{
    internal static class StartupTests
    {
        internal static void Run(Action<bool, string> check)
        {
            string preferencePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "artifacts", "language-settings", Guid.NewGuid().ToString("N"), "settings.xml"));
            check(AppSettings.Load(preferencePath).Language == "en", "fresh profile defaults to English");
            Directory.CreateDirectory(Path.GetDirectoryName(preferencePath));
            File.WriteAllText(preferencePath, "<AppSettings><InputId>saved microphone</InputId></AppSettings>");
            var legacy = AppSettings.Load(preferencePath);
            check(legacy.Language == "en" && legacy.InputId == "saved microphone", "old profile without language defaults to English and preserves devices");
            var preferences = new AppSettings
            {
                Language = "zh",
                InputId = "microphone",
                OutputId = "cable",
                MonitorId = "headphones",
                GainDb = 7
            };
            preferences.Save(preferencePath);
            var restored = AppSettings.Load(preferencePath);
            check(restored.Language == "zh", "saved Chinese preference survives reload");
            restored.Language = "en";
            restored.Save(preferencePath);
            restored = AppSettings.Load(preferencePath);
            check(restored.Language == "en" && restored.InputId == "microphone" && restored.OutputId == "cable" && restored.MonitorId == "headphones" && restored.GainDb == 7, "language change persists without replacing audio preferences");
            File.WriteAllText(preferencePath, "<AppSettings><Alkove>false</Alkove><Denoiser>false</Denoiser><InputId>legacy microphone</InputId><OutputId>legacy cable</OutputId><MonitorId>legacy headphones</MonitorId><Language>zh</Language><GainDb>7</GainDb><Python>python.exe</Python></AppSettings>");
            restored = AppSettings.Load(preferencePath);
            check(restored.InputId == "legacy microphone" && restored.OutputId == "legacy cable" && restored.MonitorId == "legacy headphones" && restored.Language == "zh" && restored.GainDb == 7 && restored.Python == AppSettings.FindPython(AppDomain.CurrentDomain.BaseDirectory), "obsolete model flags do not prevent loading legacy audio/language preferences or resolving Python");
            restored.Save(preferencePath);
            string cleaned = File.ReadAllText(preferencePath);
            check(!cleaned.Contains("<Alkove>") && !cleaned.Contains("<Denoiser>") && AppSettings.Load(preferencePath).GainDb == 7, "saved preferences omit obsolete model flags and preserve output gain");
            File.WriteAllText(preferencePath, "<AppSettings><Language>unknown</Language></AppSettings>");
            check(AppSettings.Load(preferencePath).Language == "en", "invalid saved language falls back to English");
            DriverChecks.Run(check);
            int launches = 0;
            var state = VbCableInstaller.Ensure(true, delegate (string step)
            {
            }, () => CableState.Ready, () =>
            {
                launches++;
                return 0;
            });
            check(state == CableState.Ready && launches == 0, "installed cable never invokes installer");
            state = VbCableInstaller.Ensure(true, delegate (string step)
            {
            }, () => CableState.Disabled, () =>
            {
                launches++;
                return 0;
            });
            check(state == CableState.Disabled && launches == 0, "disabled installation is not reinstalled");
            state = VbCableInstaller.Ensure(false, delegate (string step)
            {
            }, () => CableState.Missing, () =>
            {
                launches++;
                return 0;
            });
            check(state == CableState.Missing && launches == 0, "read-only startup check cannot install driver");
            state = VbCableInstaller.Ensure(true, delegate (string step)
            {
            }, () => CableState.Missing, () =>
            {
                launches++;
                return 3010;
            });
            check(state == CableState.RestartRequired && launches == 1, "missing cable launches once and reports restart required");
            int reads = 0;
            state = VbCableInstaller.Ensure(true, delegate (string step)
            {
            }, () => ++reads == 1 ? CableState.Missing : CableState.Ready, () =>
            {
                launches++;
                return 0;
            });
            check(state == CableState.Ready && launches == 1, "race recheck prevents duplicate driver installation");
            bool cancelled = false;
            try
            {
                VbCableInstaller.Ensure(true, delegate (string step)
                {
                }, () => CableState.Missing, () =>
                {
                    throw new Win32Exception(1223);
                });
            }
            catch (Win32Exception ex)
            {
                cancelled = ex.NativeErrorCode == 1223;
            }

            check(cancelled, "UAC cancellation is preserved without an automatic retry loop");
            bool failed = false;
            try
            {
                VbCableInstaller.Ensure(true, delegate (string step)
                {
                }, () => CableState.Missing, () => 5);
            }
            catch (InvalidOperationException)
            {
                failed = true;
            }

            check(failed, "failed driver setup cannot become ready");
            string stage = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "artifacts", "driver-extraction-test");
            bool request = false;
            int code = VbCableInstaller.Install(stage, info =>
            {
                request = info.Verb == "runas" && info.UseShellExecute && info.Arguments == "-i -h" && File.Exists(info.FileName) && File.Exists(Path.Combine(info.WorkingDirectory, "readme.txt")) && Directory.GetFiles(info.WorkingDirectory, "*.sys").Length >= 9;
                return 3010; // A test callback never executes the installer.
            });
            check(request && code == 3010, "original embedded package extracts intact; installation uses Windows elevation");
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KeepQuiet.VBCable.zip"))
                check(stream != null && VbCableInstaller.Hash(stream) == VbCableInstaller.ArchiveHash, "embedded original driver ZIP matches pinned SHA256");
        }

        private static void Pump(Func<bool> done, int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < milliseconds)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }

            if (!done())
                throw new Exception("Startup UI timeout");
        }

        internal static void Render(string directory)
        {
            Directory.CreateDirectory(directory);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            foreach (bool english in new[]
            {
                false,
                true
            })
                foreach (string mode in new[]
                {
                    "ready",
                    "missing-driver",
                    "restart-required",
                    "cancelled",
                    "inactive-driver",
                    "incomplete-driver",
                    "installer-busy",
                    "driver-detection-failed",
                    "audio-failed",
                    "driver-pending"
                })
                    using (var form = new StartupForm((progress, token) => new StartupResult { Ready = mode == "ready", Status = mode, CanRetry = mode != "restart-required" }, english, () =>
                    {
                        throw new Exception("Unexpected restart confirmation");
                    }, () =>
                    {
                        throw new Exception("Tests must never restart Windows");
                    }))
                    {
                        bool proceeded = false;
                        form.Ready += delegate
                        {
                            proceeded = true;
                        };
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-30000, -30000);
                        form.Show();
                        if (mode == "ready")
                        {
                            Pump(() => proceeded, 5000);
                        }
                        else
                        {
                            Pump(() => form.Controls.OfType<Button>().Any(b => b.Visible), 1000);
                            var clock = Stopwatch.StartNew();
                            while (clock.ElapsedMilliseconds < 2150)
                            {
                                Application.DoEvents();
                                Thread.Sleep(10);
                            }

                            if (proceeded)
                                throw new Exception("Failed startup continued into main UI");
                        }

                        foreach (var label in form.Controls.OfType<Label>())
                            if (TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, 1000), TextFormatFlags.WordBreak).Height > label.Height)
                                throw new Exception("Clipped startup text: " + label.Text);
                        var restart = form.Controls.OfType<Button>().Single(b => b.Text == (english ? "Restart PC" : "重启电脑"));
                        var exit = form.Controls.OfType<Button>().Single(b => b.Text == (english ? "Exit" : "退出"));
                        var language = form.Controls.OfType<Button>().Single(b => b.Name == "startupLanguage");
                        if (language.Visible != (mode != "ready"))
                            throw new Exception("Language switch shown in the wrong startup state");
                        if (language.Visible && (!form.ClientRectangle.Contains(language.Bounds) || language.Bottom > form.Controls.OfType<Label>().Min(l => l.Top)))
                            throw new Exception("Language switch overlaps startup content");
                        if (restart.Visible != (mode == "restart-required" || mode == "driver-pending"))
                            throw new Exception("Restart button shown in the wrong startup state");
                        if (restart.Visible && (restart.Right >= exit.Left || restart.Top != exit.Top || restart.Height != exit.Height))
                            throw new Exception("Restart and exit buttons are not aligned");
                        foreach (var button in form.Controls.OfType<Button>().Where(b => b.Visible))
                            if (TextRenderer.MeasureText(button.Text, button.Font).Width > button.ClientSize.Width - 6)
                                throw new Exception("Clipped startup button: " + button.Text);
                        using (var bitmap = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                            bitmap.Save(Path.Combine(directory, (english ? "en" : "zh") + "-" + mode + ".png"));
                        }

                        form.Close();
                    }

            RestartActions();
            LanguageActions(directory);
            Transition(directory);
            MicrophoneChoice(directory);
            File.WriteAllText(Path.Combine(directory, "report.txt"), "PASS: bilingual startup states including driver-pending; compact left-aligned microphone selector and aligned refresh/actions; confirmation before audio checks; Run test replays full 2.6-second typing even when checking completes immediately; missing mic does not fall back; busy error permits selection/retry; language switches without repeating tests; exact microphone saved/carried into main UI; cross-thread UI access checks; restart/language buttons and layouts; isolated preferences only; no actual installer or Windows restart executed.");
            Console.WriteLine("PASS: startup animation/state UI and restart actions; no installer or Windows restart executed");
        }

        internal static void MicrophoneChoice(string directory)
        {
            Directory.CreateDirectory(directory);
            Control.CheckForIllegalCrossThreadCalls = true;
            foreach (bool english in new[]
            {
                false,
                true
            })
            {
                string path = Path.Combine(directory, "microphone-choice-" + (english ? "en" : "zh") + ".xml");
                new AppSettings
                {
                    InputId = "mic-a",
                    OutputId = "saved-cable",
                    Language = english ? "en" : "zh",
                    GainDb = 5
                }.Save(path);
                int preflight = 0, calls = 0;
                bool promoted = false;
                var first = new AudioDevice
                {
                    Id = "mic-a",
                    Name = "USB microphone A"
                };
                var second = new AudioDevice
                {
                    Id = "mic-b",
                    Name = "USB microphone B"
                };
                AudioDevice[] devices =
                {
                    first,
                    second
                };
                using (var form = new StartupForm((p, t) =>
                {
                    preflight++;
                    return new StartupResult
                    {
                        Status = "select-microphone",
                        ChooseMicrophone = true
                    };
                }, english, null, null, selected =>
                {
                    var saved = AppSettings.Load(path);
                    saved.Language = selected;
                    saved.Save(path);
                }, (id, p, t) =>
                {
                    calls++;
                    if (id != "mic-b")
                        throw new Exception("Test opened a microphone other than the selected one");
                    return calls == 1 ? new StartupResult
                    {
                        Status = "audio-failed",
                        ChooseMicrophone = true,
                        FailureKind = AudioFailureKind.Busy,
                        Detail = "Device in exclusive use (0x8889000A)"
                    }

                    : new StartupResult
                    {
                        Ready = true,
                        Status = "ready",
                        InputId = id,
                        OutputId = "saved-cable"
                    };
                }, () => devices, () => AppSettings.Load(path).InputId, id =>
                {
                    var saved = AppSettings.Load(path);
                    saved.InputId = id;
                    saved.Save(path);
                }))
                {
                    form.Ready += () => promoted = true;
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-30000, -30000);
                    form.Show();
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    var picker = form.Controls.OfType<ComboBox>().Single();
                    var refresh = form.Controls.OfType<Button>().Single(b => b.Name == "refreshMicrophone");
                    Pump(() => picker.Visible, 2000);
                    var animation = Stopwatch.StartNew();
                    while (animation.ElapsedMilliseconds < 2150)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }

                    if (calls != 0 || picker.SelectedItem != first)
                        throw new Exception("Self-test began without explicit confirmation or saved microphone was not selected");
                    devices = new[]
                    {
                        second
                    };
                    refresh.PerformClick();
                    var run = form.Controls.OfType<Button>().Single(b => b.Text == (english ? "Run test" : "开始自检"));
                    if (picker.SelectedIndex != -1 || run.Enabled)
                        throw new Exception("Missing selected mic silently fell back to another device");
                    picker.SelectedItem = second;
                    UiChecks.CheckSelectorColors(picker);
                    var hover = (ToolTip)typeof(StartupForm).GetField("errors", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    if (form.Controls.OfType<Label>().Any(label => label.AutoEllipsis || !String.IsNullOrEmpty(hover.GetToolTip(label))))
                        throw new Exception("Ordinary startup instructions still produce a hover popup");
                    foreach (var label in form.Controls.OfType<Label>())
                        if (TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, 1000), TextFormatFlags.WordBreak).Height > label.Height)
                            throw new Exception("Clipped microphone selector label: " + label.Text);
                    var instructions = form.Controls.OfType<Label>().OrderByDescending(l => l.Top).First();
                    var caption = form.Controls.OfType<Label>().OrderBy(l => l.Top).First();
                    if (caption.Left != picker.Left || instructions.Left != picker.Left || run.Left != picker.Left || run.Right >= form.Controls.OfType<Button>().Single(b => b.Text == (english ? "Exit" : "退出")).Left)
                        throw new Exception("Compact microphone controls are not aligned");
                    if (picker.DropDownWidth != picker.Width || picker.Top != refresh.Top || picker.Height != refresh.Height)
                        throw new Exception("Microphone selector/refresh alignment changed");
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.Combine(directory, (english ? "en" : "zh") + "-microphone-choice.png"));
                    }

                    run.PerformClick();
                    Pump(() => picker.Visible && calls == 1 && form.Controls.OfType<Button>().Single(b => b.Name == "startupLanguage").Visible, 3000);
                    if (promoted || preflight != 1 || picker.SelectedItem != second || !form.Controls.OfType<Label>().Any(l => l.Text.Contains(english ? "in use" : "占用")))
                        throw new Exception("Busy mic failure lost selection, error category or retry UI");
                    if (String.IsNullOrEmpty(hover.GetToolTip(form.Controls.OfType<Label>().OrderByDescending(l => l.Top).First())))
                        throw new Exception("Actionable error hover details were removed");
                    var language = form.Controls.OfType<Button>().Single(b => b.Name == "startupLanguage");
                    language.PerformClick();
                    if (!form.Controls.OfType<Label>().Any(l => l.Text.Contains(english ? "占用" : "in use")) || calls != 1)
                        throw new Exception("Error language switch repeated audio check or lost localized reason");
                    language.PerformClick();
                    foreach (var label in form.Controls.OfType<Label>())
                        if (TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, 1000), TextFormatFlags.WordBreak).Height > label.Height)
                            throw new Exception("Clipped microphone error: " + label.Text);
                    foreach (AudioFailureKind kind in Enum.GetValues(typeof(AudioFailureKind)))
                    {
                        string message = AudioFailure.Advice(kind, english) + "\n" + (english ? "Make sure your microphone is on and unmuted." : "请确保麦克风已开启，声卡或耳机未处于静音状态。");
                        if (TextRenderer.MeasureText(message, instructions.Font, new Size(instructions.Width, 1000), TextFormatFlags.WordBreak).Height > instructions.Height)
                            throw new Exception("Localized microphone guidance does not fit: " + kind);
                    }

                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.Combine(directory, (english ? "en" : "zh") + "-microphone-busy.png"));
                    }

                    var replay = Stopwatch.StartNew();
                    run.PerformClick();
                    Pump(() => form.Result != null && form.Result.Ready, 1500);
                    if (promoted)
                        throw new Exception("A fast audio check skipped the replayed typing animation");
                    Pump(() => promoted, 5000);
                    if (replay.ElapsedMilliseconds < 3100)
                        throw new Exception("Run test did not replay the 2.6-second KeepQuiet typing animation");
                    var savedChoice = AppSettings.Load(path);
                    if (form.Result.InputId != "mic-b" || savedChoice.InputId != "mic-b" || savedChoice.OutputId != "saved-cable" || savedChoice.GainDb != 5)
                        throw new Exception("Confirmed microphone did not persist through successful test");
                    form.Close();
                }

                using (var main = new MainForm(new AppSettings { Language = english ? "en" : "zh" }))
                {
                    main.StartPosition = FormStartPosition.Manual;
                    main.Location = new Point(-30000, -30000);
                    main.Show();
                    Application.DoEvents();
                    var input = (ComboBox)typeof(MainForm).GetField("input", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(main);
                    input.Items.Clear();
                    input.Items.AddRange(new object[] { first, second });
                    main.ApplyStartupResult(new StartupResult { Ready = true, InputId = second.Id });
                    if (input.SelectedItem != second)
                        throw new Exception("Main window did not retain user's confirmed microphone");
                    main.ApplyStartupResult(new StartupResult { Ready = true, InputId = "disconnected-after-test" });
                    if (input.SelectedIndex != -1)
                        throw new Exception("Main window silently replaced a confirmed disconnected mic");
                }

                promoted = false;
                using (var form = new StartupForm((p, t) => new StartupResult { Status = "select-microphone", ChooseMicrophone = true }, english, null, null, selected =>
                {
                }, (id, p, t) => new StartupResult { Ready = true, Status = "ready", InputId = id }, () => new[] { first }, () => first.Id, id =>
                {
                    throw new UnauthorizedAccessException("isolated save failure");
                }))
                {
                    form.Ready += () => promoted = true;
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-30000, -30000);
                    form.Show();
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    Pump(() => form.Controls.OfType<Button>().Any(b => b.Visible && b.Text == (english ? "Run test" : "开始自检")), 2000);
                    form.Controls.OfType<Button>().Single(b => b.Text == (english ? "Run test" : "开始自检")).PerformClick();
                    Pump(() => promoted, 5000);
                    if (!form.Result.InputSaveFailed || form.Result.InputId != first.Id)
                        throw new Exception("Preference save failure blocked current microphone use or lost its warning");
                    using (var main = new MainForm(new AppSettings { Language = english ? "en" : "zh" }))
                    {
                        main.ApplyStartupResult(form.Result);
                        var hint = (Label)typeof(MainForm).GetField("hint", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(main);
                        if (!hint.Text.Contains(english ? "save" : "保存"))
                            throw new Exception("Failed microphone preference save was not disclosed in the main UI");
                    }

                    form.Close();
                }
            }
        }

        private static void LanguageActions(string directory)
        {
            foreach (bool english in new[]
            {
                false,
                true
            })
                foreach (string mode in new[]
                {
                    "missing-driver",
                    "missing-files",
                    "damaged-files",
                    "inactive-driver",
                    "cancelled",
                    "failed",
                    "restart-required",
                    "incomplete-driver",
                    "installer-busy",
                    "driver-detection-failed",
                    "audio-failed",
                    "driver-pending"
                })
                {
                    string path = Path.Combine(directory, "settings-" + (english ? "en" : "zh") + "-" + mode + ".xml");
                    new AppSettings
                    {
                        Language = english ? "en" : "zh",
                        InputId = "keep microphone",
                        MonitorId = "keep headphones",
                        GainDb = 5
                    }.Save(path);
                    int checks = 0, saves = 0;
                    using (var form = new StartupForm((p, t) =>
                    {
                        Interlocked.Increment(ref checks);
                        return new StartupResult
                        {
                            Status = mode,
                            Detail = "models/example.model",
                            CanRetry = mode != "restart-required"
                        };
                    }, english, () =>
                    {
                        throw new Exception("Unexpected confirmation");
                    }, () =>
                    {
                        throw new Exception("Unexpected restart");
                    }, selected =>
                    {
                        var saved = AppSettings.Load(path);
                        saved.Language = selected;
                        saved.Save(path);
                        saves++;
                    }))
                    {
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-30000, -30000);
                        form.Show();
                        var language = form.Controls.OfType<Button>().Single(b => b.Name == "startupLanguage");
                        Pump(() => language.Visible, 1000);
                        string[] before = form.Controls.OfType<Label>().Select(l => l.Text).ToArray();
                        language.PerformClick();
                        Application.DoEvents();
                        var saved = AppSettings.Load(path);
                        if (saved.Language != (english ? "zh" : "en") || saved.InputId != "keep microphone" || saved.MonitorId != "keep headphones" || saved.GainDb != 5)
                            throw new Exception("Startup language did not persist or replaced audio settings: " + mode);
                        if (language.Text != (english ? "English" : "中文") || before.SequenceEqual(form.Controls.OfType<Label>().Select(l => l.Text)))
                            throw new Exception("Blocked startup text did not translate immediately: " + mode);
                        language.PerformClick();
                        Application.DoEvents();
                        if (checks != 1 || saves != 2 || AppSettings.Load(path).Language != (english ? "en" : "zh") || !before.SequenceEqual(form.Controls.OfType<Label>().Select(l => l.Text)))
                            throw new Exception("Language switch repeated checks or failed to restore text: " + mode);
                        form.Close();
                    }
                }
        }

        private static void RestartActions()
        {
            foreach (bool english in new[]
            {
                false,
                true
            })
                foreach (string mode in new[]
                {
                    "restart-required",
                    "driver-pending"
                })
                {
                    bool confirmed = false;
                    int requests = 0, exitCode = 5;
                    using (var form = new StartupForm((p, t) => new StartupResult { Status = mode, CanRetry = mode != "restart-required" }, english, () => confirmed, () =>
                    {
                        Interlocked.Increment(ref requests);
                        return exitCode;
                    }))
                    {
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-30000, -30000);
                        form.Show();
                        var restart = form.Controls.OfType<Button>().Single(b => b.Text == (english ? "Restart PC" : "重启电脑"));
                        var exit = form.Controls.OfType<Button>().Single(b => b.Text == (english ? "Exit" : "退出"));
                        Pump(() => restart.Visible, 1000);
                        restart.PerformClick();
                        Application.DoEvents();
                        if (requests != 0 || !form.Visible)
                            throw new Exception("Cancelled confirmation requested a restart");
                        confirmed = true;
                        restart.PerformClick();
                        Pump(() => requests == 1 && restart.Enabled, 3000);
                        if (!form.Visible || !exit.Enabled || !form.Controls.OfType<Label>().Any(l => l.Text.Contains(english ? "Unable to restart" : "无法重启")))
                            throw new Exception("Failed restart did not preserve an actionable startup window");
                        var language = form.Controls.OfType<Button>().Single(b => b.Name == "startupLanguage");
                        language.PerformClick();
                        if (!form.Controls.OfType<Label>().Any(l => l.Text.Contains(english ? "无法重启" : "Unable to restart")))
                            throw new Exception("Restart failure did not translate with language change");
                        language.PerformClick();
                        exitCode = 0;
                        restart.PerformClick();
                        Pump(() => !form.Visible, 3000);
                        if (requests != 2)
                            throw new Exception("Confirmed restart was not requested exactly once");
                    }
                }
        }

        internal static void HardwareChoice(string directory)
        {
            Directory.CreateDirectory(directory);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Control.CheckForIllegalCrossThreadCalls = true;
            var microphone = NativeAudio.Devices(true).First(d => !d.IsCable && String.IsNullOrEmpty(d.IdentityError));
            bool finished = false;
            int audioChecks = 0;
            string confirmed = null;
            using (var form = new StartupForm((p, t) => StartupCheck.Run(false, p, t), true, null, null, selected =>
            {
            }, (id, p, t) =>
            {
                Interlocked.Increment(ref audioChecks);
                return StartupCheck.Run(false, p, t, true, id);
            }, () => NativeAudio.Devices(true).Where(d => !d.IsCable && String.IsNullOrEmpty(d.IdentityError)).ToArray(), () => microphone.Id, id => confirmed = id))
            {
                form.Ready += () => finished = true;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.Show();
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                var picker = form.Controls.OfType<ComboBox>().Single();
                Pump(() => picker.Visible, 10000);
                if (audioChecks != 0 || form.Result.Status != "select-microphone")
                    throw new Exception("Real startup skipped explicit microphone selection");
                form.Controls.OfType<Button>().Single(b => b.Text == "Run test").PerformClick();
                Pump(() => finished || (form.Result != null && form.Result.Status == "audio-failed"), 60000);
                if (!finished || form.Result.InputId != microphone.Id || confirmed != microphone.Id || audioChecks != 1)
                    throw new Exception("Real confirmed microphone chain failed: " + (form.Result == null ? "no result" : form.Result.Detail));
                using (var main = new MainForm(new AppSettings { Language = "en" }))
                {
                    main.ApplyStartupResult(form.Result);
                    main.StartPosition = FormStartPosition.Manual;
                    main.Location = new Point(-30000, -30000);
                    main.Show();
                    Application.DoEvents();
                    var input = (ComboBox)typeof(MainForm).GetField("input", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(main);
                    if (((AudioDevice)input.SelectedItem).Id != confirmed)
                        throw new Exception("Real main window changed the confirmed microphone");
                }

                File.WriteAllText(Path.Combine(directory, "report.txt"), "PASS: read-only dependency preparation; explicit microphone confirmation before one real silent dual-model/CABLE test; selected microphone carried exactly into main window; no installer, recording or real settings write.");
                Console.WriteLine("PASS: actual startup microphone selection, silent chain and main-window handoff");
                form.Close();
            }
        }

        private static void Transition(string directory)
        {
            string key = "KeepQuiet.StartupTest." + Guid.NewGuid().ToString("N");
            bool canProceed = false;
            var preferences = new AppSettings
            {
                Language = "zh"
            };
            using (var context = new StartupContext(() =>
            {
                var form = new StartupForm((p, t) => new StartupResult { Ready = canProceed, Status = canProceed ? "ready" : "missing-driver" }, false, null, null, selected => preferences.Language = selected);
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                return form;
            }, () =>
            {
                var form = new MainForm(preferences);
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                return form;
            }))
            using (var instance = new SingleInstance(key))
            {
                instance.Listen(context.RestoreWindow);
                var startup = (StartupForm)context.MainForm;
                var language = startup.Controls.OfType<Button>().Single(b => b.Name == "startupLanguage");
                Pump(() => language.Visible, 1000);
                language.PerformClick();
                canProceed = true;
                startup.Controls.OfType<Button>().Single(b => b.Text == "Retry").PerformClick();
                Pump(() => context.MainForm is MainForm, 5000);
                var main = (MainForm)context.MainForm;
                main.Close();
                Application.DoEvents();
                if (!(bool)typeof(MainForm).GetField("english", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(main))
                    throw new Exception("Startup language preference did not carry into main UI");
                if (main.Visible || main.IsDisposed)
                    throw new Exception("Main window did not hide after startup");
                string report = Path.GetFullPath(Path.Combine(directory, "transition-secondary.txt"));
                using (var child = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--instance-secondary " + key + " \"" + report + "\"") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    if (!child.WaitForExit(4000))
                    {
                        child.Kill();
                        throw new Exception("Startup activation timed out");
                    }

                    if (child.ExitCode != 0)
                        throw new Exception("Startup mutex was not retained");
                }

                Pump(() => main.Visible, 3000);
                var exit = (System.Threading.Tasks.Task)typeof(MainForm).GetMethod("ExitApplication", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(main, null);
                Pump(() => exit.IsCompleted && main.IsDisposed, 3000);
                exit.GetAwaiter().GetResult();
            }
        }
    }
}

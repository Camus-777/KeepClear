using System;
using System.IO;
using System.Threading;

namespace KeepQuiet
{
    internal sealed class StartupResult
    {
        public bool Ready;
        public string Status, Detail;
        public bool CanRetry = true;
        internal string InputId, OutputId;
        internal bool InputMuted;
        internal bool InputSaveFailed;
        internal bool ChooseMicrophone;
        internal AudioFailureKind FailureKind;
    }

    internal static class StartupCheck
    {
        internal static StartupResult Run(bool install, Action<string> progress, CancellationToken token, bool audioTest = true, string selectedInput = null)
        {
            string stage = "startup";
            Action<string> report = key =>
            {
                token.ThrowIfCancellationRequested();
                stage = key;
                Diagnostics.Write("startup-stage", key);
                progress(key);
            };
            try
            {
                return Check(install, report, token, audioTest, selectedInput);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Diagnostics.Failure(stage, ex);
                bool audio = stage == "checking-audio" || stage == "testing-audio" || stage == "recovering-audio";
                return new StartupResult
                {
                    Status = ex.NativeErrorCode == 1223 ? "cancelled" : audio ? "audio-failed" : "failed",
                    ChooseMicrophone = audio,
                    FailureKind = audio ? AudioFailure.Classify(stage, ex) : AudioFailureKind.None,
                    Detail = stage + ": " + ex.Message + " (" + Diagnostics.Code(ex) + ")"
                };
            }
            catch (Exception ex)
            {
                Diagnostics.Failure(stage, ex);
                bool audio = stage == "checking-audio" || stage == "testing-audio" || stage == "recovering-audio";
                return new StartupResult
                {
                    Status = audio ? "audio-failed" : "failed",
                    ChooseMicrophone = audio,
                    FailureKind = audio ? AudioFailure.Classify(stage, ex) : AudioFailureKind.None,
                    Detail = stage + ": " + ex.Message + " (" + Diagnostics.Code(ex) + ")"
                };
            }
        }

        private static StartupResult Check(bool install, Action<string> progress, CancellationToken token, bool audioTest, string selectedInput)
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            progress("checking-runtime");
            token.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(root, "workers", "model_worker.py")))
                return new StartupResult
                {
                    Status = "missing-files",
                    Detail = "workers/model_worker.py"
                };
            progress("checking-models");
            string[] names =
            {
                "alkove/alkove-focus-1-preview.alk",
                "alkove/alkove-cue-1-preview.alk",
                "denoiser/dns48-state.pt"
            };
            string[] hashes =
            {
                "a63cda32e023b8d76089f4c3df0cc2c69ba36017b8f6cdea1da1094ef8022b6a",
                "19e8b6c66ee9ce23bba3b8011f03827c5ba4bb2fe8bec25fe07558a78950190a",
                "11decc9d8e3f0998a64caed6b57b68a40cb200a627996a636625f20bdb91f1d6"
            };
            for (int i = 0; i < names.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                string path = Path.Combine(root, "models", names[i]);
                if (!File.Exists(path))
                    return new StartupResult
                    {
                        Status = "missing-files",
                        Detail = names[i]
                    };
                using (var file = File.OpenRead(path))
                    if (VbCableInstaller.Hash(file) != hashes[i])
                        return new StartupResult
                        {
                            Status = "damaged-files",
                            Detail = names[i]
                        };
            }

            progress("checking-driver");
            token.ThrowIfCancellationRequested();
            CableState cable = VbCableInstaller.Ensure(install, progress);
            token.ThrowIfCancellationRequested();
            if (cable == CableState.Missing)
                return new StartupResult
                {
                    Status = "missing-driver"
                };
            if (cable == CableState.Disabled)
                return new StartupResult
                {
                    Status = "inactive-driver"
                };
            if (cable == CableState.Incomplete)
                return new StartupResult
                {
                    Status = "incomplete-driver"
                };
            if (cable == CableState.InstallBusy)
                return new StartupResult
                {
                    Status = "installer-busy"
                };
            if (cable == CableState.DetectionFailed)
                return new StartupResult
                {
                    Status = "driver-detection-failed"
                };
            if (cable == CableState.RestartRequired)
                return new StartupResult
                {
                    Status = "restart-required",
                    CanRetry = false
                };
            if (cable == CableState.Pending)
                return new StartupResult
                {
                    Status = "driver-pending"
                };
            var settings = AppSettings.Load();
            if (audioTest)
            {
                if (String.IsNullOrEmpty(selectedInput))
                    return new StartupResult
                    {
                        Status = "select-microphone",
                        ChooseMicrophone = true
                    };
                return AudioStartupTest.Run(root, settings, selectedInput, progress, token);
            }

            progress("checking-components");
            using (var worker = new ModelWorker(AppSettings.ResolvePython(root, settings.Python), root, true, true, token))
                for (int i = 0; i < 8; i++)
                {
                    token.ThrowIfCancellationRequested();
                    worker.Process(new float[256]);
                }

            return new StartupResult
            {
                Ready = true,
                Status = "ready"
            };
        }
    }
}

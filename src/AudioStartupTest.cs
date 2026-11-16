using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace KeepClear
{
    internal static class AudioStartupTest
    {
        internal static AudioDevice SelectedInput(AudioDevice[] devices, string selected)
        {
            return devices.FirstOrDefault(d => d.Id == selected && !d.IsCable && String.IsNullOrEmpty(d.IdentityError));
        }

        internal static AudioDevice ChooseOutput(AudioDevice[] devices, string saved)
        {
            var cable = devices.Where(d => !d.Capture && d.IsCable && String.IsNullOrEmpty(d.IdentityError)).ToArray();
            return cable.FirstOrDefault(d => d.Id == saved)
                ?? cable.FirstOrDefault(d => d.FormFactor == 1) // Speakers: the regular CABLE Input endpoint.
                ?? cable.FirstOrDefault(d => d.FormFactor < 0 && (String.Equals(d.Name, "CABLE Input", StringComparison.OrdinalIgnoreCase) || (d.Name ?? "").StartsWith("CABLE Input (", StringComparison.OrdinalIgnoreCase)))
                ?? cable.FirstOrDefault();
        }

        internal static bool HasFlow(EngineSnapshot current, long readbackFrames)
        {
            return current.State == "运行中" && String.IsNullOrEmpty(current.Error)
                && current.CaptureFrames > 0 && current.ProcessedSamples > 0
                && current.ProcessedRenderFrames > 0 && readbackFrames > 0;
        }

        internal static StartupResult Run(string root, AppSettings settings, string selectedInput, Action<string> progress, CancellationToken token)
        {
            progress("checking-audio");
            var captures = NativeAudio.Devices(true).ToArray();
            var renders = NativeAudio.Devices(false).ToArray();
            var input = SelectedInput(captures, selectedInput);
            var output = ChooseOutput(renders, settings.OutputId);
            if (input == null)
                throw new AudioFailureException(AudioFailureKind.NoMicrophone, "The microphone confirmed before testing is no longer available.");
            if (output == null)
                throw new AudioFailureException(AudioFailureKind.Disconnected, "No active base VB-CABLE render endpoint.");
            var readback = captures.FirstOrDefault(d => d.IsCable && d.Driver.Id == output.Driver.Id);
            if (readback == null)
                throw new AudioFailureException(AudioFailureKind.Readback, "The matching VB-CABLE capture endpoint is unavailable.");
            bool? muted = NativeAudio.Muted(input.Id);
            Diagnostics.Write("audio-test", "input=" + input.Id + " output=" + output.Id + " readback=" + readback.Id + " os_mute=" + muted + " silent_output=True");
            using (var reader = new CableReadback(readback.Id))
            using (var engine = new AudioEngine(new EngineOptions { InputId = input.Id, OutputId = output.Id, Root = root, Python = AppSettings.ResolvePython(root, settings.Python), Alkove = true, Denoiser = true, GainDb = Math.Max(-12, Math.Min(18, settings.GainDb)), SilenceOutput = true }))
            {
                engine.Start();
                var timeout = Stopwatch.StartNew();
                progress("testing-audio");
                while (timeout.ElapsedMilliseconds < 45000)
                {
                    token.ThrowIfCancellationRequested();
                    reader.ThrowIfFailed();
                    var current = engine.Snapshot();
                    if (!String.IsNullOrEmpty(current.Error))
                        throw new AudioFailureException(current.FailureKind, current.Error);
                    if (HasFlow(current, reader.Frames))
                    {
                        Diagnostics.Write("audio-test", "PASS mode=connectivity-only blocks=" + current.Blocks + " processed=" + current.ProcessedSamples + " processed_render_frames=" + current.ProcessedRenderFrames + " readback_frames=" + reader.Frames + " input_queue_ms=" + current.InputQueueMs + " output_queue_ms=" + current.OutputQueueMs + " total_underruns=" + current.Underruns);
                        return new StartupResult
                        {
                            Ready = true,
                            Status = "ready",
                            InputId = input.Id,
                            OutputId = output.Id,
                            InputMuted = muted == true
                        };
                    }

                    Thread.Sleep(20);
                }

                throw new AudioFailureException(AudioFailureKind.Timeout, "Audio startup test timed out while loading models or waiting for stream progress.");
            }
        }

        private sealed class CableReadback : IDisposable
        {
            private readonly CancellationTokenSource stop = new CancellationTokenSource();
            private readonly Thread thread;
            private Exception error;
            private long frames;
            internal long Frames
            {
                get
                {
                    return Interlocked.Read(ref frames);
                }
            }

            internal void ThrowIfFailed()
            {
                var failure = Interlocked.CompareExchange(ref error, null, null);
                if (failure != null)
                    throw new AudioFailureException(AudioFailure.Classify("cable-readback", failure), "VB-CABLE readback failed: " + failure.Message, failure);
            }

            internal CableReadback(string id)
            {
                thread = new Thread(() => Read(id))
                {
                    IsBackground = true,
                    Name = "KeepClear startup cable readback"
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }

            private void Read(string id)
            {
                try
                {
                    using (var endpoint = new AudioEndpoint(id, true))
                    {
                        endpoint.Start();
                        while (!stop.IsCancellationRequested)
                        {
                            int signaled = WaitHandle.WaitAny(new WaitHandle[] { endpoint.Ready, stop.Token.WaitHandle }, 2000);
                            if (signaled == 1)
                                return;
                            if (signaled == WaitHandle.WaitTimeout)
                                throw new TimeoutException("No capture callbacks from VB-CABLE.");
                            uint available;
                            NativeAudio.Check(endpoint.Capture.GetNextPacketSize(out available));
                            while (available > 0 && !stop.IsCancellationRequested)
                            {
                                IntPtr data;
                                uint count, flags;
                                ulong device, clock;
                                NativeAudio.Check(endpoint.Capture.GetBuffer(out data, out count, out flags, out device, out clock));
                                try
                                {
                                    endpoint.Format.Decode(data, (int)count, (flags & 2) != 0, true);
                                }
                                finally
                                {
                                    NativeAudio.Check(endpoint.Capture.ReleaseBuffer(count));
                                }

                                Interlocked.Add(ref frames, count);
                                NativeAudio.Check(endpoint.Capture.GetNextPacketSize(out available));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref error, ex);
                    Diagnostics.Failure("cable-readback", ex);
                }
            }

            public void Dispose()
            {
                stop.Cancel();
                if (!thread.Join(4000))
                    throw new TimeoutException("Cable readback did not stop in time.");
                stop.Dispose();
            }
        }
    }

}

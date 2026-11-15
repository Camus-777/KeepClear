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

        internal static bool MeetsThroughput(EngineSnapshot current, EngineSnapshot baseline, double seconds)
        {
            long blocks = current.Blocks - baseline.Blocks, samples = current.ProcessedSamples - baseline.ProcessedSamples;
            return seconds >= 2.5 && blocks >= seconds * 62.5 * 0.8 && samples >= seconds * 16000 * 0.75 && current.CaptureFrames > baseline.CaptureFrames && current.RenderFrames > baseline.RenderFrames && current.TotalProcessMs - baseline.TotalProcessMs < seconds * 1000 && current.InputQueueMs < 128 && current.OutputQueueMs < 192 && current.Underruns == baseline.Underruns;
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
                AudioObservation observation = null;
                EngineSnapshot current = null;
                while (timeout.ElapsedMilliseconds < 55000)
                {
                    token.ThrowIfCancellationRequested();
                    reader.ThrowIfFailed();
                    current = engine.Snapshot();
                    if (!String.IsNullOrEmpty(current.Error))
                        throw new AudioFailureException(current.FailureKind, current.Error);
                    if (current.State == "运行中" && current.ProcessedSamples >= 2048 && reader.Frames > 0 && observation == null)
                    {
                        observation = new AudioObservation(current, reader.Frames, timeout.Elapsed.TotalSeconds);
                        progress("testing-audio");
                    }

                    if (observation != null)
                    {
                        ObservationResult verdict = observation.Observe(current, reader.Frames, reader.Rate, timeout.Elapsed.TotalSeconds);
                        if (verdict == ObservationResult.Recovering)
                        {
                            progress("recovering-audio");
                            Diagnostics.Write("audio-test", "Transient throughput failure; one recovery window only. blocks=" + current.Blocks + " underruns=" + current.Underruns + " input_queue_ms=" + current.InputQueueMs + " output_queue_ms=" + current.OutputQueueMs);
                        }

                        if (verdict == ObservationResult.Failed)
                            throw new AudioFailureException(AudioFailureKind.Slow, "Audio throughput failed again after one bounded recovery window.");
                        if (verdict == ObservationResult.Passed)
                        {
                            Diagnostics.Write("audio-test", "PASS recovery_used=" + observation.RecoveryUsed + " blocks=" + current.Blocks + " processed=" + current.ProcessedSamples + " readback_frames=" + reader.Frames + " input_queue_ms=" + current.InputQueueMs + " output_queue_ms=" + current.OutputQueueMs + " total_underruns=" + current.Underruns);
                            return new StartupResult
                            {
                                Ready = true,
                                Status = "ready",
                                InputId = input.Id,
                                OutputId = output.Id,
                                InputMuted = muted == true
                            };
                        }
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
            internal int Rate;
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
                        Rate = endpoint.Format.Rate;
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

    internal enum ObservationResult
    {
        Observing,
        Recovering,
        Passed,
        Failed
    }

    internal sealed class AudioObservation
    {
        private EngineSnapshot baseline;
        private long readFrames;
        private double started, settleUntil;
        internal bool RecoveryUsed { get; private set; }

        internal AudioObservation(EngineSnapshot initial, long frames, double now)
        {
            baseline = initial;
            readFrames = frames;
            started = now;
        }

        internal ObservationResult Observe(EngineSnapshot current, long frames, int rate, double now)
        {
            if (baseline == null)
            {
                if (now < settleUntil)
                    return ObservationResult.Observing;
                baseline = current;
                readFrames = frames;
                started = now;
            }

            double seconds = now - started;
            if (seconds < 2.5)
                return ObservationResult.Observing;
            if (AudioStartupTest.MeetsThroughput(current, baseline, seconds) && frames - readFrames >= rate * seconds * 0.75)
                return ObservationResult.Passed;
            if (RecoveryUsed)
                return ObservationResult.Failed;
            RecoveryUsed = true;
            baseline = null;
            settleUntil = now + 0.5;
            return ObservationResult.Recovering;
        }
    }
}

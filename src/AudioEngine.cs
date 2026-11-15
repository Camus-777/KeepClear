using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace KeepClear
{
    public sealed class EngineOptions
    {
        public string InputId, OutputId, Python, Root;
        public bool Alkove, Denoiser;
        public float GainDb;
        internal bool SilenceOutput;
    }

    public sealed class EngineSnapshot
    {
        public string State, Error;
        public double InputPeak, OutputPeak, ProcessMs, InputQueueMs, OutputQueueMs;
        public long Blocks, Underruns, Discontinuities;
        internal long CaptureFrames, RenderFrames, ProcessedSamples;
        internal double TotalProcessMs;
        public int InputRate, OutputRate;
        public bool MonitorActive;
        public string MonitorError;
        internal AudioFailureKind FailureKind;
    }

    public sealed class AudioEngine : IDisposable
    {
        private readonly object gate = new object();
        private readonly object monitorGate = new object();
        private readonly object lifecycleGate = new object();
        private MonitorOutput monitor;
        private readonly EngineOptions options;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly AutoResetEvent inputReady = new AutoResetEvent(false);
        private readonly ManualResetEvent modelsReady = new ManualResetEvent(false);
        private readonly ManualResetEvent renderReady = new ManualResetEvent(false);
        private readonly SampleBuffer input = new SampleBuffer(4096), output = new SampleBuffer(4096);
        private readonly GainLimiter gain = new GainLimiter();
        private Thread captureThread, processThread, renderThread;
        private string state = "准备中", error = "";
        private AudioFailureKind failureKind;
        private double inPeak, outPeak, processMs, renderQueuedMs;
        private int inRate, outRate, started, disposed;
        private long blocks, underruns, discontinuities;
        private long captureFrames, renderFrames, processedSamples, processTicks;
        public AudioEngine(EngineOptions options)
        {
            this.options = options;
            gain.GainDb = options.GainDb;
        }

        private Thread Launch(ThreadStart action, string name)
        {
            var thread = new Thread(action)
            {
                IsBackground = true,
                Name = name
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return thread;
        }

        public void Start()
        {
            lock (lifecycleGate)
            {
                if (disposed != 0)
                    throw new ObjectDisposedException("AudioEngine");
                if (cancellation.IsCancellationRequested || Interlocked.Exchange(ref started, 1) != 0)
                    throw new InvalidOperationException("音频引擎只能启动一次。");
                processThread = Launch(ProcessLoop, "KeepClear processing");
                renderThread = Launch(RenderLoop, "KeepClear WASAPI render");
                captureThread = Launch(CaptureLoop, "KeepClear WASAPI capture");
            }
        }

        public void SetMonitor(string deviceId)
        {
            lock (lifecycleGate)
            {
                if (disposed != 0)
                    throw new ObjectDisposedException("AudioEngine");
                bool enable = !String.IsNullOrEmpty(deviceId);
                if (enable)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    AudioDevice device = NativeAudio.Devices(false).Find(d => d.Id == deviceId);
                    if (device == null || device.IsCable || !String.IsNullOrEmpty(device.IdentityError) || device.Id == options.OutputId)
                        throw new InvalidOperationException("Select headphones or speakers for monitoring, not VB-CABLE.");
                }

                MonitorOutput previous;
                lock (monitorGate)
                    previous = monitor;
                // Keep a failed monitor reachable so another stop can finish disposing it.
                if (previous != null)
                    previous.Dispose();
                lock (monitorGate)
                    monitor = null;
                if (enable)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    lock (monitorGate)
                        monitor = new MonitorOutput(deviceId, cancellation.Token);
                }
            }
        }

        public EngineSnapshot Snapshot()
        {
            lock (gate)
                lock (monitorGate)
                    return new EngineSnapshot
                    {
                        State = state,
                        Error = error,
                        FailureKind = failureKind,
                        InputPeak = inPeak,
                        OutputPeak = outPeak,
                        ProcessMs = processMs,
                        InputQueueMs = input.Count / 16.0,
                        OutputQueueMs = output.Count / 16.0 + renderQueuedMs,
                        InputRate = inRate,
                        OutputRate = outRate,
                        Blocks = Interlocked.Read(ref blocks),
                        Underruns = Interlocked.Read(ref underruns),
                        Discontinuities = Interlocked.Read(ref discontinuities),
                        CaptureFrames = Interlocked.Read(ref captureFrames),
                        RenderFrames = Interlocked.Read(ref renderFrames),
                        ProcessedSamples = Interlocked.Read(ref processedSamples),
                        TotalProcessMs = Interlocked.Read(ref processTicks) * 1000.0 / Stopwatch.Frequency,
                        MonitorActive = monitor != null && monitor.Active,
                        MonitorError = monitor == null ? "" : monitor.Error
                    };
        }

        private void Fault(string stage, Exception ex)
        {
            lock (gate)
            {
                if (cancellation.IsCancellationRequested)
                    return;
                error = stage + ": " + ex.Message + " (" + ex.GetType().Name + ", 0x" + ex.HResult.ToString("X8") + ")";
                failureKind = AudioFailure.Classify(stage, ex);
                state = "已停止：发生错误";
            }

            Diagnostics.Failure(stage, ex);
            Diagnostics.Write(stage, "blocks=" + Interlocked.Read(ref blocks) + " input_queue_ms=" + input.Count / 16.0 + " output_queue_ms=" + output.Count / 16.0 + " underruns=" + Interlocked.Read(ref underruns));
            cancellation.Cancel();
            inputReady.Set();
        }

        private bool Await(WaitHandle ready)
        {
            return WaitHandle.WaitAny(new WaitHandle[] { ready, cancellation.Token.WaitHandle }) == 0 && !cancellation.IsCancellationRequested;
        }

        private static double Peak(float[] values)
        {
            double peak = 0;
            foreach (float value in values)
                peak = Math.Max(peak, Math.Abs(value));
            return peak;
        }

        private void CaptureLoop()
        {
            IntPtr mmcss = IntPtr.Zero;
            try
            {
                if (!Await(modelsReady) || !Await(renderReady))
                    return;
                using (var endpoint = new AudioEndpoint(options.InputId, true))
                {
                    var resampler = new StreamResampler(endpoint.Format.Rate, 16000);
                    lock (gate)
                        inRate = endpoint.Format.Rate;
                    uint task;
                    mmcss = NativeAudio.AvSetMmThreadCharacteristics("Audio", out task);
                    endpoint.Start();
                    lock (gate)
                    {
                        if (!cancellation.IsCancellationRequested)
                            state = "运行中";
                    }

                    while (!cancellation.IsCancellationRequested)
                    {
                        int signaled = WaitHandle.WaitAny(new WaitHandle[] { endpoint.Ready, cancellation.Token.WaitHandle }, 2000);
                        if (signaled == 1)
                            break;
                        if (signaled == WaitHandle.WaitTimeout)
                            throw new AudioFailureException(AudioFailureKind.InputTimeout, "No microphone callbacks within 2 seconds.");
                        uint available;
                        NativeAudio.Check(endpoint.Capture.GetNextPacketSize(out available));
                        while (available > 0 && !cancellation.IsCancellationRequested)
                        {
                            IntPtr pointer;
                            uint frames, flags;
                            ulong position, clock;
                            NativeAudio.Check(endpoint.Capture.GetBuffer(out pointer, out frames, out flags, out position, out clock));
                            float[] mono;
                            try
                            {
                                mono = endpoint.Format.Decode(pointer, (int)frames, (flags & 2) != 0, options.SilenceOutput);
                            }
                            finally
                            {
                                NativeAudio.Check(endpoint.Capture.ReleaseBuffer(frames));
                            }

                            Interlocked.Add(ref captureFrames, frames);
                            if ((flags & 1) != 0)
                                Interlocked.Increment(ref discontinuities);
                            lock (gate)
                                inPeak = Peak(mono);
                            float[] converted = resampler.Push(mono);
                            if (!input.Write(converted))
                                throw new AudioFailureException(AudioFailureKind.Slow, "Microphone input queue exceeded 256 ms.");
                            inputReady.Set();
                            NativeAudio.Check(endpoint.Capture.GetNextPacketSize(out available));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Fault("microphone", ex);
            }
            finally
            {
                if (mmcss != IntPtr.Zero)
                    NativeAudio.AvRevertMmThreadCharacteristics(mmcss);
            }
        }

        private void ProcessLoop()
        {
            bool loaded = false;
            try
            {
                IAudioStage model = null;
                if (options.Alkove || options.Denoiser)
                {
                    lock (gate)
                        state = "正在加载本地模型…";
                    model = new ModelWorker(options.Python, options.Root, options.Alkove, options.Denoiser, cancellation.Token);
                }

                using (var pipeline = model == null ? new SerialPipeline(gain) : new SerialPipeline(model, gain))
                {
                    loaded = true;
                    modelsReady.Set();
                    var block = new float[256];
                    var watch = new Stopwatch();
                    while (!cancellation.IsCancellationRequested)
                    {
                        if (input.Count < 256)
                        {
                            if (!Await(inputReady))
                                break;
                            continue;
                        }

                        input.Read(block, 256);
                        watch.Restart();
                        // Stages may mutate their input; capture always supplies a separate packet.
                        float[] processed = pipeline.Process(block);
                        watch.Stop();
                        Interlocked.Add(ref processTicks, watch.ElapsedTicks);
                        Interlocked.Add(ref processedSamples, processed.Length);
                        lock (gate)
                        {
                            outPeak = Peak(processed);
                            processMs = watch.Elapsed.TotalMilliseconds;
                        }

                        if (!output.Write(processed))
                            throw new AudioFailureException(AudioFailureKind.Slow, "Processed output queue exceeded 256 ms.");
                        lock (monitorGate)
                        {
                            if (monitor != null)
                                monitor.Push(processed);
                        }

                        Interlocked.Increment(ref blocks);
                    }
                }
            }
            catch (Exception ex)
            {
                Fault(loaded ? "model-process" : "model-load", ex);
            }
        }

        private void RenderLoop()
        {
            IntPtr mmcss = IntPtr.Zero;
            try
            {
                if (!Await(modelsReady))
                    return;
                using (var endpoint = new AudioEndpoint(options.OutputId, false))
                {
                    lock (gate)
                        outRate = endpoint.Format.Rate;
                    var resampler = new StreamResampler(16000, endpoint.Format.Rate);
                    var renderBuffer = new SampleBuffer(endpoint.Format.Rate / 2);
                    var packet = new float[(int)endpoint.Capacity];
                    bool primed = false;
                    uint task;
                    mmcss = NativeAudio.AvSetMmThreadCharacteristics("Audio", out task);
                    IntPtr pointer;
                    NativeAudio.Check(endpoint.Render.GetBuffer(endpoint.Capacity, out pointer));
                    NativeAudio.Check(endpoint.Render.ReleaseBuffer(endpoint.Capacity, 2));
                    endpoint.Start();
                    renderReady.Set();
                    while (!cancellation.IsCancellationRequested)
                    {
                        int signaled = WaitHandle.WaitAny(new WaitHandle[] { endpoint.Ready, cancellation.Token.WaitHandle }, 2000);
                        if (signaled == 1)
                            break;
                        if (signaled == WaitHandle.WaitTimeout)
                            throw new AudioFailureException(AudioFailureKind.OutputTimeout, "No virtual output callbacks within 2 seconds.");
                        uint available = endpoint.FreeFrames();
                        if (available == 0)
                            continue;
                        if (!primed && output.Count >= 768)
                            primed = true; // 48 ms elastic reserve, measured separately from inference.
                        if (primed)
                        {
                            int queued = output.Count;
                            if (queued > 0)
                            {
                                var samples = new float[queued];
                                output.Read(samples, queued);
                                double fillMs = renderBuffer.Count * 1000.0 / endpoint.Format.Rate + queued / 16.0;
                                // Modest clock correction for independent microphone/cable clocks.
                                double ratio = 1 + Math.Max(-0.003, Math.Min(0.003, (fillMs - 48) * 0.0001));
                                if (!renderBuffer.Write(resampler.Push(samples, ratio)))
                                    throw new InvalidOperationException("输出缓冲超过容量。");
                            }

                            int read = renderBuffer.Read(packet, (int)available);
                            if (read < available)
                            {
                                Array.Clear(packet, read, (int)available - read);
                                Interlocked.Increment(ref underruns);
                                // Re-prime after a stall to avoid repeatedly sending short bursts.
                                primed = false;
                            }
                        }
                        else
                            Array.Clear(packet, 0, (int)available);
                        lock (gate)
                            renderQueuedMs = renderBuffer.Count * 1000.0 / endpoint.Format.Rate;
                        NativeAudio.Check(endpoint.Render.GetBuffer(available, out pointer));
                        try
                        {
                            if (!options.SilenceOutput)
                                endpoint.Format.Encode(pointer, packet, (int)available);
                        }
                        finally
                        {
                            NativeAudio.Check(endpoint.Render.ReleaseBuffer(available, options.SilenceOutput ? 2u : 0u));
                        }

                        Interlocked.Add(ref renderFrames, available);
                    }
                }
            }
            catch (Exception ex)
            {
                Fault("virtual-output", ex);
            }
            finally
            {
                if (mmcss != IntPtr.Zero)
                    NativeAudio.AvRevertMmThreadCharacteristics(mmcss);
            }
        }

        public void Stop()
        {
            lock (lifecycleGate)
            {
                if (disposed == 0)
                    StopCore();
            }
        }

        private void StopCore()
        {
            var failures = new List<Exception>();
            try
            {
                cancellation.Cancel();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }

            inputReady.Set();
            var watch = Stopwatch.StartNew();
            foreach (Thread thread in new Thread[]
            {
                captureThread,
                renderThread,
                processThread
            })
            {
                if (thread == null)
                    continue;
                try
                {
                    if (thread == Thread.CurrentThread || !thread.Join(Math.Max(0, 4000 - (int)watch.ElapsedMilliseconds)))
                        failures.Add(new TimeoutException("音频线程未能按时停止：" + thread.Name));
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            try
            {
                SetMonitor(null);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }

            lock (gate)
            {
                inPeak = outPeak = 0;
                if (failures.Count != 0)
                {
                    if (String.IsNullOrEmpty(error))
                        error = "音频清理未完成，请再次停止或退出。";
                    state = "已停止：发生错误";
                }
                else if (String.IsNullOrEmpty(error))
                    state = "已停止";
            }

            if (failures.Count != 0)
            {
                var failure = new AggregateException("音频清理未完成，请再次停止或退出。", failures);
                Diagnostics.Failure("audio-cleanup", failure);
                throw failure;
            }
        }

        public void Dispose()
        {
            lock (lifecycleGate)
            {
                if (disposed != 0)
                    return;
                StopCore();
                // Audio threads must have exited before their wait handles/token are disposed.
                inputReady.Dispose();
                modelsReady.Dispose();
                renderReady.Dispose();
                cancellation.Dispose();
                disposed = 1;
            }
        }
    }
}

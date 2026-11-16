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
        internal long CaptureFrames, RenderFrames, ProcessedSamples, ProcessedRenderFrames;
        public int InputRate, OutputRate;
        public bool MonitorActive;
        internal bool Recovering;
        internal int Recoveries;
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
        private readonly SampleBuffer input = new SampleBuffer(8192), output = new SampleBuffer(8192);
        private readonly RealtimeRecovery recovery = new RealtimeRecovery();
        private readonly TimingWindow timings = new TimingWindow();
        private readonly Stopwatch uptime = Stopwatch.StartNew();
        private Timer healthTimer;
        private int faulted, healthBusy;
        private long droppedOutput;
        private double nextLog, maxInputMs, maxOutputMs, maxCaptureGapMs, lastCaptureAt;
        private readonly GainLimiter gain = new GainLimiter();
        private Thread captureThread, processThread, renderThread;
        private ModelWorker modelWorker;
        private string state = "准备中", error = "";
        private AudioFailureKind failureKind;
        private double inPeak, outPeak, processMs, renderQueuedMs, processingQueuedMs, resampleQueuedMs, renderSubmittingMs;
        private int inRate, outRate, started, disposed;
        private long blocks, underruns, discontinuities;
        private long captureFrames, renderFrames, processedSamples, processedRenderFrames;
        public AudioEngine(EngineOptions options)
        {
            this.options = options;
            gain.GainDb = options.GainDb;
        }

        private Thread Launch(ThreadStart action, string name, ThreadPriority priority = ThreadPriority.Normal)
        {
            var thread = new Thread(action)
            {
                IsBackground = true,
                Name = name,
                Priority = priority
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
                processThread = Launch(ProcessLoop, "KeepClear processing", ThreadPriority.AboveNormal);
                renderThread = Launch(RenderLoop, "KeepClear WASAPI render");
                captureThread = Launch(CaptureLoop, "KeepClear WASAPI capture");
                healthTimer = new Timer(HealthTick, null, 100, 100);
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
                        InputQueueMs = input.Count / 16.0 + processingQueuedMs,
                        OutputQueueMs = output.Count / 16.0 + renderQueuedMs + resampleQueuedMs + renderSubmittingMs,
                        InputRate = inRate,
                        OutputRate = outRate,
                        Blocks = Interlocked.Read(ref blocks),
                        Underruns = Interlocked.Read(ref underruns),
                        Discontinuities = Interlocked.Read(ref discontinuities),
                        CaptureFrames = Interlocked.Read(ref captureFrames),
                        RenderFrames = Interlocked.Read(ref renderFrames),
                        ProcessedSamples = Interlocked.Read(ref processedSamples),
                        ProcessedRenderFrames = Interlocked.Read(ref processedRenderFrames),
                        MonitorActive = monitor != null && monitor.Active,
                        MonitorError = monitor == null ? "" : monitor.Error,
                        Recovering = recovery.Active,
                        Recoveries = recovery.Count
                    };
        }

        private void Fault(string stage, Exception ex)
        {
            if (Interlocked.CompareExchange(ref faulted, 1, 0) != 0)
                return;
            lock (gate)
            {
                if (cancellation.IsCancellationRequested)
                    return;
                error = stage + ": " + ex.Message + " (" + ex.GetType().Name + ", 0x" + ex.HResult.ToString("X8") + ")";
                failureKind = AudioFailure.Classify(stage, ex);
                state = "已停止：发生错误";
            }

            Diagnostics.Failure(stage, ex);
            Diagnostics.Write(stage, "blocks=" + Interlocked.Read(ref blocks) + " input_queue_ms=" + input.Count / 16.0 + " output_queue_ms=" + output.Count / 16.0 + " underruns=" + Interlocked.Read(ref underruns) + " discontinuities=" + Interlocked.Read(ref discontinuities) + " " + timings.Summary());
            cancellation.Cancel();
            inputReady.Set();
        }

        private void HealthTick(object ignored)
        {
            if (Interlocked.CompareExchange(ref healthBusy, 1, 0) != 0) return;
            try
            {
                if (cancellation.IsCancellationRequested) return;
                double now = uptime.Elapsed.TotalSeconds;
                var snapshot = Snapshot();
                if (snapshot.State != "运行中") return;
                RecoveryStep step = options.SilenceOutput ? RecoveryStep.None : recovery.Observe(now, snapshot.InputQueueMs, snapshot.OutputQueueMs);
                if (step == RecoveryStep.Started || step == RecoveryStep.Completed)
                    Diagnostics.Write("stream-recovery", step + " input_ms=" + snapshot.InputQueueMs + " output_ms=" + snapshot.OutputQueueMs);
                lock (gate)
                {
                    maxInputMs = Math.Max(maxInputMs, snapshot.InputQueueMs);
                    maxOutputMs = Math.Max(maxOutputMs, snapshot.OutputQueueMs);
                    if (now < nextLog) return;
                    nextLog = now + 1;
                }
                Diagnostics.Write("audio-health", String.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "elapsed_s={0:F2} input_ms={1:F2} output_ms={2:F2} input_high_ms={3:F2} output_high_ms={4:F2} capture_gap_max_ms={5:F2} input_rate={6} output_rate={7} blocks={8} underruns={9} discontinuities={10} recoveries={11} skipped_processed_samples={12} gc0={13} gc1={14} gc2={15} {16}",
                    now, snapshot.InputQueueMs, snapshot.OutputQueueMs, maxInputMs, maxOutputMs, maxCaptureGapMs, snapshot.InputRate, snapshot.OutputRate, snapshot.Blocks, snapshot.Underruns, snapshot.Discontinuities, recovery.Count, Interlocked.Read(ref droppedOutput), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), timings.Summary()));
                var worker = Volatile.Read(ref modelWorker);
                if (worker != null) Diagnostics.Write("worker-health", worker.DiagnosticSummary());
                Diagnostics.Write("log-health", Diagnostics.Summary());
            }
            catch (Exception ex) { Diagnostics.Failure("health-monitor", ex); }
            finally { Volatile.Write(ref healthBusy, 0); }
        }

        private bool Await(WaitHandle ready)
        {
            return WaitHandle.WaitAny(new WaitHandle[] { ready, cancellation.Token.WaitHandle }) == 0 && !cancellation.IsCancellationRequested;
        }

        private static double Peak(float[] values)
        {
            return Peak(values, values.Length);
        }

        private static double Peak(float[] values, int count)
        {
            double peak = 0;
            for (int i = 0; i < count; i++) peak = Math.Max(peak, Math.Abs(values[i]));
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
                    var mono = new float[(int)endpoint.Capacity];
                    var converted = new float[0];
                    var waits = new WaitHandle[] { endpoint.Ready, cancellation.Token.WaitHandle };
                    Diagnostics.Write("capture-format", "rate=" + endpoint.Format.Rate + " channels=" + endpoint.Format.Channels + " bits=" + endpoint.Format.Bits + " capacity=" + endpoint.Capacity);
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
                        int signaled = WaitHandle.WaitAny(waits, 2000);
                        if (signaled == 1)
                            break;
                        if (signaled == WaitHandle.WaitTimeout)
                            throw new AudioFailureException(AudioFailureKind.InputTimeout, "No microphone callbacks within 2 seconds.");
                        double at = uptime.Elapsed.TotalMilliseconds;
                        lock (gate)
                        {
                            if (lastCaptureAt != 0) maxCaptureGapMs = Math.Max(maxCaptureGapMs, at - lastCaptureAt);
                            lastCaptureAt = at;
                        }
                        uint available;
                        NativeAudio.Check(endpoint.Capture.GetNextPacketSize(out available));
                        while (available > 0 && !cancellation.IsCancellationRequested)
                        {
                            IntPtr pointer;
                            uint frames, flags;
                            ulong position, clock;
                            NativeAudio.Check(endpoint.Capture.GetBuffer(out pointer, out frames, out flags, out position, out clock));
                            try
                            {
                                if (frames > mono.Length) Array.Resize(ref mono, (int)frames);
                                endpoint.Format.DecodeInto(pointer, (int)frames, (flags & 2) != 0, options.SilenceOutput, mono);
                            }
                            finally
                            {
                                NativeAudio.Check(endpoint.Capture.ReleaseBuffer(frames));
                            }

                            Interlocked.Add(ref captureFrames, frames);
                            if ((flags & 1) != 0)
                                Interlocked.Increment(ref discontinuities);
                            lock (gate)
                                inPeak = Peak(mono, (int)frames);
                            int convertedCount = resampler.PushInto(mono, (int)frames, ref converted);
                            lock (gate)
                            {
                                // Silent startup checks test connectivity, not queue stability.
                                if (options.SilenceOutput)
                                    input.WriteLatest(converted, convertedCount);
                                else if (!input.Write(converted, convertedCount))
                                    throw new AudioFailureException(AudioFailureKind.Slow, "Model input cannot keep up and its bounded 512 ms queue is full; packet_samples=" + convertedCount);
                                maxInputMs = Math.Max(maxInputMs, input.Count / 16.0 + processingQueuedMs);
                            }
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
                    Volatile.Write(ref modelWorker, (ModelWorker)model);
                }

                using (var pipeline = model == null ? new SerialPipeline(gain) : new SerialPipeline(model, gain))
                {
                    loaded = true;
                    modelsReady.Set();
                    var block = new float[256];
                    var waits = new WaitHandle[] { inputReady, cancellation.Token.WaitHandle };
                    var watch = new Stopwatch();
                    while (!cancellation.IsCancellationRequested)
                    {
                        if (input.Count < 256)
                        {
                            if (WaitHandle.WaitAny(waits) != 0 || cancellation.IsCancellationRequested)
                                break;
                            continue;
                        }

                        lock (gate)
                        {
                            input.Read(block, 256);
                            processingQueuedMs = 16;
                        }
                        watch.Restart();
                        // Stages may mutate their input; capture always supplies a separate packet.
                        float[] processed = pipeline.Process(block);
                        watch.Stop();
                        timings.Add(watch.Elapsed.TotalMilliseconds);
                        Interlocked.Add(ref processedSamples, processed.Length);
                        int skipped = 0;
                        bool forcedSplice = false;
                        lock (gate)
                        {
                            outPeak = Peak(processed);
                            processMs = watch.Elapsed.TotalMilliseconds;
                            processingQueuedMs = 0;
                            double pendingMs = input.Count / 16.0 + (output.Count + processed.Length) / 16.0 + renderQueuedMs + resampleQueuedMs + renderSubmittingMs;
                            if (!options.SilenceOutput && (recovery.Active || pendingMs >= RealtimeRecovery.EmergencyMs) && output.Count > 1920)
                                skipped = output.TrimSilence(1920, 320, .00001f);
                            if (!options.SilenceOutput && pendingMs - skipped / 16.0 >= RealtimeRecovery.EmergencyMs)
                            {
                                int cut = output.KeepLatest(Math.Max(0, 1920 - processed.Length), 160);
                                skipped += cut;
                                forcedSplice = cut > 0;
                            }
                            if (options.SilenceOutput)
                                output.WriteLatest(processed);
                            else if (!output.Write(processed))
                                throw new AudioFailureException(AudioFailureKind.Slow, "Processed output packet exceeded bounded queue capacity.");
                            maxOutputMs = Math.Max(maxOutputMs, output.Count / 16.0 + renderQueuedMs + resampleQueuedMs + renderSubmittingMs);
                        }
                        if (skipped > 0)
                        {
                            Interlocked.Add(ref droppedOutput, skipped);
                            if (forcedSplice) lock (monitorGate) if (monitor != null) monitor.Resynchronize();
                        }
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
                    Diagnostics.Write("render-format", "rate=" + endpoint.Format.Rate + " channels=" + endpoint.Format.Channels + " bits=" + endpoint.Format.Bits + " capacity=" + endpoint.Capacity);
                    var renderBuffer = new SampleBuffer(endpoint.Format.Rate / 2);
                    var packet = new float[(int)endpoint.Capacity];
                    var samples = new float[4096];
                    var converted = new float[0];
                    var waits = new WaitHandle[] { endpoint.Ready, cancellation.Token.WaitHandle };
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
                        int signaled = WaitHandle.WaitAny(waits, 2000);
                        if (signaled == 1)
                            break;
                        if (signaled == WaitHandle.WaitTimeout)
                            throw new AudioFailureException(AudioFailureKind.OutputTimeout, "No virtual output callbacks within 2 seconds.");
                        uint available = endpoint.FreeFrames();
                        if (available == 0)
                            continue;
                        int processedFrames = 0;
                        if (!primed && output.Count >= 768)
                            primed = true; // 48 ms elastic reserve, measured separately from inference.
                        if (primed)
                        {
                            int queued;
                            lock (gate)
                            {
                                queued = Math.Min(output.Count, 4096);
                                output.Read(samples, queued);
                                resampleQueuedMs = queued / 16.0;
                            }
                            if (queued > 0)
                            {
                                double fillMs = renderBuffer.Count * 1000.0 / endpoint.Format.Rate + queued / 16.0;
                                // Modest clock correction for independent microphone/cable clocks.
                                double ratio = 1 + Math.Max(-0.003, Math.Min(0.003, (fillMs - 48) * 0.0001));
                                int convertedCount = resampler.PushInto(samples, queued, ref converted, ratio);
                                lock (gate)
                                {
                                    if (options.SilenceOutput)
                                        renderBuffer.WriteLatest(converted, convertedCount);
                                    else if (!renderBuffer.Write(converted, convertedCount))
                                    {
                                        int discarded = renderBuffer.KeepLatest(Math.Max(0, endpoint.Format.Rate / 2 - convertedCount), endpoint.Format.Rate / 100);
                                        Interlocked.Add(ref droppedOutput, (long)discarded * 16000 / endpoint.Format.Rate);
                                        if (!renderBuffer.Write(converted, convertedCount)) throw new InvalidOperationException("Processed render packet exceeded bounded capacity.");
                                    }
                                    renderQueuedMs = renderBuffer.Count * 1000.0 / endpoint.Format.Rate;
                                    resampleQueuedMs = 0;
                                }
                            }
                            int skipped = 0, read;
                            bool forcedSplice = false;
                            lock (gate)
                            {
                                double totalMs = input.Count / 16.0 + processingQueuedMs + output.Count / 16.0 + renderQueuedMs + renderSubmittingMs;
                                if (!options.SilenceOutput && (recovery.Active || totalMs >= RealtimeRecovery.EmergencyMs) && renderBuffer.Count > endpoint.Format.Rate * 120 / 1000)
                                    skipped = renderBuffer.TrimSilence(endpoint.Format.Rate * 120 / 1000, endpoint.Format.Rate / 50, .00001f);
                                if (!options.SilenceOutput && totalMs - skipped * 1000.0 / endpoint.Format.Rate >= RealtimeRecovery.EmergencyMs)
                                {
                                    int cut = renderBuffer.KeepLatest(endpoint.Format.Rate * 120 / 1000, endpoint.Format.Rate / 100);
                                    skipped += cut;
                                    forcedSplice = cut > 0;
                                }
                                read = renderBuffer.Read(packet, (int)available);
                                renderQueuedMs = renderBuffer.Count * 1000.0 / endpoint.Format.Rate;
                                renderSubmittingMs = read * 1000.0 / endpoint.Format.Rate;
                            }
                            if (skipped > 0)
                            {
                                Interlocked.Add(ref droppedOutput, (long)skipped * 16000 / endpoint.Format.Rate);
                                if (forcedSplice) lock (monitorGate) if (monitor != null) monitor.Resynchronize();
                            }
                            processedFrames = read;
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
                        // Count model output only after successful WASAPI submission.
                        Interlocked.Add(ref processedRenderFrames, processedFrames);
                        lock (gate) renderSubmittingMs = 0;
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
            if (healthTimer != null) { healthTimer.Dispose(); healthTimer = null; }
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

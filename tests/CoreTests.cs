using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeepQuiet
{
    internal static class Tests
    {
        private static int passed;
        private static void Check(bool condition, string name)
        {
            if (!condition)
                throw new Exception("FAIL: " + name);
            passed++;
            Console.WriteLine("PASS: " + name);
        }

        private sealed class AddStage : IAudioStage
        {
            public string Name
            {
                get
                {
                    return "add";
                }
            }

            public float[] Process(float[] x)
            {
                return x.Select(v => v + 1).ToArray();
            }

            public void Dispose()
            {
            }
        }

        private sealed class MultiplyStage : IAudioStage
        {
            public string Name
            {
                get
                {
                    return "multiply";
                }
            }

            public float[] Process(float[] x)
            {
                return x.Select(v => v * 3).ToArray();
            }

            public void Dispose()
            {
            }
        }

        private sealed class InvalidStage : IAudioStage
        {
            public string Name
            {
                get
                {
                    return "invalid";
                }
            }

            public float[] Process(float[] x)
            {
                return new float[]
                {
                    float.NaN
                };
            }

            public void Dispose()
            {
            }
        }

        private static float[] Tone(int rate, double frequency, int length)
        {
            return Enumerable.Range(0, length).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / rate))).ToArray();
        }

        private static double Rms(float[] x, int skip)
        {
            return Math.Sqrt(x.Skip(skip).Select(v => (double)v * v).Average());
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 3 && args[0] == "--instance-secondary")
                {
                    using (var instance = new SingleInstance(args[1]))
                    {
                        File.WriteAllText(args[2], instance.IsPrimary ? "primary" : "secondary");
                        if (!instance.IsPrimary)
                            instance.SignalPrimary();
                        return instance.IsPrimary ? 1 : 0;
                    }
                }

                if (args.Length > 0 && args[0] == "--tray-hardware")
                {
                    UiChecks.TrayHardware(args.Length > 1 ? args[1] : "tray-hardware.txt");
                    return 0;
                }

                if (args.Length > 0 && args[0] == "--startup-ui")
                {
                    StartupTests.Render(args.Length > 1 ? args[1] : "startup-ui");
                    return 0;
                }

                if (args.Length > 0 && args[0] == "--startup-choice")
                {
                    System.Windows.Forms.Application.EnableVisualStyles();
                    System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                    StartupTests.MicrophoneChoice(args.Length > 1 ? args[1] : "startup-choice");
                    return 0;
                }

                if (args.Length > 0 && args[0] == "--startup-choice-hardware")
                {
                    StartupTests.HardwareChoice(args.Length > 1 ? args[1] : "startup-choice-hardware");
                    return 0;
                }

                if (args.Length > 0 && args[0] == "--hardware")
                {
                    Hardware(args.Length > 1 ? args[1] : "hardware.txt", false);
                    return 0;
                }

                if (args.Length > 0 && args[0] == "--hardware-models")
                {
                    Hardware(args.Length > 1 ? args[1] : "hardware-models.txt", true);
                    return 0;
                }

                if (args.Length > 0 && args[0] == "--models")
                {
                    RealModels(args.Length > 1 ? args[1] : "model-pipe.txt");
                    return 0;
                }

                if (args.Length > 0 && args[0] == "--cable")
                {
                    Cable(args.Length > 1 ? args[1] : "cable.txt");
                    return 0;
                }

                if (args.Length > 0 && args[0] == "--monitor")
                {
                    Cable(args.Length > 1 ? args[1] : "monitor.txt", true);
                    return 0;
                }

                if (args.Length > 0 && args[0] == "--monitor-engine")
                {
                    MonitorEngine(args.Length > 1 ? args[1] : "monitor-engine.txt");
                    return 0;
                }

                if (args.Length > 0 && args[0] == "--ui")
                {
                    UiChecks.Run(args.Length > 1 ? args[1] : "ui-checks");
                    return 0;
                }

                StartupTests.Run(Check);
                StartupOptimizationTests.Run(Check);
                MaintenanceTests.Run(Check);
                ReliabilityTests.Run(Check);
                string instanceKey = "KeepQuiet.Test." + Guid.NewGuid().ToString("N");
                string instanceReport = Path.Combine(Path.GetTempPath(), instanceKey + ".txt");
                using (var instanceForm = new System.Windows.Forms.Form())
                using (var primary = new SingleInstance(instanceKey))
                {
                    Check(primary.IsPrimary, "first process owns single-instance mutex");
                    bool activated = false;
                    primary.Listen(instanceForm, delegate
                    {
                        activated = true;
                    });
                    var childInfo = new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath, "--instance-secondary " + instanceKey + " \"" + instanceReport + "\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var secondary = Process.Start(childInfo))
                    {
                        if (!secondary.WaitForExit(4000))
                        {
                            secondary.Kill();
                            throw new Exception("Secondary instance timed out");
                        }

                        Check(secondary.ExitCode == 0 && File.ReadAllText(instanceReport) == "secondary", "second process cannot own the application instance");
                    }

                    var activationWatch = Stopwatch.StartNew();
                    while (!activated && activationWatch.ElapsedMilliseconds < 3000)
                    {
                        System.Windows.Forms.Application.DoEvents();
                        Thread.Sleep(10);
                    }

                    Check(activated, "second process signals original UI thread to restore window");
                }

                using (var reopened = new SingleInstance(instanceKey))
                    Check(reopened.IsPrimary, "single-instance lock released after application exit");
                string portableFixture = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "artifacts", "portable-resolution-fixture");
                Directory.CreateDirectory(Path.Combine(portableFixture, "runtime"));
                string bundledPython = Path.Combine(portableFixture, "runtime", "python.exe");
                File.WriteAllText(bundledPython, "");
                Check(AppSettings.ResolvePython(portableFixture, ApplicationPython()) == bundledPython, "bundled Python wins over valid saved development path");
                File.Delete(bundledPython);
                File.WriteAllText(Path.Combine(portableFixture, "PORTABLE.json"), "{}");
                Check(AppSettings.ResolvePython(portableFixture, ApplicationPython()) == bundledPython, "damaged portable runtime never falls back to system Python");
                var latest = new SampleBuffer(4);
                latest.WriteLatest(new float[] { 1, 2, 3 });
                latest.WriteLatest(new float[] { 4, 5, 6 });
                var newest = new float[4];
                latest.Read(newest, 4);
                Check(newest.SequenceEqual(new float[] { 3, 4, 5, 6 }), "monitor queue drops oldest audio under backpressure");
                latest.WriteLatest(new float[] { 1, 2, 3, 4, 5, 6 });
                latest.Read(newest, 4);
                Check(newest.SequenceEqual(new float[] { 3, 4, 5, 6 }), "oversized monitor packet retains newest bounded samples");
                var meter60 = new MeterEnvelope();
                var meter30 = new MeterEnvelope();
                for (int i = 0; i < 60; i++)
                    meter60.Advance(0.1, 1.0 / 60);
                for (int i = 0; i < 30; i++)
                    meter30.Advance(0.1, 1.0 / 30);
                Check(Math.Abs(meter60.Db - meter30.Db) < 0.000001 && Math.Abs(meter60.Db + 20) < 0.001, "meter motion independent of refresh rate");
                double before = meter60.Db;
                meter60.Advance(0, 0.016);
                Check(meter60.Db < before && meter60.Db > -25, "meter decays smoothly without snapping to silence");
                using (var monitorFailure = new MonitorOutput("invalid", CancellationToken.None))
                {
                    var deadline = Stopwatch.StartNew();
                    while (String.IsNullOrEmpty(monitorFailure.Error) && deadline.ElapsedMilliseconds < 3000)
                        Thread.Sleep(10);
                    Check(!String.IsNullOrEmpty(monitorFailure.Error) && !monitorFailure.Active, "invalid monitor device reports isolated failure");
                }

                using (var noRoute = new AudioEngine(new EngineOptions { OutputId = "invalid" }))
                {
                    bool invalidMonitor = false;
                    try
                    {
                        noRoute.SetMonitor("invalid");
                    }
                    catch (InvalidOperationException)
                    {
                        invalidMonitor = true;
                    }

                    Check(invalidMonitor && String.IsNullOrEmpty(noRoute.Snapshot().Error), "monitor rejection leaves main route healthy");
                }

                var fifo = new SampleBuffer(4);
                Check(fifo.Write(new float[] { 1, 2, 3 }), "FIFO writes");
                Check(!fifo.Write(new float[] { 4, 5 }), "FIFO overflow rejected atomically");
                var read = new float[4];
                Check(fifo.Read(read, 2) == 2 && read[0] == 1 && read[1] == 2, "FIFO ordering");
                fifo.Write(new float[] { 4, 5, 6 });
                fifo.Read(read, 4);
                Check(read.SequenceEqual(new float[] { 3, 4, 5, 6 }), "FIFO wraps without loss");
                using (var pipeline = new SerialPipeline(new AddStage(), new MultiplyStage()))
                    Check(pipeline.Process(new float[] { 1 })[0] == 6, "serial order, not parallel or reversed");
                bool rejected = false;
                try
                {
                    using (var pipeline = new SerialPipeline(new InvalidStage()))
                        pipeline.Process(new float[] { 1 });
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }

                Check(rejected, "non-finite model output rejected");
                var limiter = new GainLimiter
                {
                    GainDb = 18
                };
                var limited = limiter.Process(Enumerable.Repeat(0.9f, 5000).ToArray());
                Check(limited.All(v => v <= 0.98001f && v >= -0.98001f), "gain never exceeds limiter ceiling");
                var data = Tone(48000, 1000, 48000);
                var whole = new StreamResampler(48000, 16000).Push(data);
                var chunked = new List<float>();
                var resampler = new StreamResampler(48000, 16000);
                for (int i = 0; i < data.Length; i += 137)
                    chunked.AddRange(resampler.Push(data.Skip(i).Take(137).ToArray()));
                Check(whole.Length == chunked.Count && whole.Zip(chunked, (a, b) => Math.Abs(a - b)).Max() < 0.0001, "resampling packet-boundary continuity");
                Check(Math.Abs(whole.Length - 16000) < 32, "resampling sample count bounded by FIR lookahead");
                Check(Math.Abs(Rms(whole, 100) - 0.35355) < 0.01, "voice-band amplitude preserved");
                var ultrasonic = new StreamResampler(48000, 16000).Push(Tone(48000, 12000, 48000));
                Check(Rms(ultrasonic, 100) < 0.005, "anti-alias filter rejects 12 kHz before 16 kHz output");
                foreach (int rate in new[]
                {
                    44100,
                    48000,
                    96000
                })
                {
                    var down = new StreamResampler(rate, 16000).Push(Tone(rate, 440, rate));
                    var up = new StreamResampler(16000, rate).Push(down);
                    Check(Math.Abs(up.Length - rate) < rate / 100 && Math.Abs(Rms(up, 500) - 0.35355) < 0.015, "round trip at " + rate);
                }

                foreach (int bits in new[]
                {
                    16,
                    24,
                    32
                })
                {
                    var format = new MixFormat
                    {
                        Rate = 48000,
                        Channels = 2,
                        Bits = bits,
                        Align = 2 * bits / 8,
                        ValidBits = bits
                    };
                    var pointer = Marshal.AllocHGlobal(format.Align * 3);
                    try
                    {
                        format.Encode(pointer, new float[] { -0.75f, 0, 0.75f }, 3);
                        var decoded = format.Decode(pointer, 3, false);
                        Check(Math.Abs(decoded[0] + 0.75) < 0.0001 && Math.Abs(decoded[2] - 0.75) < 0.0001, "PCM round trip " + bits);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pointer);
                    }
                }

                var watch = Stopwatch.StartNew();
                resampler = new StreamResampler(48000, 16000);
                for (int i = 0; i < 100; i++)
                    resampler.Push(Tone(48000, 440, 480));
                watch.Stop();
                Check(watch.ElapsedMilliseconds < 1000, "one second resampling faster than wall clock (" + watch.ElapsedMilliseconds + " ms)");
                using (var worker = new ModelWorker("python.exe", AppDomain.CurrentDomain.BaseDirectory, false, false, CancellationToken.None))
                {
                    float[] pcm = Tone(16000, 440, 256);
                    Check(worker.Process(pcm).SequenceEqual(pcm), "C# to Python binary pipe exact PCM round trip");
                    for (int i = 0; i < 10; i++)
                        worker.Process(pcm);
                    Check(true, "persistent worker accepts repeated packets");
                }

                using (var cancel = new CancellationTokenSource())
                using (var worker = new ModelWorker("python.exe", AppDomain.CurrentDomain.BaseDirectory, false, false, cancel.Token))
                {
                    cancel.Cancel();
                    bool stopped = false;
                    watch.Restart();
                    try
                    {
                        worker.Process(new float[] { 0 });
                    }
                    catch (InvalidOperationException)
                    {
                        stopped = true;
                    }

                    Check(stopped && watch.ElapsedMilliseconds < 2500, "cancellation terminates optional model process");
                }

                string fixture = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "artifacts", "stall-fixture");
                Directory.CreateDirectory(Path.Combine(fixture, "workers"));
                File.WriteAllText(Path.Combine(fixture, "workers", "model_worker.py"), "import sys,struct,time\nsys.stdout.buffer.write(b'CV01'+struct.pack('<i',16000))\nsys.stdout.buffer.flush()\ntime.sleep(10)\n");
                using (var worker = new ModelWorker("python.exe", fixture, false, false, CancellationToken.None))
                {
                    bool timeout = false;
                    watch.Restart();
                    try
                    {
                        worker.Process(new float[] { 0 });
                    }
                    catch (InvalidOperationException)
                    {
                        timeout = true;
                    }

                    Check(timeout && watch.ElapsedMilliseconds >= 1500 && watch.ElapsedMilliseconds < 4000, "stalled model call has bounded timeout");
                }

                using (var engine = new AudioEngine(new EngineOptions { InputId = "invalid", OutputId = "invalid", Python = "python.exe", Root = AppDomain.CurrentDomain.BaseDirectory }))
                {
                    engine.Start();
                    var timeout = Stopwatch.StartNew();
                    while (String.IsNullOrEmpty(engine.Snapshot().Error) && timeout.ElapsedMilliseconds < 3000)
                        Thread.Sleep(10);
                    Check(!String.IsNullOrEmpty(engine.Snapshot().Error), "device open failure becomes engine fault");
                }

                Console.WriteLine("TOTAL: " + passed + " passed");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void RealModels(string path)
        {
            var report = new List<string>();
            string root = AppDomain.CurrentDomain.BaseDirectory;
            foreach (int mode in new[]
            {
                1,
                2,
                3
            })
            {
                var watch = Stopwatch.StartNew();
                using (var worker = new ModelWorker(AppSettings.FindPython(root), root, (mode & 1) != 0, (mode & 2) != 0, CancellationToken.None))
                {
                    double loadMs = watch.Elapsed.TotalMilliseconds;
                    int total = 0, first = -1;
                    var times = new List<double>();
                    for (int i = 0; i < 250; i++)
                    {
                        watch.Restart();
                        float[] result = worker.Process(Tone(16000, 440, 256));
                        times.Add(watch.Elapsed.TotalMilliseconds);
                        if (result.Length > 0 && first < 0)
                            first = i + 1;
                        total += result.Length;
                        if (result.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                            throw new Exception("Non-finite model pipe output");
                    }

                    if (total < 62000 || total > 64000 || times.Average() >= 16)
                        throw new Exception("Real model pipe failed continuity/realtime check");
                    report.Add(String.Format("mode={0}; load_ms={1:F1}; calls=250; output_samples={2}; first_nonempty_call={3}; mean_roundtrip_ms={4:F3}; max_roundtrip_ms={5:F3}", mode, loadMs, total, first, times.Average(), times.Max()));
                }
            }

            File.WriteAllLines(path, report.ToArray());
            Console.WriteLine(String.Join(Environment.NewLine, report));
        }

        private static string ApplicationPython()
        {
            return AppSettings.FindPython(AppDomain.CurrentDomain.BaseDirectory);
        }

        private static void Hardware(string path, bool models)
        {
            var input = NativeAudio.Devices(true).FirstOrDefault(d => !d.IsCable);
            var output = NativeAudio.Devices(false).FirstOrDefault(d => d.IsCable);
            if (input == null || output == null)
                throw new Exception("Real microphone and VB-CABLE are required.");
            var report = new List<string>();
            for (int run = 0; run < 3; run++)
            {
                using (var engine = new AudioEngine(new EngineOptions { InputId = input.Id, OutputId = output.Id, Python = AppSettings.FindPython(AppDomain.CurrentDomain.BaseDirectory), Root = AppDomain.CurrentDomain.BaseDirectory, Alkove = models, Denoiser = models }))
                {
                    engine.Start();
                    var timeout = Stopwatch.StartNew();
                    while (engine.Snapshot().State != "运行中" && String.IsNullOrEmpty(engine.Snapshot().Error) && timeout.ElapsedMilliseconds < 20000)
                        Thread.Sleep(20);
                    if (engine.Snapshot().State != "运行中")
                        throw new Exception("Engine did not start: " + engine.Snapshot().Error);
                    var live = Stopwatch.StartNew();
                    double maxInput = 0, maxOutput = 0, maxProcess = 0;
                    int duration = models && run == 0 ? 30000 : 4000;
                    while (live.ElapsedMilliseconds < duration)
                    {
                        var current = engine.Snapshot();
                        if (!String.IsNullOrEmpty(current.Error))
                            throw new Exception(current.Error);
                        maxInput = Math.Max(maxInput, current.InputQueueMs);
                        maxOutput = Math.Max(maxOutput, current.OutputQueueMs);
                        maxProcess = Math.Max(maxProcess, current.ProcessMs);
                        Thread.Sleep(40);
                    }

                    var snapshot = engine.Snapshot();
                    if (!String.IsNullOrEmpty(snapshot.Error) || snapshot.Blocks < 50)
                        throw new Exception(snapshot.Error + " blocks=" + snapshot.Blocks);
                    if (models && snapshot.Underruns > 0)
                        throw new Exception("Output underrun during model hardware check");
                    report.Add(String.Format("run={0}; blocks={1}; input_rate={2}; output_rate={3}; process_ms={4:F3}; underruns={5}; discontinuities={6}; input_peak={7:F5}; output_peak={8:F5}; max_input_queue_ms={9:F1}; max_output_queue_ms={10:F1}; max_observed_process_ms={11:F3}; live_ms={12}; models={13}", run + 1, snapshot.Blocks, snapshot.InputRate, snapshot.OutputRate, snapshot.ProcessMs, snapshot.Underruns, snapshot.Discontinuities, snapshot.InputPeak, snapshot.OutputPeak, maxInput, maxOutput, maxProcess, live.ElapsedMilliseconds, models));
                    engine.Stop();
                }
            }

            File.WriteAllLines(path, report.ToArray());
            Console.WriteLine(String.Join(Environment.NewLine, report));
        }

        private static void MonitorEngine(string path)
        {
            var source = NativeAudio.Devices(true).First(d => !d.IsCable);
            var destinations = NativeAudio.Devices(false);
            var cable = destinations.First(d => d.IsCable);
            var headphones = destinations.FirstOrDefault(d => !d.IsCable && (d.Name.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0 || d.Name.Contains("耳机")));
            if (headphones == null)
                throw new Exception("A headphone endpoint is required for the live monitor check.");
            var report = new List<string>();
            using (var engine = new AudioEngine(new EngineOptions { InputId = source.Id, OutputId = cable.Id, Root = AppDomain.CurrentDomain.BaseDirectory, Python = AppSettings.FindPython(AppDomain.CurrentDomain.BaseDirectory), Alkove = true, Denoiser = true }))
            {
                engine.Start();
                var watch = Stopwatch.StartNew();
                while (engine.Snapshot().State != "运行中" && String.IsNullOrEmpty(engine.Snapshot().Error) && watch.ElapsedMilliseconds < 20000)
                    Thread.Sleep(20);
                if (engine.Snapshot().State != "运行中")
                    throw new Exception("Main route not running: " + engine.Snapshot().Error);
                for (int pass = 0; pass < 2; pass++)
                {
                    long before = engine.Snapshot().Blocks;
                    engine.SetMonitor(headphones.Id);
                    watch.Restart();
                    while (!engine.Snapshot().MonitorActive && String.IsNullOrEmpty(engine.Snapshot().MonitorError) && watch.ElapsedMilliseconds < 3000)
                        Thread.Sleep(10);
                    if (!engine.Snapshot().MonitorActive)
                        throw new Exception("Headphone monitor failed: " + engine.Snapshot().MonitorError);
                    Thread.Sleep(1500);
                    var on = engine.Snapshot();
                    if (!String.IsNullOrEmpty(on.Error) || !String.IsNullOrEmpty(on.MonitorError) || on.Blocks <= before)
                        throw new Exception("Monitor/main route stalled");
                    engine.SetMonitor(null);
                    Thread.Sleep(500);
                    var off = engine.Snapshot();
                    if (off.MonitorActive || off.Blocks <= on.Blocks || !String.IsNullOrEmpty(off.Error))
                        throw new Exception("Main output interrupted by monitor toggle");
                    report.Add(String.Format("pass={0}; monitor_active=True; monitor_disabled=True; blocks_before={1}; blocks_on={2}; blocks_after={3}; underruns={4}", pass + 1, before, on.Blocks, off.Blocks, off.Underruns));
                }

                bool cableRejected = false;
                try
                {
                    engine.SetMonitor(cable.Id);
                }
                catch (InvalidOperationException)
                {
                    cableRejected = true;
                }

                if (!cableRejected || !String.IsNullOrEmpty(engine.Snapshot().Error))
                    throw new Exception("VB-CABLE monitor guard failed");
                report.Add("VB-CABLE monitor rejected; main route healthy; headphone=" + headphones.Name);
            }

            File.WriteAllLines(path, report.ToArray());
            Console.WriteLine(String.Join(Environment.NewLine, report));
        }

        private static void Cable(string path, bool monitor = false)
        {
            var source = NativeAudio.Devices(true).First(d => d.Name.IndexOf("CABLE Output", StringComparison.OrdinalIgnoreCase) >= 0);
            var destination = NativeAudio.Devices(false).First(d => d.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0);
            var ready = new ManualResetEvent(false);
            var stop = new ManualResetEvent(false);
            Exception captureError = null;
            long count = 0;
            double energy = 0, cosine = 0, sine = 0;
            var captured = new List<float>();
            int captureRate = 0;
            var thread = new Thread(delegate ()
            {
                try
                {
                    using (var endpoint = new AudioEndpoint(source.Id, true))
                    {
                        captureRate = endpoint.Format.Rate;
                        endpoint.Start();
                        ready.Set();
                        while (WaitHandle.WaitAny(new WaitHandle[] { endpoint.Ready, stop }, 3000) == 0)
                        {
                            uint available;
                            NativeAudio.Check(endpoint.Capture.GetNextPacketSize(out available));
                            while (available > 0)
                            {
                                IntPtr pointer;
                                uint frames, flags;
                                ulong position, clock;
                                NativeAudio.Check(endpoint.Capture.GetBuffer(out pointer, out frames, out flags, out position, out clock));
                                float[] pcm;
                                try
                                {
                                    pcm = endpoint.Format.Decode(pointer, (int)frames, (flags & 2) != 0);
                                }
                                finally
                                {
                                    NativeAudio.Check(endpoint.Capture.ReleaseBuffer(frames));
                                }

                                foreach (float sample in pcm)
                                {
                                    if (monitor)
                                        captured.Add(sample);
                                    double angle = 2 * Math.PI * 997 * count / endpoint.Format.Rate;
                                    energy += sample * sample;
                                    cosine += sample * Math.Cos(angle);
                                    sine += sample * Math.Sin(angle);
                                    count++;
                                }

                                NativeAudio.Check(endpoint.Capture.GetNextPacketSize(out available));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    captureError = ex;
                    ready.Set();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try
            {
                if (!ready.WaitOne(3000))
                    throw new Exception("Cable capture initialization timeout");
                if (captureError != null)
                    throw captureError;
                if (monitor)
                {
                    using (var listener = new MonitorOutput(destination.Id, CancellationToken.None))
                    {
                        var deadline = Stopwatch.StartNew();
                        while (!listener.Active && String.IsNullOrEmpty(listener.Error) && deadline.ElapsedMilliseconds < 3000)
                            Thread.Sleep(10);
                        if (!listener.Active)
                            throw new Exception("Monitor did not initialize: " + listener.Error);
                        long generated = 0;
                        var watch = Stopwatch.StartNew();
                        int packets = 0;
                        while (watch.ElapsedMilliseconds < 3000)
                        {
                            if (watch.Elapsed.TotalMilliseconds < packets * 16)
                            {
                                Thread.Sleep(1);
                                continue;
                            }

                            var pcm = new float[256];
                            for (int i = 0; i < pcm.Length; i++)
                                pcm[i] = (float)(0.05 * Math.Sin(2 * Math.PI * 997 * generated++ / 16000));
                            listener.Push(pcm);
                            packets++;
                            if (!String.IsNullOrEmpty(listener.Error))
                                throw new Exception(listener.Error);
                        }

                        if (listener.RenderedSamples < 16000)
                            throw new Exception("Monitor did not render samples");
                    }
                }
                else
                    using (var endpoint = new AudioEndpoint(destination.Id, false))
                    {
                        var packet = new float[(int)endpoint.Capacity];
                        long generated = 0;
                        endpoint.Start();
                        var watch = Stopwatch.StartNew();
                        while (watch.ElapsedMilliseconds < 3000)
                        {
                            if (!endpoint.Ready.WaitOne(2000))
                                throw new Exception("Cable render timeout");
                            uint free = endpoint.FreeFrames();
                            if (free == 0)
                                continue;
                            for (int i = 0; i < free; i++)
                                packet[i] = (float)(0.05 * Math.Sin(2 * Math.PI * 997 * generated++ / endpoint.Format.Rate));
                            IntPtr pointer;
                            NativeAudio.Check(endpoint.Render.GetBuffer(free, out pointer));
                            try
                            {
                                endpoint.Format.Encode(pointer, packet, (int)free);
                            }
                            finally
                            {
                                NativeAudio.Check(endpoint.Render.ReleaseBuffer(free, 0));
                            }
                        }
                    }
            }
            finally
            {
                stop.Set();
                if (!thread.Join(4000))
                    throw new Exception("Cable capture stop timeout");
                ready.Dispose();
                stop.Dispose();
            }

            if (captureError != null)
                throw captureError;
            double rms = Math.Sqrt(energy / Math.Max(1, count)), amplitude = 2 * Math.Sqrt(cosine * cosine + sine * sine) / Math.Max(1, count);
            double frequency = 997;
            if (monitor)
            {
                // Independent-clock correction intentionally shifts a tone by up to 0.3%.
                // Search that band on the final second instead of requiring fixed phase.
                amplitude = 0;
                int n = Math.Min(captureRate, captured.Count), offset = captured.Count - n;
                for (double hz = 993; hz <= 1001; hz += 0.125)
                {
                    double c = 0, s = 0;
                    for (int i = 0; i < n; i++)
                    {
                        double a = 2 * Math.PI * hz * i / captureRate;
                        c += captured[offset + i] * Math.Cos(a);
                        s += captured[offset + i] * Math.Sin(a);
                    }

                    double level = 2 * Math.Sqrt(c * c + s * s) / n;
                    if (level > amplitude)
                    {
                        amplitude = level;
                        frequency = hz;
                    }
                }
            }

            string report = String.Format("frequency_hz={4:F3}; samples={0}; captured_rms={1:F6}; coherent_amplitude={2:F6}; signal_detected={3}; monitor={5}", count, rms, amplitude, count > 16000 && rms > 0.002 && amplitude / rms > 0.5, frequency, monitor);
            File.WriteAllText(path, report);
            Console.WriteLine(report);
            if (count < 16000 || rms < 0.002 || amplitude / rms < 0.5)
                throw new Exception("VB-CABLE synthetic signal was not reliably detected");
        }
    }
}

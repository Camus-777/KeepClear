using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace KeepClear
{
    // One optional Python process owns BOTH model states in serial order.
    // Binary PCM travels over local anonymous pipes; stdout is never used for logs.
    public sealed class ModelWorker : IAudioStage
    {
        private readonly Process process = new Process();
        private BinaryReader reader;
        private BinaryWriter writer;
        private CancellationTokenRegistration cancellation;
        private readonly object errorGate = new object();
        private string lastError = "";
        private int timedOut;
        private readonly object deadlineGate = new object();
        private readonly Timer watchdog;
        private long deadline;
        private byte[] inputBytes = new byte[1024], outputBytes = new byte[2048];
        private readonly TimingWindow sendTimes = new TimingWindow(), waitTimes = new TimingWindow(), receiveTimes = new TimingWindow();
        private readonly Stopwatch resourceClock = Stopwatch.StartNew();
        private double previousCpuMs, previousWallMs, nextResourceAt;
        private string resourceSummary = "resources=pending";

        internal string DiagnosticSummary()
        {
            // Called by the health timer, never by the audio processing thread.
            double now = resourceClock.Elapsed.TotalMilliseconds;
            if (now >= nextResourceAt)
            {
                nextResourceAt = now + 5000;
                try
                {
                    process.Refresh();
                    double cpu = process.TotalProcessorTime.TotalMilliseconds;
                    resourceSummary = String.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "pid={0} cpu_total_ms={1:F2} cpu_interval_ms={2:F2} wall_interval_ms={3:F2} cpu_cores={4:F3} working_set_bytes={5} private_bytes={6} threads={7}",
                        process.Id, cpu, cpu - previousCpuMs, now - previousWallMs,
                        (cpu - previousCpuMs) / Math.Max(1, now - previousWallMs), process.WorkingSet64, process.PrivateMemorySize64, process.Threads.Count);
                    previousCpuMs = cpu;
                    previousWallMs = now;
                }
                catch (Exception ex) { resourceSummary = "resources_unavailable=" + ex.GetType().Name; }
            }
            return resourceSummary + " send={" + sendTimes.Summary() + "} reply_wait={" + waitTimes.Summary()
                + "} receive={" + receiveTimes.Summary() + "}";
        }
        public string Name
        {
            get
            {
                return "Alkove → Denoiser";
            }
        }

        public ModelWorker(string python, string root, bool alkove, bool denoiser, CancellationToken token)
        {
            watchdog = new Timer(TimeoutKill, null, 50, 50);
            var info = new ProcessStartInfo(python);
            info.Arguments = "-I -B -u " + Quote(Path.Combine(root, "workers", "model_worker.py")) + " --models " + Quote(Path.Combine(root, "models")) + (alkove ? " --alkove" : "") + (denoiser ? " --denoiser" : "");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
            info.StandardErrorEncoding = Encoding.UTF8;
            info.WorkingDirectory = root;
            process.StartInfo = info;
            process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs args)
            {
                if (!String.IsNullOrEmpty(args.Data))
                {
                    if (args.Data.StartsWith("KC_METRIC ", StringComparison.Ordinal))
                    {
                        Diagnostics.Write("model-timing", args.Data.Substring(10));
                        return;
                    }
                    if (args.Data.StartsWith("KC_CPU ", StringComparison.Ordinal))
                    {
                        Diagnostics.Write("model-cpu", args.Data.Substring(7));
                        return;
                    }
                    lock (errorGate)
                        lastError = args.Data.Length > 2000 ? args.Data.Substring(0, 2000) : args.Data;
                }
            };
            try
            {
                process.Start();
                try { process.PriorityClass = ProcessPriorityClass.AboveNormal; }
                catch (Exception ex) { Diagnostics.Failure("model-priority", ex); }
                process.BeginErrorReadLine();
                cancellation = token.Register(Kill);
                reader = new BinaryReader(process.StandardOutput.BaseStream);
                writer = new BinaryWriter(process.StandardInput.BaseStream);
                Arm(45000);
                try
                {
                    if (Encoding.ASCII.GetString(ReadExactly(4)) != "CV01" || reader.ReadInt32() != 16000)
                        throw new InvalidDataException("模型工作进程协议不匹配。");
                }
                finally { Arm(0); }

                token.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                Kill();
                DrainExit();
                string detail = ErrorDetail();
                Dispose();
                throw new InvalidOperationException("模型启动失败。" + (String.IsNullOrEmpty(detail) ? ex.Message : detail), ex);
            }
        }

        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    slashes++;
                    continue;
                }

                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                result.Append(c);
                slashes = 0;
            }

            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }

        private byte[] ReadExactly(int count)
        {
            byte[] data = reader.ReadBytes(count);
            if (data.Length != count)
                throw new EndOfStreamException("模型进程已退出或音频回复被截断。");
            return data;
        }

        private string ErrorDetail()
        {
            if (Volatile.Read(ref timedOut) != 0)
                return "模型响应超时，已终止进程。";
            lock (errorGate)
                return lastError;
        }

        private void TimeoutKill(object ignored)
        {
            lock (deadlineGate)
            {
                if (deadline == 0 || Stopwatch.GetTimestamp() < deadline) return;
                deadline = 0;
                Interlocked.Exchange(ref timedOut, 1);
                Kill();
            }
        }

        private void Arm(int milliseconds)
        {
            lock (deadlineGate)
                deadline = milliseconds == 0 ? 0 : Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * milliseconds / 1000.0);
        }

        private void Kill()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Diagnostics.Failure("model-termination", ex);
            }
        }

        private void DrainExit()
        {
            try
            {
                if (process.WaitForExit(1000))
                    process.WaitForExit();
            }
            catch (InvalidOperationException)
            {
            }
        }

        public float[] Process(float[] samples)
        {
            try
            {
                Arm(2000);
                try
                {
                    long sentAt = Stopwatch.GetTimestamp();
                    int byteCount = samples.Length * 4;
                    if (inputBytes.Length < byteCount) Array.Resize(ref inputBytes, byteCount);
                    Buffer.BlockCopy(samples, 0, inputBytes, 0, byteCount);
                    writer.Write(samples.Length);
                    writer.Write(inputBytes, 0, byteCount);
                    writer.Flush();
                    long waitingAt = Stopwatch.GetTimestamp();
                    sendTimes.Add((waitingAt - sentAt) * 1000.0 / Stopwatch.Frequency);
                    int frames = reader.ReadInt32();
                    long receivingAt = Stopwatch.GetTimestamp();
                    waitTimes.Add((receivingAt - waitingAt) * 1000.0 / Stopwatch.Frequency);
                    if (frames < 0 || frames > 32000)
                        throw new InvalidDataException("模型返回的采样数量超出协议范围。");
                    byteCount = frames * 4;
                    if (outputBytes.Length < byteCount) Array.Resize(ref outputBytes, byteCount);
                    int offset = 0;
                    while (offset < byteCount)
                    {
                        int read = reader.BaseStream.Read(outputBytes, offset, byteCount - offset);
                        if (read == 0) throw new EndOfStreamException("模型进程已退出或音频回复被截断。");
                        offset += read;
                    }
                    var result = new float[frames];
                    Buffer.BlockCopy(outputBytes, 0, result, 0, byteCount);
                    receiveTimes.Add((Stopwatch.GetTimestamp() - receivingAt) * 1000.0 / Stopwatch.Frequency);
                    return result;
                }
                finally { Arm(0); }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("模型处理失败；已停止输出。" + ErrorDetail(), ex);
            }
        }

        public void Dispose()
        {
            watchdog.Dispose();
            Arm(0);
            cancellation.Dispose();
            Kill();
            DrainExit();
            if (writer != null)
                writer.Dispose();
            if (reader != null)
                reader.Dispose();
            process.Dispose();
        }
    }
}

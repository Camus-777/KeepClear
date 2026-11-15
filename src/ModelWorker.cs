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
        public string Name
        {
            get
            {
                return "Alkove → Denoiser";
            }
        }

        public ModelWorker(string python, string root, bool alkove, bool denoiser, CancellationToken token)
        {
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
                    lock (errorGate)
                        lastError = args.Data.Length > 2000 ? args.Data.Substring(0, 2000) : args.Data;
            };
            try
            {
                process.Start();
                process.BeginErrorReadLine();
                cancellation = token.Register(Kill);
                reader = new BinaryReader(process.StandardOutput.BaseStream);
                writer = new BinaryWriter(process.StandardInput.BaseStream);
                using (var timer = new Timer(TimeoutKill, null, 45000, Timeout.Infinite))
                {
                    if (Encoding.ASCII.GetString(ReadExactly(4)) != "CV01" || reader.ReadInt32() != 16000)
                        throw new InvalidDataException("模型工作进程协议不匹配。");
                }

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
            Interlocked.Exchange(ref timedOut, 1);
            Kill();
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
                using (var timer = new Timer(TimeoutKill, null, 2000, Timeout.Infinite))
                {
                    var bytes = new byte[samples.Length * 4];
                    Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                    writer.Write(samples.Length);
                    writer.Write(bytes);
                    writer.Flush();
                    int frames = reader.ReadInt32();
                    if (frames < 0 || frames > 32000)
                        throw new InvalidDataException("模型返回的采样数量超出协议范围。");
                    bytes = ReadExactly(frames * 4);
                    var result = new float[frames];
                    Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
                    return result;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("模型处理失败；已停止输出。" + ErrorDetail(), ex);
            }
        }

        public void Dispose()
        {
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

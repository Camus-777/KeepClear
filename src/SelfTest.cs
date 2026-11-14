using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace KeepQuiet
{
    internal static class SelfTest
    {
        // Exercises the exact shipped C# -> Python -> two-model path without a microphone.
        public static int Run(string reportPath)
        {
            var report = new List<string>();
            try
            {
                string root = AppDomain.CurrentDomain.BaseDirectory;
                string python = AppSettings.ResolvePython(root, AppSettings.Load().Python);
                report.Add("python=" + python);
                int total = 0;
                double totalMs = 0, maxMs = 0;
                using (var worker = new ModelWorker(python, root, true, true, CancellationToken.None))
                {
                    var watch = new Stopwatch();
                    for (int packet = 0; packet < 128; packet++)
                    {
                        var pcm = new float[256];
                        for (int i = 0; i < pcm.Length; i++)
                            pcm[i] = (float)(0.05 * Math.Sin(2 * Math.PI * 440 * (packet * 256 + i) / 16000));
                        watch.Restart();
                        float[] result = worker.Process(pcm);
                        watch.Stop();
                        foreach (float value in result)
                            if (Single.IsNaN(value) || Single.IsInfinity(value))
                                throw new InvalidDataException("Non-finite model output.");
                        total += result.Length;
                        totalMs += watch.Elapsed.TotalMilliseconds;
                        maxMs = Math.Max(maxMs, watch.Elapsed.TotalMilliseconds);
                    }
                }

                if (total < 31000 || total > 32768)
                    throw new InvalidDataException("Unexpected output sample count: " + total);
                report.Add("models=Alkove Focus -> Denoiser DNS48");
                report.Add("input_samples=32768; output_samples=" + total);
                report.Add(String.Format("mean_ms={0:F3}; max_ms={1:F3}", totalMs / 128, maxMs));
                report.Add("PASS; synthetic audio only; no audio devices opened");
                File.WriteAllLines(reportPath, report.ToArray());
                return 0;
            }
            catch (Exception ex)
            {
                report.Add("FAIL: " + ex);
                File.WriteAllLines(reportPath, report.ToArray());
                return 1;
            }
        }
    }
}

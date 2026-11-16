using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace KeepClear
{
    internal static class StabilityTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var queue = new SampleBuffer(8);
            float[] scratch = { 1, 2, 99, 99 };
            queue.Write(scratch, 2);
            scratch[0] = -100;
            var read = new float[8];
            check(queue.Read(read, 8) == 2 && read[0] == 1 && read[1] == 2, "counted FIFO copies only valid samples and retains ownership");
            queue.WriteLatest(new float[] { 3, 4, 100 }, 2);
            check(queue.Read(read, 8) == 2 && read[0] == 3 && read[1] == 4, "counted latest FIFO excludes stale buffer tail");
            bool invalid = false;
            try { queue.Write(scratch, 5); } catch (ArgumentOutOfRangeException) { invalid = true; }
            check(invalid && queue.Count == 0, "invalid counted FIFO write leaves queue intact");

            foreach (int rate in new[] { 8000, 16000, 44100, 48000, 96000, 192000 })
            {
                var owned = new StreamResampler(16000, rate);
                var reused = new StreamResampler(16000, rate);
                var buffer = new float[0];
                bool same = true;
                for (int packet = 0; packet < 40; packet++)
                {
                    int length = packet % 7 == 0 ? 0 : 31 + packet * 13;
                    var data = new float[length + 9];
                    for (int i = 0; i < length; i++) data[i] = (float)Math.Sin((packet * 100 + i) * .07);
                    for (int i = length; i < data.Length; i++) data[i] = float.NaN;
                    double ratio = packet % 2 == 0 ? .997 : 1.003;
                    float[] expected = owned.Push(data.Take(length).ToArray(), ratio);
                    int count = reused.PushInto(data, length, ref buffer, ratio);
                    same &= count == expected.Length && expected.SequenceEqual(buffer.Take(count));
                }
                check(same, "reused resampler exact samples across variable packets, empty input, clock correction and poisoned tails at " + rate);
                var original = buffer;
                var small = new float[32];
                reused.PushInto(small, small.Length, ref buffer);
                check(Object.ReferenceEquals(original, buffer), "resampler retains allocated capacity at " + rate);
            }

            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                string collected = "";
                using (var log = new AsyncLogWriter(batch => { entered.Set(); release.WaitOne(); collected += batch; }, 4))
                {
                    try
                    {
                        log.Enqueue("first\n", false);
                        check(entered.WaitOne(2000), "log writer reaches injected slow disk sink");
                        for (int i = 0; i < 30; i++) log.Enqueue("ordinary\n", false);
                        log.Enqueue("FAULT retained\n", true);
                        check(log.Summary().Contains("pending=4 capacity=4"), "slow logging retains a bounded queue");
                        check(!log.Flush(20), "log flush has a finite deadline while disk is blocked");
                    }
                    finally { release.Set(); }
                    check(log.Flush(2000) && collected.Contains("FAULT retained") && collected.Contains("[log-drop]")
                        && collected.IndexOf("FAULT retained") < collected.IndexOf("ordinary"), "overflow prioritizes fault and reports dropped ordinary logs after disk recovery");
                }
            }

            int writes = 0;
            using (var log = new AsyncLogWriter(batch => { if (Interlocked.Increment(ref writes) == 1) throw new IOException("fixture disk failure"); }))
            {
                log.Enqueue("failed\n", true);
                check(log.Flush(2000) && log.Summary().Contains("write_failures=1"), "disk errors are isolated and observable");
                log.Enqueue("retry\n", true);
                check(log.Flush(2000) && writes == 2, "writer continues after a failed disk write");
            }
            var records = new List<string>();
            using (var log = new AsyncLogWriter(batch => records.AddRange(batch.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)), 1000))
            {
                var producers = Enumerable.Range(0, 4).Select(id => new Thread(() =>
                {
                    for (int i = 0; i < 100; i++) log.Enqueue(id + "-" + i + "\n", false);
                })).ToArray();
                foreach (var producer in producers) producer.Start();
                foreach (var producer in producers) producer.Join();
                check(log.Flush(2000) && records.Count == 400 && records.Distinct().Count() == 400, "concurrent diagnostic producers flush complete records without duplication");
            }
        }
    }
}

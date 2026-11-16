using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Reflection;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Threading;

namespace KeepClear
{
    // One bounded queue owns all disk I/O. Audio callers only enqueue complete records.
    internal sealed class AsyncLogWriter : IDisposable
    {
        private sealed class Entry
        {
            internal string Text;
            internal bool Critical;
        }
        private readonly object gate = new object();
        private readonly LinkedList<Entry> queue = new LinkedList<Entry>();
        private readonly Action<string> sink;
        private readonly Thread thread;
        private readonly int capacity;
        private bool writing, stopping;
        private long dropped, droppedCritical, failures;

        internal AsyncLogWriter(Action<string> sink, int capacity = 512)
        {
            if (sink == null || capacity < 1) throw new ArgumentException("Invalid log writer configuration");
            this.sink = sink;
            this.capacity = capacity;
            thread = new Thread(Run) { IsBackground = true, Name = "KeepClear log writer" };
            thread.Start();
        }

        internal void Enqueue(string text, bool critical)
        {
            if (text.Length > 16384) text = text.Substring(0, 16384) + " [truncated]" + Environment.NewLine;
            lock (gate)
            {
                if (stopping) { dropped++; if (critical) droppedCritical++; return; }
                if (queue.Count == capacity)
                {
                    if (!critical) { dropped++; return; }
                    var victim = queue.First;
                    while (victim != null && victim.Value.Critical) victim = victim.Next;
                    if (victim == null) { victim = queue.First; droppedCritical++; }
                    queue.Remove(victim);
                    dropped++;
                }
                var entry = new Entry { Text = text, Critical = critical };
                var before = critical ? queue.First : null;
                while (before != null && before.Value.Critical) before = before.Next;
                if (before == null) queue.AddLast(entry);
                else queue.AddBefore(before, entry);
                Monitor.PulseAll(gate);
            }
        }

        private void Run()
        {
            long reportedDrops = 0;
            while (true)
            {
                var batch = new StringBuilder();
                lock (gate)
                {
                    while (queue.Count == 0 && !stopping) Monitor.Wait(gate);
                    if (queue.Count == 0) return;
                    writing = true;
                    if (dropped != reportedDrops)
                    {
                        batch.AppendLine(DateTime.UtcNow.ToString("o") + " [log-drop] total=" + dropped + " critical=" + droppedCritical);
                        reportedDrops = dropped;
                    }
                    for (int i = 0; i < 32 && queue.Count > 0; i++)
                    {
                        batch.Append(queue.First.Value.Text);
                        queue.RemoveFirst();
                    }
                }
                try { sink(batch.ToString()); }
                catch (Exception) { Interlocked.Increment(ref failures); }
                finally
                {
                    lock (gate) { writing = false; Monitor.PulseAll(gate); }
                }
            }
        }

        internal bool Flush(int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            lock (gate)
            {
                while (queue.Count > 0 || writing)
                {
                    int left = milliseconds - (int)watch.ElapsedMilliseconds;
                    if (left <= 0) return false;
                    Monitor.Wait(gate, left);
                }
                return true;
            }
        }

        internal string Summary()
        {
            lock (gate)
                return "pending=" + queue.Count + " capacity=" + capacity + " dropped=" + dropped
                    + " critical_dropped=" + droppedCritical + " write_failures=" + Interlocked.Read(ref failures);
        }

        public void Dispose()
        {
            lock (gate) { stopping = true; Monitor.PulseAll(gate); }
            thread.Join(2000);
        }
    }

    internal static class Diagnostics
    {
        internal static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeepClear", "logs");
        private static readonly string file = Path.Combine(Folder, "KeepClear-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Process.GetCurrentProcess().Id + ".log");
        private static readonly AsyncLogWriter writer = new AsyncLogWriter(delegate (string batch)
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(file, batch, Encoding.UTF8);
        });
        private static int exitFlushStarted;

        static Diagnostics()
        {
            AppDomain.CurrentDomain.ProcessExit += delegate { Flush(); };
        }

        internal static bool Flush()
        {
            // Main's finally and ProcessExit share one shutdown waiting budget.
            return writer.Flush(Interlocked.Exchange(ref exitFlushStarted, 1) == 0 ? 2000 : 0);
        }
        internal static string Summary() { return writer.Summary(); }
        internal static void Write(string stage, string message)
        {
            writer.Enqueue(DateTime.UtcNow.ToString("o") + " [" + stage + "] " + message + Environment.NewLine, false);
        }

        internal static void Failure(string stage, Exception error)
        {
            writer.Enqueue(DateTime.UtcNow.ToString("o") + " [" + stage + "] " + error + Environment.NewLine, true);
        }

        internal static string Code(Exception error)
        {
            return "0x" + error.HResult.ToString("X8");
        }

        internal static void Session()
        {
            try
            {
                string cpu = "unknown";
                using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    if (key != null) cpu = Convert.ToString(key.GetValue("ProcessorNameString"));
                var assembly = Assembly.GetExecutingAssembly();
                Write("session", "version=" + assembly.GetName().Version + " build=" + assembly.ManifestModule.ModuleVersionId + " os=" + Environment.OSVersion + " runtime=" + Environment.Version + " cpu=" + cpu.Trim() + " logical_processors=" + Environment.ProcessorCount);
            }
            catch (Exception ex) { Failure("session-info", ex); }
        }
    }
}

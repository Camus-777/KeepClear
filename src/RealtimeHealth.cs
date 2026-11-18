using System;

namespace KeepClear
{
    internal enum RecoveryStep { None, Started, Completed }

    // Compare one-second averages. Stable latency does not request recovery.
    internal sealed class RealtimeRecovery
    {
        private readonly object gate = new object();
        private bool active;
        internal const double EmergencyMs = 500;
        private double windowStart, lastObserved = Double.NegativeInfinity;
        private double sum, previousMean, growthMs;
        private int samples, growingWindows, settledWindows;
        private double windowMaximum;
        internal int Count { get; private set; }
        internal bool Active { get { lock (gate) return active; } }

        internal RecoveryStep Observe(double now, double inputMs, double outputMs)
        {
            lock (gate)
            {
                if (now - lastObserved < .08) return RecoveryStep.None;
                double total = inputMs + outputMs;
                if (Double.IsNegativeInfinity(lastObserved) || now - lastObserved > .5)
                {
                    windowStart = now;
                    previousMean = total;
                    sum = 0;
                    growthMs = 0;
                    samples = growingWindows = settledWindows = 0;
                    windowMaximum = total;
                }
                lastObserved = now;
                sum += total;
                windowMaximum = Math.Max(windowMaximum, total);
                samples++;
                if (total >= EmergencyMs && !active)
                {
                    active = true;
                    settledWindows = 0;
                    Count++;
                    return RecoveryStep.Started;
                }
                if (now - windowStart < 1) return RecoveryStep.None;
                double mean = sum / samples;
                bool settled = mean <= 160 && windowMaximum < 200;
                double increase = mean - previousMean;
                bool growing = increase >= 2;
                growingWindows = growing ? growingWindows + 1 : 0;
                growthMs = growing ? growthMs + increase : 0;
                previousMean = mean;
                sum = 0;
                samples = 0;
                windowStart = now;
                windowMaximum = 0;
                if (active)
                {
                    settledWindows = settled && !growing ? settledWindows + 1 : 0;
                    if (settledWindows >= 3)
                    {
                        active = false;
                        return RecoveryStep.Completed;
                    }
                }
                else if (growingWindows >= 2 && growthMs >= 20 && total >= 200)
                {
                    active = true;
                    settledWindows = 0;
                    Count++;
                    return RecoveryStep.Started;
                }
                return RecoveryStep.None;
            }
        }
    }

    internal sealed class TimingWindow
    {
        private readonly object gate = new object();
        private readonly double[] samples = new double[256];
        private int position, count;
        internal void Add(double milliseconds)
        {
            lock (gate)
            {
                samples[position] = milliseconds;
                position = (position + 1) % samples.Length;
                count = Math.Min(count + 1, samples.Length);
            }
        }
        internal string Summary()
        {
            double[] copy;
            lock (gate)
            {
                copy = new double[count];
                Array.Copy(samples, copy, count);
            }
            if (copy.Length == 0) return "samples=0";
            Array.Sort(copy);
            double sum = 0;
            foreach (double value in copy) sum += value;
            return String.Format(System.Globalization.CultureInfo.InvariantCulture,
                "samples={0} mean_ms={1:F3} p95_ms={2:F3} p99_ms={3:F3} max_ms={4:F3}",
                copy.Length, sum / copy.Length, copy[(int)((copy.Length - 1) * .95)], copy[(int)((copy.Length - 1) * .99)], copy[copy.Length - 1]);
        }
    }
}

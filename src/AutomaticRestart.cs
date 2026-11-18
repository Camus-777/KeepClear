using System;

namespace KeepClear
{
    internal interface IAudioSession : IDisposable
    {
        void Start();
        void SetMonitor(string deviceId);
        EngineSnapshot Snapshot();
    }

    // A successful restart alone does not replenish the budget.
    internal sealed class AutomaticRestart
    {
        private int attempts;
        private double healthySince = -1, lastObserved = Double.NegativeInfinity;
        private double windowStart, queueSum, previousMean, growthMs;
        private int samples, growingWindows;
        private long captureAt, blocksAt, renderAt;
        internal int Attempts { get { return attempts; } }

        internal static bool CanRetry(AudioFailureKind kind)
        {
            return kind == AudioFailureKind.Slow || kind == AudioFailureKind.InputTimeout
                || kind == AudioFailureKind.OutputTimeout || kind == AudioFailureKind.ModelTimeout;
        }

        internal bool TryReserve(AudioFailureKind kind, out int delayMs)
        {
            healthySince = -1;
            lastObserved = Double.NegativeInfinity;
            delayMs = 0;
            if (!CanRetry(kind) || attempts >= 3) return false;
            delayMs = attempts == 0 ? 2000 : attempts == 1 ? 5000 : 15000;
            attempts++;
            return true;
        }

        // Only consecutive observed, progressing, low-queue windows earn retry credit.
        internal bool ObserveHealthy(double now, EngineSnapshot snapshot)
        {
            if (now <= lastObserved) return false;
            bool gap = Double.IsNegativeInfinity(lastObserved) || now - lastObserved > 1.5;
            lastObserved = now;
            double total = snapshot.InputQueueMs + snapshot.OutputQueueMs;
            bool eligible = snapshot.State == "运行中" && String.IsNullOrEmpty(snapshot.Error)
                && !snapshot.Recovering && total >= 0 && total < 200;
            if (gap || !eligible)
            {
                healthySince = -1;
                samples = growingWindows = 0;
                queueSum = growthMs = 0;
                previousMean = total;
                windowStart = now;
                captureAt = snapshot.CaptureFrames;
                blocksAt = snapshot.Blocks;
                renderAt = snapshot.ProcessedRenderFrames;
                return false;
            }
            queueSum += total;
            samples++;
            if (now - windowStart < 1) return false;
            double mean = queueSum / samples;
            double increase = mean - previousMean;
            growingWindows = increase >= 2 ? growingWindows + 1 : 0;
            growthMs = increase >= 2 ? growthMs + increase : 0;
            bool progressing = snapshot.CaptureFrames > captureAt && snapshot.Blocks > blocksAt
                && snapshot.ProcessedRenderFrames > renderAt;
            bool healthy = progressing && !(growingWindows >= 2 && growthMs >= 20);
            if (!healthy) healthySince = -1;
            else if (healthySince < 0) healthySince = now;
            previousMean = mean;
            windowStart = now;
            queueSum = 0;
            samples = 0;
            captureAt = snapshot.CaptureFrames;
            blocksAt = snapshot.Blocks;
            renderAt = snapshot.ProcessedRenderFrames;
            if (healthySince < 0 || now - healthySince < 120 || attempts == 0) return false;
            attempts = 0;
            return true;
        }

        internal void Reset()
        {
            attempts = 0;
            healthySince = -1;
            lastObserved = Double.NegativeInfinity;
        }
    }
}

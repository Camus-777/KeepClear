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
        private double healthySince = -1;
        internal int Attempts { get { return attempts; } }

        internal static bool CanRetry(AudioFailureKind kind)
        {
            return kind == AudioFailureKind.Slow || kind == AudioFailureKind.InputTimeout
                || kind == AudioFailureKind.OutputTimeout || kind == AudioFailureKind.ModelTimeout;
        }

        internal bool TryReserve(AudioFailureKind kind, out int delayMs)
        {
            healthySince = -1;
            delayMs = 0;
            if (!CanRetry(kind) || attempts >= 3) return false;
            delayMs = attempts == 0 ? 2000 : attempts == 1 ? 5000 : 15000;
            attempts++;
            return true;
        }

        internal void ObserveHealthy(double now)
        {
            if (healthySince < 0) healthySince = now;
            if (now - healthySince >= 120) attempts = 0;
        }

        internal void Reset()
        {
            attempts = 0;
            healthySince = -1;
        }
    }
}

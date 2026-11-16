using System;

namespace KeepClear
{
    internal static class RepairTests
    {
        private static RealtimeRecovery Timeline(Func<double, double> latency, double seconds)
        {
            var policy = new RealtimeRecovery();
            for (int tick = 0; tick <= seconds * 10; tick++)
            {
                double at = tick / 10.0;
                policy.Observe(at, latency(at), 0);
            }
            return policy;
        }

        internal static void Run(Action<bool, string> check)
        {
            check(Timeline(t => 70, 120).Count == 0, "stable 70 ms continues for two minutes without intervention");
            check(Timeline(t => 75 + 5 * Math.Sin(t), 120).Count == 0, "stable 70 to 80 ms does not request recovery");
            check(Timeline(t => t >= 2 && t < 2.5 ? 120 : 70, 10).Count == 0, "70 to 120 to 70 ms transient passes without recovery");
            check(Timeline(t => 250, 15).Count == 0, "stable higher latency is not mistaken for continuous growth");
            check(Timeline(t => 70 + 110 * t, 3).Active, "two rising windows above 200 ms start recovery");
            check(Timeline(t => 70 + 5 * t, 45).Active, "slow but sustained growth is detected before the capacity ceiling");
            var policy = new RealtimeRecovery();
            for (int tick = 0; tick <= 20; tick++) policy.Observe(tick / 10.0, 70 + tick * 11, 0);
            check(policy.Active && policy.Count == 1, "sustained growth is recognized before 500 ms capacity pressure");
            check(policy.Observe(1, 0, 0) == RecoveryStep.None && policy.Active, "older observations cannot finish recovery");
            for (int tick = 21; tick <= 50; tick++) policy.Observe(tick / 10.0, 250, 0);
            check(!policy.Active, "a flattened queue completes recovery without a one-second stop deadline");
            for (int tick = 51; tick <= 80; tick++) policy.Observe(tick / 10.0, 250 + (tick - 50) * 7, 0);
            check(policy.Active && policy.Count == 2, "growth can recover again without a 30-second failure rule");
            policy = new RealtimeRecovery();
            check(policy.Observe(0, 300, 200) == RecoveryStep.Started, "combined input and output at 500 ms request immediate capacity intervention");
            check(!Timeline(t => t < 4 ? 70 + 100 * t : 70, 8).Active, "falling total latency leaves recovery");
            policy = new RealtimeRecovery();
            for (int tick = 0; tick <= 30; tick++) policy.Observe(tick / 10.0, 300 - tick * 5, 70 + tick * 5);
            check(policy.Count == 0, "moving backlog between input and output does not create a false trend");
            policy = new RealtimeRecovery();
            policy.Observe(0, 70, 0); policy.Observe(5, 300, 0); policy.Observe(5.1, 300, 0);
            check(!policy.Active, "a monitoring gap does not invent unobserved growing windows");

            var buffer = new SampleBuffer(16);
            buffer.Write(new float[] { 1, 2, 0, 0, 0, 0, 3, 4 });
            check(buffer.TrimSilence(4, 4, .00001f) == 4, "confirmed silence can reduce backlog");
            var read = new float[16];
            buffer.Read(read, 4);
            check(read[0] == 1 && read[1] == 2 && read[2] == 3 && read[3] == 4, "silence trimming preserves voiced samples in exact order");
            buffer.Write(new float[] { 1, 0, 0, 2 });
            check(buffer.TrimSilence(1, 4, .00001f) == 0, "a short near-zero crossing is not treated as a pause");
            buffer.KeepLatest(0);
            buffer.Write(new float[] { .001f, .001f, .001f, .001f });
            check(buffer.TrimSilence(0, 4, .00001f) == 0, "quiet speech above the conservative threshold is retained");
            buffer.KeepLatest(0);
            buffer.Write(new float[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 });
            buffer.Read(read, 10);
            buffer.Write(new float[] { 0, 0, 0, 0, 12, 13 });
            check(buffer.TrimSilence(4, 4, .00001f) == 4, "silence trimming handles wrapped circular storage");
            buffer.Read(read, 4);
            check(read[0] == 10 && read[1] == 11 && read[2] == 12 && read[3] == 13, "wrapped silence trimming retains all voiced values");
            buffer.Write(new float[] { 1, 2, 3, 4 });
            check(buffer.KeepLatest(2, 2) == 2, "capacity intervention retains bounded output with a crossfade");
            buffer.Read(read, 2);
            check(read[0] == 1 && read[1] == 4, "emergency splice fades from the old audio into the retained tail");
            Replay(check, false);
            Replay(check, true);
            bool burstsPassed = true;
            foreach (int pause in new[] { 20, 40, 100, 200 })
                Replay((passed, name) => burstsPassed &= passed, false, pause);
            check(burstsPassed, "20, 40, 100 and 200 ms interruptions preserve every simulated voice sample");
        }

        private static void Replay(Action<bool, string> check, bool sustained, int pauseMs = 350)
        {
            var input = new SampleBuffer(8192);
            var output = new SampleBuffer(8192);
            var policy = new RealtimeRecovery();
            int source = 0, consumed = 0, dropped = 0, nextProcess = 0;
            bool overflow = false;
            var frame = new float[256]; var playback = new float[160];
            for (int ms = 0; ms < 6000; ms++)
            {
                if (ms % 10 == 0)
                {
                    var captured = new float[160];
                    for (int i = 0; i < captured.Length; i++) captured[i] = ++source;
                    if (!input.Write(captured)) { overflow = true; break; }
                    output.Read(playback, 160);
                }
                if (ms >= nextProcess && input.Count >= 256 && (sustained || ms < 1000 || ms >= 1000 + pauseMs))
                {
                    input.Read(frame, 256);
                    for (int i = 0; i < frame.Length; i++)
                        if (frame[i] != ++consumed) throw new Exception("Lost model-input continuity");
                    if ((input.Count + output.Count + frame.Length) / 16.0 >= RealtimeRecovery.EmergencyMs)
                        dropped += output.KeepLatest(Math.Max(0, 1920 - frame.Length));
                    if (!output.Write(frame)) { overflow = true; break; }
                    nextProcess = ms + (sustained ? 24 : 8);
                }
                policy.Observe(ms / 1000.0, input.Count / 16.0, output.Count / 16.0);
            }
            if (sustained)
                check(overflow, "truly inadequate sustained throughput remains bounded by input capacity");
            else
                check(!overflow && dropped == 0 && input.Count < 768 && output.Count < 8192,
                    "350 ms transient continues with every voiced sample retained and no manual reset");
        }
    }
}

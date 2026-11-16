using System;
using System.Collections.Generic;

namespace KeepClear
{
    // Elapsed-time ballistics: identical motion at different UI refresh rates.
    internal sealed class MeterEnvelope
    {
        public double Db { get; private set; }

        public MeterEnvelope()
        {
            Db = -60;
        }

        public void Advance(double peak, double seconds)
        {
            double target = peak > 0.000001 ? Math.Max(-60, Math.Min(0, 20 * Math.Log10(peak))) : -60;
            double tau = target > Db ? 0.025 : 0.24;
            Db += (target - Db) * (1 - Math.Exp(-Math.Max(0, seconds) / tau));
        }
    }

    // A bounded FIFO: overflow is an explicit fault, never unbounded latency.
    public sealed class SampleBuffer
    {
        private readonly float[] data;
        private int head, count;
        private readonly object gate = new object();
        public SampleBuffer(int capacity)
        {
            if (capacity < 1)
                throw new ArgumentOutOfRangeException("capacity");
            data = new float[capacity];
        }

        public int Count
        {
            get
            {
                lock (gate)
                    return count;
            }
        }

        public bool Write(float[] source)
        {
            return Write(source, source.Length);
        }

        internal bool Write(float[] source, int length)
        {
            if (length < 0 || length > source.Length) throw new ArgumentOutOfRangeException("length");
            lock (gate)
            {
                if (length > data.Length - count)
                    return false;
                for (int i = 0; i < length; i++)
                    data[(head + count + i) % data.Length] = source[i];
                count += length;
                return true;
            }
        }

        // Optional local monitoring must never block or fault the main route.
        public void WriteLatest(float[] source)
        {
            WriteLatest(source, source.Length);
        }

        internal void WriteLatest(float[] source, int validLength)
        {
            if (validLength < 0 || validLength > source.Length) throw new ArgumentOutOfRangeException("validLength");
            lock (gate)
            {
                int start = Math.Max(0, validLength - data.Length);
                int length = validLength - start;
                int discard = Math.Max(0, count + length - data.Length);
                head = (head + discard) % data.Length;
                count -= discard;
                for (int i = start; i < validLength; i++)
                    data[(head + count++) % data.Length] = source[i];
            }
        }

        public int Read(float[] destination, int wanted)
        {
            lock (gate)
            {
                int n = Math.Min(wanted, count);
                for (int i = 0; i < n; i++)
                    destination[i] = data[(head + i) % data.Length];
                head = (head + n) % data.Length;
                count -= n;
                return n;
            }
        }

        internal int KeepLatest(int wanted)
        {
            return KeepLatest(wanted, 0);
        }

        internal int KeepLatest(int wanted, int fadeSamples)
        {
            lock (gate)
            {
                int discard = Math.Max(0, count - Math.Max(0, wanted));
                int blend = Math.Min(fadeSamples, Math.Min(discard, count - discard));
                for (int i = 0; i < blend; i++)
                {
                    float fraction = (float)i / Math.Max(1, blend - 1);
                    int target = (head + discard + i) % data.Length;
                    data[target] = data[(head + i) % data.Length] * (1 - fraction) + data[target] * fraction;
                }
                head = (head + discard) % data.Length;
                count -= discard;
                return discard;
            }
        }

        // Remove a confirmed quiet run while retaining every voiced sample.
        internal int TrimSilence(int wanted, int minimumRun, float threshold)
        {
            lock (gate)
            {
                int removable = Math.Max(0, count - wanted), run = 0;
                for (int i = 0; i < count; i++)
                {
                    if (Math.Abs(data[(head + i) % data.Length]) <= threshold) run++;
                    else
                    {
                        if (run >= minimumRun) return RemoveRun(i - run, Math.Min(run, removable));
                        run = 0;
                    }
                    if (run >= minimumRun && run >= removable)
                        return RemoveRun(i - run + 1, removable);
                }
                return run >= minimumRun ? RemoveRun(count - run, Math.Min(run, removable)) : 0;
            }
        }

        private int RemoveRun(int start, int length)
        {
            for (int i = start; i < count - length; i++)
                data[(head + i) % data.Length] = data[(head + i + length) % data.Length];
            count -= length;
            return length;
        }

    }

    // Stateful, windowed-sinc polyphase FIR. Preserves phase across packet boundaries.
    // Downsampling low-pass cutoff follows the smaller Nyquist frequency.
    public sealed class StreamResampler
    {
        private const int Half = 32, Phases = 512;
        private readonly double[, ] kernel = new double[Phases, Half * 2];
        private readonly List<float> pending = new List<float>();
        private double position = Half;
        private readonly double step;
        public StreamResampler(int inputRate, int outputRate)
        {
            if (inputRate < 8000 || outputRate < 8000 || inputRate > 192000 || outputRate > 192000)
                throw new ArgumentOutOfRangeException("inputRate");
            step = (double)inputRate / outputRate;
            for (int i = 0; i < Half; i++)
                pending.Add(0);
            double cutoff = Math.Min(1, (double)outputRate / inputRate) * 0.92;
            for (int phase = 0; phase < Phases; phase++)
            {
                double sum = 0;
                for (int j = 0; j < Half * 2; j++)
                {
                    double x = j - Half + 1 - (double)phase / Phases;
                    double sinc = Math.Abs(x) < 1e-10 ? cutoff : Math.Sin(Math.PI * cutoff * x) / (Math.PI * x);
                    double window = 0.42 + 0.5 * Math.Cos(Math.PI * x / Half) + 0.08 * Math.Cos(2 * Math.PI * x / Half);
                    kernel[phase, j] = sinc * window;
                    sum += kernel[phase, j];
                }

                for (int j = 0; j < Half * 2; j++)
                    kernel[phase, j] /= sum;
            }
        }

        public float[] Push(float[] input, double clockRatio)
        {
            float[] output = new float[0];
            int count = PushInto(input, input.Length, ref output, clockRatio);
            Array.Resize(ref output, count);
            return output;
        }

        // The caller owns this output and consumes only the returned prefix before reusing it.
        internal int PushInto(float[] input, int count, ref float[] output, double clockRatio = 1)
        {
            if (count < 0 || count > input.Length) throw new ArgumentOutOfRangeException("count");
            if (clockRatio < 0.99 || clockRatio > 1.01)
                throw new ArgumentOutOfRangeException("clockRatio");
            for (int i = 0; i < count; i++) pending.Add(input[i]);
            int written = 0;
            while (position + Half < pending.Count)
            {
                int center = (int)position;
                int phase = Math.Min(Phases - 1, (int)((position - center) * Phases));
                double sample = 0;
                for (int j = 0; j < Half * 2; j++)
                    sample += pending[center - Half + 1 + j] * kernel[phase, j];
                if (written == output.Length) Array.Resize(ref output, Math.Max(256, output.Length * 2));
                output[written++] = (float)sample;
                position += step * clockRatio;
            }

            int consumed = Math.Max(0, (int)position - Half);
            if (consumed > 0)
            {
                pending.RemoveRange(0, consumed);
                position -= consumed;
            }

            return written;
        }

        public float[] Push(float[] input)
        {
            return Push(input, 1);
        }
    }

    public interface IAudioStage : IDisposable
    {
        string Name { get; }

        // 16 kHz mono float PCM. Output may be empty or variable length.
        float[] Process(float[] samples);
    }

    public sealed class SerialPipeline : IDisposable
    {
        private readonly IAudioStage[] stages;
        public SerialPipeline(params IAudioStage[] stages)
        {
            this.stages = stages;
        }

        public float[] Process(float[] samples)
        {
            foreach (var stage in stages)
            {
                if (samples.Length == 0)
                    break;
                samples = stage.Process(samples);
                if (samples == null || samples.Length > 32000)
                    throw new InvalidOperationException(stage.Name + " 返回了无效的音频长度。");
                foreach (float value in samples)
                    if (float.IsNaN(value) || float.IsInfinity(value))
                        throw new InvalidOperationException(stage.Name + " 返回了无效的音频采样。");
            }

            return samples;
        }

        public void Dispose()
        {
            for (int i = stages.Length - 1; i >= 0; i--)
                stages[i].Dispose();
        }
    }

    public sealed class GainLimiter : IAudioStage
    {
        public string Name
        {
            get
            {
                return "输出增益与限幅";
            }
        }

        public volatile float GainDb;
        private double smoothed = 1;
        public float[] Process(float[] samples)
        {
            double target = Math.Pow(10, GainDb / 20.0);
            for (int i = 0; i < samples.Length; i++)
            {
                // 5 ms gain smoothing; memoryless ceiling prevents overshoot.
                smoothed += (target - smoothed) * 0.0124;
                double value = samples[i] * smoothed;
                samples[i] = (float)Math.Max(-0.98, Math.Min(0.98, value));
            }

            return samples;
        }

        public void Dispose()
        {
        }
    }
}

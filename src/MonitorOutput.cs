using System;
using System.Threading;

namespace KeepQuiet
{
    // Independent WASAPI endpoint/clock. A monitor failure cannot stop VB-CABLE.
    internal sealed class MonitorOutput : IDisposable
    {
        private readonly SampleBuffer input = new SampleBuffer(2048);
        private readonly CancellationTokenSource stop;
        private readonly Thread thread;
        private readonly object disposeGate = new object();
        private bool disposed;
        private volatile string error = "";
        private int active;
        private long rendered;
        public string Error
        {
            get
            {
                return error;
            }
        }

        public bool Active
        {
            get
            {
                return Volatile.Read(ref active) != 0;
            }
        }

        public long RenderedSamples
        {
            get
            {
                return Interlocked.Read(ref rendered);
            }
        }

        public MonitorOutput(string deviceId, CancellationToken parent)
        {
            stop = CancellationTokenSource.CreateLinkedTokenSource(parent);
            thread = new Thread(delegate ()
            {
                Run(deviceId);
            })
            {
                IsBackground = true,
                Name = "KeepQuiet local monitor"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void Push(float[] pcm)
        {
            if (!stop.IsCancellationRequested && String.IsNullOrEmpty(error))
                input.WriteLatest(pcm);
        }

        private void Run(string deviceId)
        {
            IntPtr mmcss = IntPtr.Zero;
            try
            {
                using (var endpoint = new AudioEndpoint(deviceId, false))
                {
                    var resampler = new StreamResampler(16000, endpoint.Format.Rate);
                    var buffer = new SampleBuffer(endpoint.Format.Rate / 2);
                    var packet = new float[(int)endpoint.Capacity];
                    bool primed = false;
                    uint task;
                    mmcss = NativeAudio.AvSetMmThreadCharacteristics("Audio", out task);
                    IntPtr pointer;
                    NativeAudio.Check(endpoint.Render.GetBuffer(endpoint.Capacity, out pointer));
                    NativeAudio.Check(endpoint.Render.ReleaseBuffer(endpoint.Capacity, 2));
                    endpoint.Start();
                    Volatile.Write(ref active, 1);
                    while (!stop.IsCancellationRequested)
                    {
                        int signaled = WaitHandle.WaitAny(new WaitHandle[] { endpoint.Ready, stop.Token.WaitHandle }, 2000);
                        if (signaled == 1)
                            break;
                        if (signaled == WaitHandle.WaitTimeout)
                            throw new TimeoutException("Monitor device timed out.");
                        uint free = endpoint.FreeFrames();
                        if (free == 0)
                            continue;
                        int queued = input.Count;
                        if (!primed && queued >= 768)
                            primed = true;
                        if (primed)
                        {
                            if (queued > 0)
                            {
                                var samples = new float[queued];
                                input.Read(samples, queued);
                                double fillMs = buffer.Count * 1000.0 / endpoint.Format.Rate + queued / 16.0;
                                double ratio = 1 + Math.Max(-0.003, Math.Min(0.003, (fillMs - 48) * 0.0001));
                                buffer.WriteLatest(resampler.Push(samples, ratio));
                            }

                            int read = buffer.Read(packet, (int)free);
                            Interlocked.Add(ref rendered, read);
                            if (read < free)
                            {
                                Array.Clear(packet, read, (int)free - read);
                                primed = false;
                            }
                        }
                        else
                            Array.Clear(packet, 0, (int)free);
                        NativeAudio.Check(endpoint.Render.GetBuffer(free, out pointer));
                        try
                        {
                            endpoint.Format.Encode(pointer, packet, (int)free);
                        }
                        finally
                        {
                            NativeAudio.Check(endpoint.Render.ReleaseBuffer(free, 0));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!stop.IsCancellationRequested)
                {
                    error = ex.Message;
                    Diagnostics.Failure("monitor", ex);
                }
            }
            finally
            {
                Volatile.Write(ref active, 0);
                if (mmcss != IntPtr.Zero)
                    NativeAudio.AvRevertMmThreadCharacteristics(mmcss);
            }
        }

        public void Dispose()
        {
            lock (disposeGate)
            {
                if (disposed)
                    return;
                stop.Cancel();
                if (!thread.Join(4000))
                    throw new TimeoutException("Monitor did not stop in time.");
                stop.Dispose();
                disposed = true;
            }
        }
    }
}

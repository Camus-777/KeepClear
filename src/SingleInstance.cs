using System;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace KeepClear
{
    // One GUI per Windows user/session, shared across portable folders and versions.
    internal sealed class SingleInstance : IDisposable
    {
        private readonly Mutex mutex;
        private readonly EventWaitHandle activate;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private Thread listener;
        private WindowsFormsSynchronizationContext dispatcher;
        public bool IsPrimary { get; private set; }

        public SingleInstance() : this("KeepClear." + WindowsIdentity.GetCurrent().User.Value)
        {
        }

        internal SingleInstance(string key)
        {
            activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + key + ".activate");
            mutex = new Mutex(false, "Local\\" + key + ".instance");
            try
            {
                IsPrimary = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                IsPrimary = true;
            }
        }

        public void SignalPrimary()
        {
            activate.Set();
        }

        public void Listen(Form form, Action restore)
        {
            var handle = form.Handle; // Activation may arrive before the message loop starts.
            Listen(restore);
        }

        public void Listen(Action restore)
        {
            if (!IsPrimary)
                throw new InvalidOperationException("Only the primary instance can listen.");
            dispatcher = SynchronizationContext.Current as WindowsFormsSynchronizationContext ?? new WindowsFormsSynchronizationContext();
            listener = new Thread(delegate ()
            {
                while (WaitHandle.WaitAny(new WaitHandle[] { stop, activate }) == 1)
                {
                    try
                    {
                        dispatcher.Post(delegate (object ignored)
                        {
                            restore();
                        }, null);
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "KeepClear activation"
            };
            listener.Start();
        }

        public void Dispose()
        {
            stop.Set();
            if (listener != null)
                listener.Join(1000);
            // WinForms owns the UI thread's shared marshalling control/context.
            dispatcher = null;
            if (IsPrimary)
                mutex.ReleaseMutex();
            mutex.Dispose();
            activate.Dispose();
            stop.Dispose();
        }
    }
}

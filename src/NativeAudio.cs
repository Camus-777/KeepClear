using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeepQuiet
{
    public sealed class AudioDevice
    {
        public string Id, Name;
        public bool Capture;
        internal bool IsDefault;
        internal uint State;
        internal DriverNode Driver;
        internal string AdapterId;
        internal string IdentityError;
        public bool IsCable
        {
            get
            {
                return Driver != null && Driver.IsBaseCable && Driver.ServiceMatches;
            }
        }

        public override string ToString()
        {
            return Name;
        }
    }

    // Windows SDK COM contracts. No external audio library is used.
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class DeviceEnumerator
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int flow, uint mask, out IMMDeviceCollection devices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);
        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig]
        int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig]
        int GetState(out uint state);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Type;
        [FieldOffset(8)]
        public IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);
        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);
        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig]
        int Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int mode, uint flags, long duration, long period, IntPtr format, IntPtr session);
        [PreserveSig]
        int GetBufferSize(out uint frames);
        [PreserveSig]
        int GetStreamLatency(out long latency);
        [PreserveSig]
        int GetCurrentPadding(out uint frames);
        [PreserveSig]
        int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
        [PreserveSig]
        int GetMixFormat(out IntPtr format);
        [PreserveSig]
        int GetDevicePeriod(out long normal, out long minimum);
        [PreserveSig]
        int Start();
        [PreserveSig]
        int Stop();
        [PreserveSig]
        int Reset();
        [PreserveSig]
        int SetEventHandle(IntPtr handle);
        [PreserveSig]
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong clockPosition);
        [PreserveSig]
        int ReleaseBuffer(uint frames);
        [PreserveSig]
        int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig]
        int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig]
        int ReleaseBuffer(uint frames, uint flags);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeviceTopology
    {
        [PreserveSig]
        int GetConnectorCount(out uint count);
        [PreserveSig]
        int GetConnector(uint index, out IConnector connector);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IConnector
    {
        [PreserveSig]
        int GetType(out int type);
        [PreserveSig]
        int GetDataFlow(out int flow);
        [PreserveSig]
        int ConnectTo(IConnector other);
        [PreserveSig]
        int Disconnect();
        [PreserveSig]
        int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig]
        int GetConnectedTo(out IConnector connector);
        [PreserveSig]
        int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig]
        int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig]
        int GetChannelCount(out uint channels);
        [PreserveSig]
        int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig]
        int GetMasterVolumeLevel(out float level);
        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    internal static class NativeAudio
    {
        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);
        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr AvSetMmThreadCharacteristics(string task, out uint index);
        [DllImport("avrt.dll")]
        internal static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
        public static void Check(int hr)
        {
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
        }

        public static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value))
                Marshal.ReleaseComObject(value);
        }

        private static DriverNode Adapter(IMMDevice device, out string instance)
        {
            instance = null;
            object topology = null;
            IConnector connector = null;
            IMMDeviceEnumerator enumerator = null;
            IMMDevice adapter = null;
            IPropertyStore properties = null;
            try
            {
                // Follow Windows' topology to the adapter; no editable label or container-ID guessing.
                Guid iid = typeof(IDeviceTopology).GUID;
                Check(device.Activate(ref iid, 23, IntPtr.Zero, out topology));
                Check(((IDeviceTopology)topology).GetConnector(0, out connector));
                string id;
                Check(connector.GetDeviceIdConnectedTo(out id));
                enumerator = (IMMDeviceEnumerator)new DeviceEnumerator();
                Check(enumerator.GetDevice(id, out adapter));
                Check(adapter.OpenPropertyStore(0, out properties));
                instance = TextProperty(properties, new PropertyKey { Format = new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), Id = 256 });
                if (String.IsNullOrEmpty(instance))
                    throw new InvalidOperationException("Audio adapter instance metadata is unavailable.");
                return DriverCatalog.Read(instance);
            }
            finally
            {
                Release(properties);
                Release(adapter);
                Release(enumerator);
                Release(connector);
                Release(topology);
            }
        }

        internal static bool? Muted(string id)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            object volume = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new DeviceEnumerator();
                Check(enumerator.GetDevice(id, out device));
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                Check(device.Activate(ref iid, 23, IntPtr.Zero, out volume));
                bool mute;
                Check(((IAudioEndpointVolume)volume).GetMute(out mute));
                return mute;
            }
            catch (Exception ex)
            {
                Diagnostics.Failure("mute-query", ex);
                return null;
            }
            finally
            {
                Release(volume);
                Release(device);
                Release(enumerator);
            }
        }

        private static string TextProperty(IPropertyStore store, PropertyKey key)
        {
            PropVariant value;
            Check(store.GetValue(ref key, out value));
            try
            {
                return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : "";
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }

        public static List<AudioDevice> Devices(bool capture)
        {
            return Devices(capture, 1);
        }

        internal static List<AudioDevice> Devices(bool capture, uint stateMask)
        {
            var enumerator = (IMMDeviceEnumerator)new DeviceEnumerator();
            IMMDeviceCollection collection = null;
            try
            {
                string defaultId = null;
                IMMDevice defaultDevice = null;
                try
                {
                    if (enumerator.GetDefaultAudioEndpoint(capture ? 1 : 0, 1, out defaultDevice) >= 0)
                        Check(defaultDevice.GetId(out defaultId));
                }
                catch (Exception ex)
                {
                    Diagnostics.Failure("default-endpoint", ex);
                }
                finally
                {
                    Release(defaultDevice);
                }

                Check(enumerator.EnumAudioEndpoints(capture ? 1 : 0, stateMask, out collection));
                uint count;
                Check(collection.GetCount(out count));
                return CollectEndpoints(count, i =>
                {
                    IMMDevice device = null;
                    IPropertyStore store = null;
                    try
                    {
                        Check(collection.Item(i, out device));
                        string id;
                        Check(device.GetId(out id));
                        var entry = new AudioDevice
                        {
                            Id = id,
                            Name = id,
                            Capture = capture,
                            IsDefault = id == defaultId
                        };
                        try
                        {
                            entry.Driver = Adapter(device, out entry.AdapterId);
                        }
                        catch (Exception ex)
                        {
                            entry.IdentityError = ex.Message;
                            Diagnostics.Failure("endpoint-identity " + id, ex);
                        }

                        try
                        {
                            uint state;
                            Check(device.GetState(out state));
                            entry.State = state;
                        }
                        catch (Exception ex)
                        {
                            entry.IdentityError = (entry.IdentityError ?? "") + " " + ex.Message;
                            Diagnostics.Failure("endpoint-state " + id, ex);
                        }

                        // Display metadata is optional and cannot invalidate a verified driver identity.
                        try
                        {
                            Check(device.OpenPropertyStore(0, out store));
                            string name = TextProperty(store, new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 });
                            if (!String.IsNullOrEmpty(name))
                                entry.Name = name;
                        }
                        catch (Exception ex)
                        {
                            Diagnostics.Failure("endpoint-name " + id, ex);
                        }

                        return entry;
                    }
                    finally
                    {
                        Release(store);
                        Release(device);
                    }
                });
            }
            finally
            {
                Release(collection);
                Release(enumerator);
            }
        }

        internal static List<AudioDevice> CollectEndpoints(uint count, Func<uint, AudioDevice> read)
        {
            var result = new List<AudioDevice>();
            for (uint i = 0; i < count; i++)
                try
                {
                    result.Add(read(i));
                }
                catch (Exception ex)
                {
                    Diagnostics.Failure("endpoint-read " + i, ex);
                }

            return result;
        }
    }

    internal sealed class MixFormat
    {
        public int Rate, Channels, Bits, Align, ValidBits;
        public bool Float;
        public static MixFormat Read(IntPtr pointer)
        {
            int tag = (ushort)Marshal.ReadInt16(pointer, 0);
            var format = new MixFormat
            {
                Channels = (ushort)Marshal.ReadInt16(pointer, 2),
                Rate = Marshal.ReadInt32(pointer, 4),
                Align = (ushort)Marshal.ReadInt16(pointer, 12),
                Bits = (ushort)Marshal.ReadInt16(pointer, 14)
            };
            format.ValidBits = format.Bits;
            if (tag == 0xFFFE)
            {
                if ((ushort)Marshal.ReadInt16(pointer, 16) < 22)
                    throw new NotSupportedException("设备格式扩展不完整。");
                format.ValidBits = (ushort)Marshal.ReadInt16(pointer, 18);
                var subtype = (Guid)Marshal.PtrToStructure(IntPtr.Add(pointer, 24), typeof(Guid));
                if (subtype == new Guid("00000003-0000-0010-8000-00AA00389B71"))
                    tag = 3;
                else if (subtype == new Guid("00000001-0000-0010-8000-00AA00389B71"))
                    tag = 1;
                else
                    throw new NotSupportedException("设备使用不支持的音频子格式。");
            }

            format.Float = tag == 3;
            if ((tag != 1 && tag != 3) || (format.Float && format.Bits != 32) || (!format.Float && format.Bits != 16 && format.Bits != 24 && format.Bits != 32) || format.Channels < 1 || format.Channels > 32 || format.Rate < 8000 || format.Rate > 192000 || format.Align != format.Channels * format.Bits / 8 || format.ValidBits < 1 || format.ValidBits > format.Bits)
                throw new NotSupportedException("设备格式不支持；请在 Windows 声音设置选择 16/24/32 位 PCM 或 32 位浮点格式。");
            return format;
        }

        public unsafe float[] Decode(IntPtr pointer, int frames, bool silent, bool strict = false)
        {
            var mono = new float[frames];
            if (silent)
                return mono;
            byte* p = (byte*)pointer;
            for (int f = 0; f < frames; f++)
            {
                double sum = 0;
                for (int c = 0; c < Channels; c++)
                {
                    byte* s = p + f * Align + c * Bits / 8;
                    float v = Float ? *(float*)s : Bits == 16 ? *(short*)s / 32768f : Bits == 24 ? ((s[0] | s[1] << 8 | s[2] << 16) << 8 >> 8) / 8388608f : *(int*)s / 2147483648f;
                    if (strict && (float.IsNaN(v) || float.IsInfinity(v)))
                        throw new InvalidOperationException("Device returned non-finite audio samples.");
                    sum += float.IsNaN(v) || float.IsInfinity(v) ? 0 : v;
                }

                mono[f] = (float)(sum / Channels);
            }

            return mono;
        }

        public unsafe void Encode(IntPtr pointer, float[] mono, int frames)
        {
            byte* p = (byte*)pointer;
            for (int f = 0; f < frames; f++)
            {
                float v = Math.Max(-0.999f, Math.Min(0.999f, mono[f]));
                for (int c = 0; c < Channels; c++)
                {
                    byte* s = p + f * Align + c * Bits / 8;
                    if (Float)
                        *(float*)s = v;
                    else if (Bits == 16)
                        *(short*)s = (short)Math.Round(v * 32767);
                    else if (Bits == 24)
                    {
                        int n = (int)Math.Round(v * 8388607);
                        s[0] = (byte)n;
                        s[1] = (byte)(n >> 8);
                        s[2] = (byte)(n >> 16);
                    }
                    else
                        *(int*)s = (int)Math.Round(v * 2147483647.0);
                }
            }
        }
    }

    // Construct, use and dispose each endpoint on its owning audio thread.
    internal sealed class AudioEndpoint : IDisposable
    {
        private IAudioClient client;
        private object service;
        public readonly AutoResetEvent Ready = new AutoResetEvent(false);
        public MixFormat Format;
        public uint Capacity;
        public IAudioCaptureClient Capture
        {
            get
            {
                return (IAudioCaptureClient)service;
            }
        }

        public IAudioRenderClient Render
        {
            get
            {
                return (IAudioRenderClient)service;
            }
        }

        public AudioEndpoint(string id, bool capture)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IntPtr format = IntPtr.Zero;
            try
            {
                enumerator = (IMMDeviceEnumerator)new DeviceEnumerator();
                NativeAudio.Check(enumerator.GetDevice(id, out device));
                Guid iid = typeof(IAudioClient).GUID;
                object obj;
                NativeAudio.Check(device.Activate(ref iid, 23, IntPtr.Zero, out obj));
                client = (IAudioClient)obj;
                NativeAudio.Check(client.GetMixFormat(out format));
                Format = MixFormat.Read(format);
                NativeAudio.Check(client.Initialize(0, 0x40000, 300000, 0, format, IntPtr.Zero));
                NativeAudio.Check(client.GetBufferSize(out Capacity));
                NativeAudio.Check(client.SetEventHandle(Ready.SafeWaitHandle.DangerousGetHandle()));
                Guid sid = capture ? typeof(IAudioCaptureClient).GUID : typeof(IAudioRenderClient).GUID;
                NativeAudio.Check(client.GetService(ref sid, out service));
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                if (format != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(format);
                NativeAudio.Release(device);
                NativeAudio.Release(enumerator);
            }
        }

        public void Start()
        {
            NativeAudio.Check(client.Start());
        }

        public uint FreeFrames()
        {
            uint padding;
            NativeAudio.Check(client.GetCurrentPadding(out padding));
            return Capacity - padding;
        }

        public void Dispose()
        {
            if (client != null)
            {
                client.Stop();
            }

            NativeAudio.Release(service);
            service = null;
            NativeAudio.Release(client);
            client = null;
            Ready.Dispose();
        }
    }
}

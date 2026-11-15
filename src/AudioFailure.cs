using System;

namespace KeepClear
{
    internal enum AudioFailureKind
    {
        None,
        Permission,
        Busy,
        Disconnected,
        NoMicrophone,
        ModelLoad,
        ModelProcess,
        Slow,
        InputTimeout,
        OutputTimeout,
        Readback,
        Format,
        Timeout,
        Unknown
    }

    internal sealed class AudioFailureException : Exception
    {
        internal readonly AudioFailureKind Kind;
        internal AudioFailureException(AudioFailureKind kind, string detail, Exception inner = null) : base(detail, inner)
        {
            Kind = kind;
            if (inner != null)
                HResult = inner.HResult;
        }
    }

    internal static class AudioFailure
    {
        internal static AudioFailureKind Classify(string stage, Exception error)
        {
            for (Exception cause = error; cause != null; cause = cause.InnerException)
            {
                var typed = cause as AudioFailureException;
                if (typed != null)
                    return typed.Kind;
            }

            if (stage == "model-load")
                return AudioFailureKind.ModelLoad;
            for (Exception cause = error; cause != null; cause = cause.InnerException)
            {
                uint code = unchecked((uint)cause.HResult);
                if (code == 0x80070005)
                    return AudioFailureKind.Permission;
                if (code == 0x8889000A)
                    return AudioFailureKind.Busy;
                if (code == 0x88890008)
                    return AudioFailureKind.Format;
                if (code == 0x88890004 || code == 0x80070490 || code == 0x8007048F)
                    return AudioFailureKind.Disconnected;
            }

            if (stage == "model-process")
                return AudioFailureKind.ModelProcess;
            if (error is NotSupportedException)
                return AudioFailureKind.Format;
            if (stage == "cable-readback")
                return AudioFailureKind.Readback;
            if (error is TimeoutException)
                return AudioFailureKind.Timeout;
            return AudioFailureKind.Unknown;
        }

        internal static string Title(AudioFailureKind kind, bool english)
        {
            switch (kind)
            {
                case AudioFailureKind.Permission:
                    return english ? "Microphone access denied" : "麦克风访问权限被拒绝";
                case AudioFailureKind.Busy:
                    return english ? "Audio device is in use" : "音频设备被其他程序占用";
                case AudioFailureKind.Disconnected:
                    return english ? "Audio device disconnected" : "音频设备已断开或失效";
                case AudioFailureKind.NoMicrophone:
                    return english ? "Selected microphone is unavailable" : "所选麦克风不可用";
                case AudioFailureKind.ModelLoad:
                    return english ? "Local models could not load" : "本地模型加载失败";
                case AudioFailureKind.ModelProcess:
                    return english ? "Model processing failed" : "模型处理失败";
                case AudioFailureKind.Slow:
                    return english ? "Processing cannot keep up" : "处理速度或音频缓冲不稳定";
                case AudioFailureKind.InputTimeout:
                    return english ? "Microphone is not returning audio" : "麦克风没有返回音频";
                case AudioFailureKind.OutputTimeout:
                    return english ? "Virtual output is not responding" : "虚拟声卡输出没有响应";
                case AudioFailureKind.Readback:
                    return english ? "VB-CABLE readback failed" : "虚拟声卡回读失败";
                case AudioFailureKind.Format:
                    return english ? "Audio format is unsupported" : "音频设备格式不支持";
                case AudioFailureKind.Timeout:
                    return english ? "Audio test timed out" : "音频链路检查超时";
                default:
                    return english ? "Audio stream test failed" : "音频链路自检未通过";
            }
        }

        internal static string Advice(AudioFailureKind kind, bool english)
        {
            switch (kind)
            {
                case AudioFailureKind.Permission:
                    return english ? "Allow microphone access for desktop apps in Windows Settings." : "请在 Windows 设置中允许桌面应用访问麦克风。";
                case AudioFailureKind.Busy:
                    return english ? "Close apps using exclusive audio, then retry." : "请关闭独占音频设备的程序，再重新检查。";
                case AudioFailureKind.Disconnected:
                case AudioFailureKind.NoMicrophone:
                    return english ? "Reconnect or enable the device, then select it again." : "请连接或启用设备，再重新选择。";
                case AudioFailureKind.ModelLoad:
                    return english ? "Extract the complete package again; check runtime and model files." : "请重新解压完整运行包，检查运行组件和模型文件。";
                case AudioFailureKind.ModelProcess:
                    return english ? "Retry; if it fails again, open the diagnostic log." : "请重新检查；再次失败时查看诊断日志。";
                case AudioFailureKind.Slow:
                    return english ? "Reduce CPU load, then retry the audio test." : "请减少 CPU 负载后重新检查音频链路。";
                case AudioFailureKind.InputTimeout:
                    return english ? "Check the microphone connection, permissions and audio driver." : "请检查麦克风连接、访问权限和声卡驱动。";
                case AudioFailureKind.OutputTimeout:
                case AudioFailureKind.Readback:
                    return english ? "Check VB-CABLE is enabled and available, then retry." : "请确认 VB-CABLE 已启用且可用，再重新检查。";
                case AudioFailureKind.Format:
                    return english ? "Select a supported PCM format in Windows sound settings." : "请在 Windows 声音设置中选择受支持的 PCM 格式。";
                default:
                    return english ? "Retry and check the diagnostic logs for details." : "请重新检查，并查看诊断日志获取详情。";
            }
        }
    }
}

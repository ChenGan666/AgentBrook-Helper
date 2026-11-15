using System.Runtime.InteropServices;

namespace AgentBrook.Plugin.Tts.Audio;

/// <summary>
/// macOS 播放端：AVAudioPlayer ObjC interop（libobjc + AVFoundation，MicRecorder 同款手法）。
/// S0-5 spike 实证（2026-11-15，darwin 27）：AVAudioPlayer 已无 initWithContentsOfFile:error:，
/// 路径初始化器只剩 initWithContentsOfURL:error:；播放状态选择器为 isPlaying（非 playing）。
/// 音量 = setVolume:（单路径增益）；完成判定 = 轮询 isPlaying（免 ObjC delegate 注册）。
/// </summary>
public sealed class MacAudioPlayer : IAudioPlayer
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibAvFoundation = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";

    [DllImport(LibObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(LibObjC)] private static extern IntPtr sel_registerName(string sel);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr self, IntPtr sel);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr self, IntPtr sel, IntPtr a);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr self, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern byte MsgSendBool(IntPtr self, IntPtr sel);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern void MsgSendVoid(IntPtr self, IntPtr sel);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern void MsgSendVoidFloat(IntPtr self, IntPtr sel, float v);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern double MsgSendDouble(IntPtr self, IntPtr sel);

    private static IntPtr Sel(string name) => sel_registerName(name);

    private IntPtr _player;
    private bool _frameworkLoaded;
    private readonly object _gate = new();
    private volatile bool _disposed;

    /// <summary>惰性加载 AVFoundation（首个 ObjC 调用前必须完成框架注册）。</summary>
    private void EnsureFramework()
    {
        if (_frameworkLoaded) return;
        NativeLibrary.Load(LibAvFoundation);
        _frameworkLoaded = true;
    }

    public Task<(bool Completed, string? ErrorCode)> PlayAsync(string wavPath, float volume, CancellationToken ct)
    {
        if (_disposed) return Task.FromResult<(bool Completed, string? ErrorCode)>((false, "tts.disposed"));

        var utf8 = IntPtr.Zero;
        var errPtr = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(errPtr, IntPtr.Zero);
        try
        {
            lock (_gate)
            {
                EnsureFramework();
            }

            utf8 = Marshal.StringToHGlobalAnsi(wavPath);
            var nsPath = MsgSend(objc_getClass("NSString"), Sel("stringWithUTF8String:"), utf8);
            var fileUrl = MsgSend(objc_getClass("NSURL"), Sel("fileURLWithPath:"), nsPath);

            var alloc = MsgSend(objc_getClass("AVAudioPlayer"), Sel("alloc"));
            _player = MsgSend(alloc, Sel("initWithContentsOfURL:error:"), fileUrl, errPtr);
            if (_player == IntPtr.Zero)
                return Task.FromResult<(bool Completed, string? ErrorCode)>((false, "tts.play.open"));

            MsgSendVoidFloat(_player, Sel("setVolume:"), Math.Clamp(volume, 0f, 1f));
            if (MsgSendBool(_player, Sel("play")) == 0)
                return Task.FromResult<(bool Completed, string? ErrorCode)>((false, "tts.play.start"));

            var duration = MsgSendDouble(_player, Sel("duration"));

            // 轮询完成（免 delegate 注册；duration +5s 兜底防漏事件）
            return Task.Run<(bool, string?)>(async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var cancelled = false;
                while (true)
                {
                    if (ct.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                    if (_disposed)
                    {
                        cancelled = true;
                        break;
                    }
                    await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                    byte playing;
                    lock (_gate)
                    {
                        if (_player == IntPtr.Zero) return (false, "tts.disposed");
                        playing = MsgSendBool(_player, Sel("isPlaying"));
                    }
                    if (playing == 0) break;
                    if (sw.Elapsed.TotalSeconds > duration + 5) break;
                }
                if (cancelled)
                {
                    lock (_gate)
                    {
                        if (_player != IntPtr.Zero) MsgSendVoid(_player, Sel("stop"));
                    }
                    return (false, "tts.cancelled");
                }
                return (true, null);
            }, CancellationToken.None);
        }
        finally
        {
            // 局部临时对象（NSString/NSURL/err）随进程/autorelease 清理；播放器句柄由 Stop/Dispose 管理
            if (errPtr != IntPtr.Zero) Marshal.FreeHGlobal(errPtr);
            if (utf8 != IntPtr.Zero) Marshal.FreeHGlobal(utf8);
        }

    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_player != IntPtr.Zero)
            {
                MsgSendVoid(_player, Sel("stop"));
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_player != IntPtr.Zero)
            {
                MsgSendVoid(_player, Sel("stop"));
                MsgSendVoid(_player, Sel("release"));
                _player = IntPtr.Zero;
            }
        }
    }
}

using System.Runtime.InteropServices;

namespace AgentBrook.Helper.Core.Voice;

/// <summary>麦克风采集异常（启动失败/权限拒绝/设备丢失），Reason 供 UI 归类提示文案。</summary>
public sealed class MicRecorderException : Exception
{
    public string Reason { get; }
    public MicRecorderException(string reason, Exception? inner = null) : base(reason, inner) => Reason = reason;
}

/// <summary>
/// 麦克风采集（doc/Voice/03 §3.2 A1 + 06）：16kHz 单声道 16bit PCM 直出，直配 IAsrCapability 与云端 wav 上传，零转码。
/// 平台实现（仓库惯例：运行时分支 OperatingSystem.IsXxx()，禁 #if）：
/// · macOS = AudioToolbox AudioQueue 纯 P/Invoke（系统框架零打包；AudioQueue 内建硬件格式转码；首次使用触发 TCC 授权）
/// · Windows = NAudio WASAPI（WasapiCapture 混合格式采集 + MediaFoundationResampler 归一 16k/mono/s16）
/// 录音缓冲是用户语音：仅内存、不落盘、不进日志（脱敏红线），Stop/Dispose 后即释放。
/// </summary>
public sealed class MicRecorder
{
    public const int SampleRate = 16000;
    /// <summary>硬上限（秒，03 §2）：60s ≈ 1.92MB 内存。</summary>
    public const int MaxDurationSeconds = 60;
    internal const int BytesPerSecond = SampleRate * 2;
    internal const int MaxBufferBytes = BytesPerSecond * MaxDurationSeconds;

    private IMicCapture? _capture;

    /// <summary>本次采集的实际采样率（Start 成功后有效；转写管线按它封 WAV，AsrService 支持任意采样率）。</summary>
    public int CaptureRate { get; private set; } = SampleRate;

    public bool IsRecording => _capture is not null;

    /// <summary>开始采集。失败抛 MicRecorderException（Reason：platform-unsupported / start-failed 等）。</summary>
    public void Start()
    {
        if (_capture is not null)
        {
            throw new MicRecorderException("already-recording");
        }
        // macOS 后端顺序：AVAudioEngine 优先（本机构建 AudioQueue 输入对第三方进程不可用——Start=0 但零回调，
        // 真机 2026-11-15 排障定案），失败自动落回 AudioQueue 候选链
        Exception? lastError = null;
        if (OperatingSystem.IsMacOS())
        {
            foreach (var factory in new Func<IMicCapture>[] { () => new MacAvAudioEngineCapture(), () => new MacAudioQueueCapture() })
            {
                IMicCapture capture = factory();
                try
                {
                    capture.Start();
                    CaptureRate = capture.SampleRate;
                    _capture = capture;
                    return;
                }
                catch (Exception ex)
                {
                    capture.Dispose();
                    lastError = ex;
                }
            }
            throw lastError is MicRecorderException mre ? mre : new MicRecorderException("start-failed", lastError);
        }
        IMicCapture capture2 = OperatingSystem.IsWindows()
            ? new WindowsWasapiCapture()
            : throw new MicRecorderException("platform-unsupported");
        try
        {
            capture2.Start();
        }
        catch (MicRecorderException)
        {
            capture2.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            capture2.Dispose();
            throw new MicRecorderException("start-failed", ex);
        }
        CaptureRate = capture2.SampleRate;
        _capture = capture2;
    }

    /// <summary>停止并取回 PCM16（单声道，采样率=CaptureRate）原始字节；未在录音返回空数组。本次缓冲在返回后即弃。</summary>
    public byte[] Stop()
    {
        var capture = Interlocked.Exchange(ref _capture, null);
        if (capture is null) return [];
        try
        {
            return capture.Stop();
        }
        finally
        {
            capture.Dispose();
        }
    }

    /// <summary>最近一块数据的归一化电平（0..1；未在录音为 0）——气泡波形指示数据源。</summary>
    public double Level => _capture?.Level ?? 0.0;
}

/// <summary>平台采集器统一面：Start 成功后 Stop 返回数据（异常仅代表采集失败）。</summary>
internal interface IMicCapture : IDisposable
{
    /// <summary>实际采集采样率（Start 成功后有效）。</summary>
    int SampleRate { get; }

    void Start();

    /// <summary>停止并取回 PCM16 mono 字节（采样率=SampleRate）。</summary>
    byte[] Stop();

    /// <summary>最近一块数据的归一化电平（0..1，气泡波形指示用；无数据为 0）。</summary>
    double Level { get; }
}

/// <summary>
/// macOS AudioQueue 采集（AudioToolbox 系统框架，纯 P/Invoke）。
/// 指定 16k/mono/s16 目标格式，AudioQueue 经内建转换器从硬件格式转码（真机若不配合，
/// 回退方案：采 48k + AudioFrontend.Resample，见 doc/Voice/06 §3）。
/// </summary>
internal sealed class MacAudioQueueCapture : IMicCapture
{
    private const string AudioToolboxLib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreAudioLib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const uint FormatLinearPcm = 0x6C70636D;      // 'lpcm'
    private const uint FlagsSignedIntegerPacked = 4 | 8;  // kLinearPCMFormatFlagIsSignedInteger | kLinearPCMFormatFlagIsPacked
    private const int BufferCount = 4;
    // 4KB = 128ms@16k mono s16：回调快速产出首包（32KB=1s 的旧值下，短按 <1s 一包都攒不满 → 0 字节误报"太短"）
    private const int BufferBytes = 4 * 1024;
    // AudioQueueBuffer 64-bit 布局：0 userData / 8 capacity(+pad) / 16 audioData / 24 byteSize(+pad) / 32 userData2 / 40 packetDescCount
    private const int OffAudioData = 16;
    private const int OffAudioDataByteSize = 24;

    // 委托须保持引用防 GC 回收 native 回调段
    private delegate void AudioQueueInputCallback(IntPtr userData, IntPtr queue, IntPtr buffer, IntPtr timeStamp, uint packetDescCount, IntPtr packetDescs);
    private AudioQueueInputCallback? _callback;

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatID;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [DllImport(AudioToolboxLib)]
    private static extern int AudioQueueNewInput(ref AudioStreamBasicDescription format,
        AudioQueueInputCallback callback, IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);
    [DllImport(AudioToolboxLib)]
    private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);
    [DllImport(AudioToolboxLib)]
    private static extern int AudioQueueStop(IntPtr queue, [MarshalAs(UnmanagedType.I1)] bool immediate);
    [DllImport(AudioToolboxLib)]
    private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint size, out IntPtr buffer);
    [DllImport(AudioToolboxLib)]
    private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packetDescCount, IntPtr packetDescs);
    [DllImport(AudioToolboxLib)]
    private static extern int AudioQueueFreeBuffer(IntPtr queue, IntPtr buffer);
    [DllImport(AudioToolboxLib)]
    private static extern int AudioQueueDispose(IntPtr queue, [MarshalAs(UnmanagedType.I1)] bool disposeNow);

    private IntPtr _queue;
    private readonly IntPtr[] _buffers = new IntPtr[BufferCount];
    private readonly MemoryStream _pcm = new();
    private readonly object _gate = new();
    private Exception? _error;
    private long _callbackCount;   // 诊断：回调次数/收到字节数（只记尺寸，不落音频内容）
    private long _bytesReceived;
    private double _lastLevel;     // 最近块 RMS 电平（0..1，波形指示用）
    private int _rate;             // 实际请求的采集格式（首包看门狗确认后生效）
    private int _channels;
    private readonly object _startGate = new();   // Start 候选尝试串行化（看门狗轮询与回调并发）

    /// <summary>实际采集采样率（Start 成功后有效）。</summary>
    public int SampleRate => _rate;

    public void Start()
    {
        // 根因（真机 2026-11-15）：AudioQueue 输入对采样率转换不可用——44.1kHz 硬件上请求 16k/48k
        // 均 Start=0 但零回调。故按设备当前格式优先，逐候选尝试直到首包到达（首包看门狗）。
        var (deviceRate, deviceCh) = ProbeDefaultInputFormat();
        var candidates = new List<(int Rate, int Ch)>();
        void Add(int rate, int ch)
        {
            if (rate is > 8000 and < 192000 && ch is 1 or 2 && !candidates.Contains((rate, ch)))
            {
                candidates.Add((rate, ch));
            }
        }
        Add(deviceRate, deviceCh is 1 or 2 ? deviceCh : 1);
        Add(deviceRate, 1);
        Add(44100, 1);
        Add(48000, 1);
        Add(16000, 1);

        var errors = new List<string>();
        foreach (var (rate, ch) in candidates)
        {
            try
            {
                StartWithFormat(rate, ch);
                return;   // 首包已到，该格式可用
            }
            catch (Exception ex)
            {
                errors.Add($"{rate}Hz/ch{ch}:{ex.Message}");
                Cleanup();
            }
        }
        // 全候选零回调：Start 均返回 0 但不回调——典型为权限/TCC 归属层静默拦截
        throw new MicRecorderException("no-callbacks:[" + string.Join("; ", errors) + "]");
    }

    private void StartWithFormat(int rate, int ch)
    {
        _rate = rate;
        _channels = ch;
        var bytesPerFrame = 2 * ch;
        var asbd = new AudioStreamBasicDescription
        {
            SampleRate = rate,
            FormatID = FormatLinearPcm,
            FormatFlags = FlagsSignedIntegerPacked,
            BytesPerPacket = (uint)bytesPerFrame,
            FramesPerPacket = 1,
            BytesPerFrame = (uint)bytesPerFrame,
            ChannelsPerFrame = (uint)ch,
            BitsPerChannel = 16,
        };
        _callback = OnInput;
        if (AudioQueueNewInput(ref asbd, _callback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out _queue) != 0
            || _queue == IntPtr.Zero)
        {
            throw new MicRecorderException("queue-create-failed");
        }
        for (var i = 0; i < BufferCount; i++)
        {
            if (AudioQueueAllocateBuffer(_queue, BufferBytes, out _buffers[i]) != 0)
            {
                throw new MicRecorderException("buffer-allocate-failed");
            }
            Marshal.WriteInt32(_buffers[i], OffAudioDataByteSize, 0);
            AudioQueueEnqueueBuffer(_queue, _buffers[i], 0, IntPtr.Zero);
        }
        // 首次使用在此触发 TCC 麦克风授权弹窗（Info.plist 已含 NSMicrophoneUsageDescription）；
        // 被拒/无设备时返回非 0（kAudioQueueErr_CannotStart 等），由 UI 归类提示
        var status = AudioQueueStart(_queue, IntPtr.Zero);
        if (status != 0)
        {
            throw new MicRecorderException($"start-failed:status={status}");
        }
        // 首包看门狗：AudioQueue 不回调时 Start 仍返回 0——700ms 内无首包判定该格式不可用
        for (var waited = 0; waited < 700; waited += 50)
        {
            Thread.Sleep(50);
            lock (_gate)
            {
                if (_bytesReceived > 0)
                {
                    return;
                }
            }
        }
        throw new MicRecorderException("no-first-callback");
    }

    /// <summary>默认输入设备的当前采样率/声道数（CoreAudio；探测失败回 (0,0)，候选列表自行兜底）。</summary>
    private static (int Rate, int Ch) ProbeDefaultInputFormat()
    {
        try
        {
            var device = AudioObjectGetUInt32Property(1 /*kAudioObjectSystemObject*/, 0x64496E20 /*'dIn '*/);
            if (device == 0)
            {
                return (0, 0);
            }
            var rate = AudioObjectGetFloat64Property(device, 0x6E737220 /*'nsr '*/);
            var asbd = AudioObjectGetAsbdProperty(device, 0x73746D66 /*'stmf'*/);
            return ((int)rate, (int)asbd.ChannelsPerFrame);
        }
        catch
        {
            return (0, 0);
        }
    }

    // ── CoreAudio 硬件层探测（仅查询；带状态检查） ─────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioObjectPropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    [DllImport(CoreAudioLib)]
    private static extern int AudioObjectGetPropertyData(uint id, ref AudioObjectPropertyAddress addr,
        uint qualifierSize, IntPtr qualifier, ref uint size, IntPtr data);

    private static uint AudioObjectGetUInt32Property(uint objectId, uint selector)
    {
        var addr = new AudioObjectPropertyAddress { Selector = selector, Scope = 0x676C6F62 /*'glob'*/, Element = 0 };
        var ptr = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            uint size = sizeof(uint);
            var status = AudioObjectGetPropertyData(objectId, ref addr, 0, IntPtr.Zero, ref size, ptr);
            if (status != 0)
            {
                throw new MicRecorderException($"coreaudio:{selector:x8}:status={status}");
            }
            return (uint)Marshal.ReadInt32(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static double AudioObjectGetFloat64Property(uint objectId, uint selector)
    {
        var addr = new AudioObjectPropertyAddress { Selector = selector, Scope = 0x676C6F62 /*'glob'*/, Element = 0 };
        var ptr = Marshal.AllocHGlobal(sizeof(double));
        try
        {
            uint size = sizeof(double);
            var status = AudioObjectGetPropertyData(objectId, ref addr, 0, IntPtr.Zero, ref size, ptr);
            if (status != 0)
            {
                throw new MicRecorderException($"coreaudio:{selector:x8}:status={status}");
            }
            return Marshal.ReadInt64(ptr) == 0 ? 0 : BitConverter.Int64BitsToDouble(Marshal.ReadInt64(ptr));
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static AudioStreamBasicDescription AudioObjectGetAsbdProperty(uint objectId, uint selector)
    {
        var addr = new AudioObjectPropertyAddress { Selector = selector, Scope = 0x676C6F62 /*'glob'*/, Element = 0 };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioStreamBasicDescription>());
        try
        {
            uint size = (uint)Marshal.SizeOf<AudioStreamBasicDescription>();
            var status = AudioObjectGetPropertyData(objectId, ref addr, 0, IntPtr.Zero, ref size, ptr);
            if (status != 0)
            {
                throw new MicRecorderException($"coreaudio:{selector:x8}:status={status}");
            }
            return Marshal.PtrToStructure<AudioStreamBasicDescription>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>清理队列与缓冲（候选格式切换时复用）。</summary>
    private void Cleanup()
    {
        foreach (var b in _buffers)
        {
            if (b != IntPtr.Zero && _queue != IntPtr.Zero)
            {
                AudioQueueFreeBuffer(_queue, b);
            }
        }
        Array.Clear(_buffers);
        if (_queue != IntPtr.Zero)
        {
            AudioQueueDispose(_queue, true);
            _queue = IntPtr.Zero;
        }
        _callback = null;
        lock (_gate)
        {
            _pcm.SetLength(0);
            _bytesReceived = 0;
            _callbackCount = 0;
            _error = null;
            _lastLevel = 0;
        }
    }

    private void OnInput(IntPtr userData, IntPtr queue, IntPtr buffer, IntPtr timeStamp, uint packetDescCount, IntPtr packetDescs)
    {
        try
        {
            var size = Marshal.ReadInt32(buffer, OffAudioDataByteSize);
            if (size > 0)
            {
                var src = Marshal.ReadIntPtr(buffer, OffAudioData);
                var chunk = new byte[size];
                Marshal.Copy(src, chunk, 0, size);
                lock (_gate)
                {
                    var cap = _rate * 2 * Math.Max(1, _channels) * MicRecorder.MaxDurationSeconds;
                    var room = cap - _pcm.Length;
                    var toWrite = (int)Math.Min(room, size);
                    if (toWrite > 0)
                    {
                        _pcm.Write(chunk, 0, toWrite);
                        _bytesReceived += toWrite;
                    }
                    _callbackCount++;
                    _lastLevel = ComputeLevel(chunk, size);
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate) { _error ??= ex; }
        }
        finally
        {
            Marshal.WriteInt32(buffer, OffAudioDataByteSize, 0);
            AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
        }
    }

    public byte[] Stop()
    {
        if (_queue != IntPtr.Zero)
        {
            AudioQueueStop(_queue, true);
        }
        lock (_gate)
        {
            if (_error is not null)
            {
                throw new MicRecorderException("capture-error", _error);
            }
            if (_bytesReceived == 0)
            {
                // 区分"回调一次都没进"（权限/设备层没数据）与"进了但零字节"（格式转换层异常）
                throw new MicRecorderException(_callbackCount == 0
                    ? "no-data:no-callbacks"
                    : $"no-data:callbacks={_callbackCount}");
            }
            var pcm = _pcm.ToArray();
            return _channels == 2 ? DownmixStereoToMono(pcm) : pcm;
        }
    }

    /// <summary>交错双声道 → 单声道（均值；internal 供单测）。</summary>
    internal static byte[] DownmixStereoToMono(byte[] pcm)
    {
        var frames = pcm.Length / 4;
        var mono = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var l = BitConverter.ToInt16(pcm, i * 4);
            var r = BitConverter.ToInt16(pcm, i * 4 + 2);
            BitConverter.GetBytes((short)Math.Clamp((l + r) / 2, short.MinValue, short.MaxValue))
                .CopyTo(mono, i * 2);
        }
        return mono;
    }

    /// <summary>最近块 RMS → 归一化电平（×4 感知增益；正常语音 RMS≈0.05-0.3，静音≈0）。</summary>
    private static double ComputeLevel(byte[] chunk, int size)
    {
        var count = size / 2;
        if (count == 0) return 0;
        double sumSq = 0;
        for (int i = 0; i + 1 < size; i += 2)
        {
            var v = BitConverter.ToInt16(chunk, i) / 32768.0;
            sumSq += v * v;
        }
        return Math.Clamp(Math.Sqrt(sumSq / count) * 4.0, 0.0, 1.0);
    }

    public double Level
    {
        get { lock (_gate) return _lastLevel; }
    }

    public void Dispose()
    {
        foreach (var b in _buffers)
        {
            if (b != IntPtr.Zero && _queue != IntPtr.Zero)
            {
                AudioQueueFreeBuffer(_queue, b);
            }
        }
        Array.Clear(_buffers);
        if (_queue != IntPtr.Zero)
        {
            AudioQueueDispose(_queue, true);
            _queue = IntPtr.Zero;
        }
        _callback = null;
        lock (_gate) { _pcm.SetLength(0); }   // 用户语音即弃（脱敏红线）
    }
}

/// <summary>
/// macOS AVAudioEngine 采集（AVFoundation 系统框架，ObjC interop）。
/// 为什么不用 AudioQueue：本机 macOS 构建上 AudioQueue 输入对第三方进程不可用（Start=0 但零回调，
/// 显式绑设备报错、设备属性查询返回垃圾——真机 2026-11-15 排障定案，doc/Voice/06）；AVFoundation 路径
/// （微信/ffmpeg 同款）正常。tap 回调内存直读，不落盘（脱敏红线）。
/// 未授权时系统投递全零音频（静音），由 VoiceInput.IsSilence 归类提示。
/// </summary>
internal sealed class MacAvAudioEngineCapture : IMicCapture
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibAvFoundation = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";

    [DllImport(LibObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(LibObjC)] private static extern IntPtr sel_registerName(string sel);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr self, IntPtr sel);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr self, IntPtr sel, ulong a);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr self, IntPtr sel, ulong a, ulong b, IntPtr c, IntPtr d);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr self, IntPtr sel, IntPtr a);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern byte MsgSendBoolErr(IntPtr self, IntPtr sel, IntPtr err);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern uint MsgSendU32(IntPtr self, IntPtr sel);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern double MsgSendDouble(IntPtr self, IntPtr sel);

    private delegate void TapCallback(IntPtr block, IntPtr pcmBuffer, IntPtr time);

    private IntPtr _engine, _inputNode, _block, _desc, _err;
    private TapCallback? _cb;
    private GCHandle _cbHandle;
    private readonly MemoryStream _pcm = new();
    private readonly object _gate = new();
    private int _sampleRate = 48000;   // 输入节点原生率（启动时从 AVAudioFormat 读，读不到按 48000）
    private long _bytesReceived;

    public int SampleRate => _sampleRate;

    private static IntPtr Sel(string name) => sel_registerName(name);

    public void Start()
    {
        // 先加载框架（否则 ObjC 类未注册）
        NativeLibrary.Load(LibAvFoundation);

        var engineCls = objc_getClass("AVAudioEngine");
        if (engineCls == IntPtr.Zero) throw new MicRecorderException("av:class-not-found");
        _engine = MsgSend(MsgSend(engineCls, Sel("alloc")), Sel("init"));
        if (_engine == IntPtr.Zero) throw new MicRecorderException("av:engine-init-failed");
        _inputNode = MsgSend(_engine, Sel("inputNode"));
        if (_inputNode == IntPtr.Zero) throw new MicRecorderException("av:input-node-null");

        var fmt = MsgSend(_inputNode, Sel("outputFormatForBus:"), (ulong)0);
        if (fmt != IntPtr.Zero)
        {
            var native = MsgSendDouble(fmt, Sel("sampleRate"));
            if (native is > 8000 and < 192000)
            {
                _sampleRate = (int)native;
            }
        }

        // 全局 block（无捕获 → _NSConcreteGlobalBlock，进程生命周期存活）
        _cb = OnTap;
        _cbHandle = GCHandle.Alloc(_cb);
        _block = Marshal.AllocHGlobal(IntPtr.Size * 5);
        var lib = NativeLibrary.Load(LibObjC);
        if (!NativeLibrary.TryGetExport(lib, "_NSConcreteGlobalBlock", out var concrete))
        {
            throw new MicRecorderException("av:block-class-not-found");
        }
        Marshal.WriteIntPtr(_block, 0, concrete);
        Marshal.WriteInt32(_block, IntPtr.Size, 0x50000000);   // BLOCK_IS_GLOBAL
        Marshal.WriteInt32(_block, IntPtr.Size + 4, 0);
        Marshal.WriteIntPtr(_block, IntPtr.Size * 2, Marshal.GetFunctionPointerForDelegate(_cb));
        _desc = Marshal.AllocHGlobal(IntPtr.Size * 4);
        for (int i = 0; i < 4; i++) Marshal.WriteIntPtr(_desc, i * IntPtr.Size, IntPtr.Zero);
        Marshal.WriteIntPtr(_block, IntPtr.Size * 3, _desc);

        MsgSend(_inputNode, Sel("installTapOnBus:bufferSize:format:block:"), (ulong)0, (ulong)4096, fmt, _block);
        MsgSend(_engine, Sel("prepare"));

        _err = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(_err, IntPtr.Zero);
        var ok = MsgSendBoolErr(_engine, Sel("startAndReturnError:"), _err);
        if (ok == 0)
        {
            throw new MicRecorderException("av:engine-start-failed");
        }
    }

    private void OnTap(IntPtr block, IntPtr buffer, IntPtr time)
    {
        try
        {
            var frames = MsgSendU32(buffer, Sel("frameLength"));
            if (frames == 0) return;
            var fcd = MsgSend(buffer, Sel("floatChannelData"));
            var ch0 = Marshal.ReadIntPtr(fcd);
            var floats = new float[frames];
            Marshal.Copy(ch0, floats, 0, (int)frames);
            var chunk = new byte[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                var v = (short)Math.Clamp(floats[i] * 32767f, short.MinValue, short.MaxValue);
                BitConverter.GetBytes(v).CopyTo(chunk, i * 2);
            }
            lock (_gate)
            {
                var cap = _sampleRate * 2 * MicRecorder.MaxDurationSeconds;
                var room = cap - _pcm.Length;
                var toWrite = (int)Math.Min(room, chunk.Length);
                if (toWrite > 0)
                {
                    _pcm.Write(chunk, 0, toWrite);
                    _bytesReceived += toWrite;
                }
                _lastLevel = ComputeLevel(chunk, chunk.Length);
            }
        }
        catch
        {
            // 回调内吞异常防崩溃；采集层错误经 Stop 的 no-data 路径暴露
        }
    }

    /// <summary>最近块 RMS → 归一化电平（×4 感知增益）。</summary>
    private static double ComputeLevel(byte[] chunk, int size)
    {
        var count = size / 2;
        if (count == 0) return 0;
        double sumSq = 0;
        for (int i = 0; i + 1 < size; i += 2)
        {
            var v = BitConverter.ToInt16(chunk, i) / 32768.0;
            sumSq += v * v;
        }
        return Math.Clamp(Math.Sqrt(sumSq / count) * 4.0, 0.0, 1.0);
    }

    private double _lastLevel;

    public double Level
    {
        get { lock (_gate) return _lastLevel; }
    }

    public byte[] Stop()
    {
        MsgSend(_inputNode, Sel("removeTapOnBus:"), (ulong)0);
        MsgSend(_engine, Sel("stop"));
        lock (_gate)
        {
            if (_bytesReceived == 0)
            {
                throw new MicRecorderException("no-data:av-engine");
            }
            return _pcm.ToArray();
        }
    }

    public void Dispose()
    {
        if (_err != IntPtr.Zero) { Marshal.FreeHGlobal(_err); _err = IntPtr.Zero; }
        if (_desc != IntPtr.Zero) { Marshal.FreeHGlobal(_desc); _desc = IntPtr.Zero; }
        if (_block != IntPtr.Zero) { Marshal.FreeHGlobal(_block); _block = IntPtr.Zero; }
        if (_cbHandle.IsAllocated) _cbHandle.Free();
        _cb = null;
        lock (_gate) _pcm.SetLength(0);   // 用户语音即弃（脱敏红线）
    }
}

/// <summary>Windows WASAPI 采集（NAudio）：共享模式混合格式采集 → MediaFoundationResampler 一步归一 16k/mono/s16。</summary>
internal sealed class WindowsWasapiCapture : IMicCapture
{
    private NAudio.CoreAudioApi.WasapiCapture? _capture;
    private NAudio.Wave.WaveFormat? _sourceFormat;
    private readonly MemoryStream _pcm = new();
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _stopped = new(false);
    private Exception? _error;
    private double _lastLevel;   // 最近块 RMS 电平（0..1，波形指示用）

    /// <summary>输出已归一为 16k（MediaFoundationResampler）。</summary>
    public int SampleRate => MicRecorder.SampleRate;

    public void Start()
    {
        _capture = new NAudio.CoreAudioApi.WasapiCapture();   // 默认采集设备 · 共享模式
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _stopped.Reset();
        _capture.StartRecording();   // 首次使用触发系统隐私开关（设置→隐私→麦克风）拦截
    }

    private void OnDataAvailable(object? sender, NAudio.Wave.WaveInEventArgs e)
    {
        _sourceFormat ??= _capture?.WaveFormat;   // WASAPI 混合格式常为 48k/2ch/float32
        lock (_gate)
        {
            if (_pcm.Length < MicRecorder.MaxBufferBytes)
            {
                _pcm.Write(e.Buffer, 0, e.BytesRecorded);
            }
            _lastLevel = ComputeLevel(e.Buffer, e.BytesRecorded, _sourceFormat);
        }
    }

    private void OnRecordingStopped(object? sender, NAudio.Wave.StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            lock (_gate) { _error ??= e.Exception; }
        }
        _stopped.Set();
    }

    public byte[] Stop()
    {
        try
        {
            _capture?.StopRecording();
        }
        catch
        {
            // 已停/设备丢失：失败由 RecordingStopped 汇报
        }
        _stopped.Wait(TimeSpan.FromSeconds(2));
        lock (_gate)
        {
            if (_error is not null)
            {
                throw new MicRecorderException("capture-error", _error);
            }
            var raw = _pcm.ToArray();
            var format = _sourceFormat;
            return format is null ? [] : ConvertTo16kMono16(raw, format);
        }
    }

    public void Dispose()
    {
        try { _capture?.Dispose(); } catch { /* 采集未启动等：忽略 */ }
        _capture = null;
        lock (_gate) { _pcm.SetLength(0); }
    }

    /// <summary>最近块 RMS → 归一化电平（×4 感知增益；按实际格式取 float32/16bit，未知格式回 0 走合成微动）。</summary>
    private static double ComputeLevel(byte[] buffer, int bytes, NAudio.Wave.WaveFormat? format)
    {
        if (format is null || bytes <= 0) return 0;
        double sumSq = 0;
        var count = 0;
        if (format.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat)
        {
            count = bytes / 4;
            for (int i = 0; i < count; i++)
            {
                var v = BitConverter.ToSingle(buffer, i * 4);
                sumSq += v * v;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            count = bytes / 2;
            for (int i = 0; i < count; i++)
            {
                var v = BitConverter.ToInt16(buffer, i * 2) / 32768.0;
                sumSq += v * v;
            }
        }
        if (count == 0) return 0;
        return Math.Clamp(Math.Sqrt(sumSq / count) * 4.0, 0.0, 1.0);
    }

    public double Level
    {
        get { lock (_gate) return _lastLevel; }
    }

    /// <summary>WASAPI 混合格式 → 16k/mono/16bit（MediaFoundation 系统转码器，无额外 native 依赖）。</summary>
    private static byte[] ConvertTo16kMono16(byte[] raw, NAudio.Wave.WaveFormat format)
    {
        using var source = new NAudio.Wave.RawSourceWaveStream(raw, 0, raw.Length, format);
        using var resampler = new NAudio.Wave.MediaFoundationResampler(
            source, new NAudio.Wave.WaveFormat(MicRecorder.SampleRate, 16, 1));
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}

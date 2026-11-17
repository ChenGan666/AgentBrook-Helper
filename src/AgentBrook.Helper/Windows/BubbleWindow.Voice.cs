using AgentBrook.Helper.Support;
using AgentBrook.Helper.Core.Voice;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace AgentBrook.Helper.Windows;

// 气泡长按语音输入（doc/Voice/06 状态机）：
// · 按下（气泡主体上、非双击）→ 450ms 长按判定；期间移动 > 8px → 取消判定转原生拖拽（保留旧行为）
// · 450ms 到期 → 门卫分流：插件就绪 → 录音态（Icon.Mic + 红光晕 + 呼吸膜 + toast）；
//   未安装/模型缺失/已停用 → toast 确认按钮 → 设置→插件管理 自动安装 asr / 下载语音模型
// · 录音中松开 → 停录 → AsrService 转写 → 追加主会话输入框（不自动发送，D8）；<0.5s 丢弃；60s 硬上限截断
// 录音期间 SetState 被守卫忽略（Visual.cs 顶部），结束按 _lastVisualState 重放。
public partial class BubbleWindow
{
    private const int VoiceHoldThresholdMs = 450;
    private const double VoiceDragThresholdPx = 8.0;

    private readonly Panel _root;
    private PointerPressedEventArgs? _voicePressArgs;
    private Point _voicePressPoint;
    private IPointer? _voicePointer;
    private bool _voiceRecording;
    private bool _voiceVisualActive;   // 录音视觉激活（气泡自身录音或主窗口语音输入跨窗同步共用，Visual.SetState 守卫）
    private MicRecorder? _voiceRecorder;
    private Action? _voiceRecordingToastClose;
    private DispatcherTimer? _voiceHoldTimer;
    private DispatcherTimer? _voiceMaxTimer;
    // 延迟 BeginMoveDrag 平台不支持时的手动拖拽回退（Position 跟踪；释放走正常磁吸/存位收尾）
    private bool _voiceManualDrag;
    private PixelPoint _voiceManualDragOrigin;
    // 录音波形指示（振幅响应）：5 根白条逐帧改 Height（项目惯例：不依赖 RotateTransform 语义）
    private StackPanel? _voiceWave;
    private Rectangle[]? _voiceWaveBars;
    private DispatcherTimer? _voiceWaveTimer;
    private double _voiceWaveLevel;   // 快攻慢放包络
    private long _voiceWaveTickN;

    // ───────────────────────── 长按判定（axaml.cs 指针事件驱动） ─────────────────────────

    /// <summary>按压点是否落在气泡主体（Root）上——插件图标/伙伴层/Toast 卡按下不触发语音判定。</summary>
    private bool VoicePressOnBody(PointerPressedEventArgs e)
    {
        return e.Source is Visual visual && (_root == visual || _root.IsVisualAncestorOf(visual));
    }

    /// <summary>单次左键按下（非双击）：记录按压点，启动 450ms 长按判定（暂不启动原生拖拽）。</summary>
    private void VoicePressStarted(PointerPressedEventArgs e, Point position)
    {
        _voicePressArgs = e;
        _voicePressPoint = position;
        _voicePointer = e.Pointer;
        if (_voiceHoldTimer is null)
        {
            _voiceHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(VoiceHoldThresholdMs) };
            _voiceHoldTimer.Tick += VoiceHoldTimerTick;
        }
        _voiceHoldTimer.Stop();
        _voiceHoldTimer.Start();
    }

    /// <summary>按住期间移动超阈值 → 取消判定转拖拽（立即拖拽=移动气泡位置，doc/Voice/06 决策1）。</summary>
    private void VoicePressMoved(Point position)
    {
        var dx = position.X - _voicePressPoint.X;
        var dy = position.Y - _voicePressPoint.Y;
        if (dx * dx + dy * dy > VoiceDragThresholdPx * VoiceDragThresholdPx)
        {
            BeginDeferredDrag();
        }
    }

    /// <summary>松开/取消时清理挂起判定（不影响录音态收尾）。</summary>
    private void VoicePressEnded()
    {
        if (_voiceRecording) return;
        _voiceHoldTimer?.Stop();
        _voicePressArgs = null;
        _voicePointer = null;
        _voiceManualDrag = false;
    }

    private void VoiceHoldTimerTick(object? sender, EventArgs e)
    {
        _voiceHoldTimer?.Stop();
        _voicePressArgs = null;
        if (_initializing)
        {
            return;   // 初始化中不进语音（Init.cs 门卫）：ASR 未就绪；拖拽不受影响（移动即转原生拖拽）
        }
        _ = VoiceBeginAsync();
    }

    /// <summary>延迟原生拖拽：把按下时暂存的 args 交给系统（仍在按住手势内调用）。
    /// 平台不支持延迟调用时回退手动 Position 跟踪（模式同 LanPeerBubbles）。</summary>
    private void BeginDeferredDrag()
    {
        _voiceHoldTimer?.Stop();
        if (_voiceRecording) return;
        var args = _voicePressArgs;
        _voicePressArgs = null;
        if (args is not null)
        {
            try
            {
                BeginMoveDrag(args);
                return;
            }
            catch
            {
                // 平台拒绝延迟调用 → 手动拖拽回退
            }
        }
        _voiceManualDrag = true;
        _voiceManualDragOrigin = Position;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (_voiceManualDrag)
        {
            // 手动拖拽回退：窗口位置 = 起点 + 指针位移
            Position = new PixelPoint(
                (int)(_voiceManualDragOrigin.X + position.X - _voicePressPoint.X),
                (int)(_voiceManualDragOrigin.Y + position.Y - _voicePressPoint.Y));
            return;
        }
        if (_voiceHoldTimer is { IsEnabled: true })
        {
            VoicePressMoved(position);
        }
    }

    // ───────────────────────── 语音模式 ─────────────────────────

    private async Task VoiceBeginAsync()
    {
        if (_voiceRecording) return;
        try
        {
            var gate = VoiceInput.EvaluateFromApp();
            if (gate == VoiceInput.Gate.Ready)
            {
                await VoiceStartRecordingAsync();
                return;
            }
            ShowVoiceGateToasts(gate);   // 门卫引导（未安装/模型缺/停用/未就绪）
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[voice] begin: {ex.Message}");
        }
    }

    /// <summary>门卫未过引导 toast（确认按钮跳设置页）：气泡自身长按与主窗口语音输入共用。</summary>
    public void ShowVoiceGateToasts() => ShowVoiceGateToasts(VoiceInput.EvaluateFromApp());

    private void ShowVoiceGateToasts(VoiceInput.Gate gate)
    {
        switch (gate)
        {
            case VoiceInput.Gate.AgentNotReady:
                ShowVoiceHint(I18n.T("ui.voice.agent.not.ready"));
                break;
            case VoiceInput.Gate.PluginNotInstalled:
                ShowToast(
                    I18n.T("ui.voice.need.plugin"), "#ffb340", null,
                    [(I18n.T("ui.voice.install.now"), OpenVoicePluginSetup)]);
                break;
            case VoiceInput.Gate.ModelMissing:
                ShowToast(
                    I18n.T("ui.voice.model.missing"), "#ffb340", null,
                    [(I18n.T("ui.voice.download.model"), OpenVoiceModelDownload)]);
                break;
            case VoiceInput.Gate.PluginDisabled:
                ShowToast(
                    I18n.T("ui.voice.plugin.disabled"), "#ffb340", null,
                    [(I18n.T("ui.voice.open.settings"), OpenVoicePluginPage)]);
                break;
        }
    }

    private async Task VoiceStartRecordingAsync()
    {
        var recorder = new MicRecorder();
        try
        {
            recorder.Start();   // macOS 首用在此触发 TCC 授权；被拒/无设备抛 MicRecorderException
        }
        catch (MicRecorderException ex)
        {
            // status= 为 AudioQueueStart 失败——dev 运行（dotnet run）时 TCC 授权挂在宿主终端上，多半是权限
            VoiceLog.Write($"start failed: {ex.Reason}");
            var permissionLikely = ex.Reason.Contains("status=", StringComparison.Ordinal);
            ShowVoiceHint(I18n.T(permissionLikely ? "ui.voice.mic.permission" : "ui.voice.record.start.failed"));
            return;
        }
        catch (Exception ex)
        {
            VoiceLog.Write($"start failed: {ex.Message}");
            ShowVoiceHint(I18n.T("ui.voice.record.start.failed"));
            return;
        }
        _voiceRecorder = recorder;
        _voiceRecording = true;
        _voicePointer?.Capture(_root);   // 窗口外松开也能收到 Released（60s 上限兜底）

        // 录音态视觉：麦克风 + 红色光晕 + 呼吸膜 + 底缘振幅波形（SetState 已被守卫忽略）
        ShowRecordingVisual(I18n.T("ui.voice.recording"));

        // 60s 硬上限自动截断（doc/Voice/03 §2）
        if (_voiceMaxTimer is null)
        {
            _voiceMaxTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(MicRecorder.MaxDurationSeconds) };
            _voiceMaxTimer.Tick += (_, _) => VoiceFinishRecording();
        }
        _voiceMaxTimer.Stop();
        _voiceMaxTimer.Start();
    }

    /// <summary>释放事件分流：录音中且主键释放 → 收尾转写（返回 true，不走磁吸/存位）；否则 false。</summary>
    private bool VoiceRelease(PointerReleasedEventArgs e)
    {
        if (!_voiceRecording) return false;
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased)
        {
            return false;   // 非主键释放不结束录音
        }
        VoiceFinishRecording();
        return true;
    }

    private async void VoiceFinishRecording()
    {
        _voiceMaxTimer?.Stop();
        var recorder = _voiceRecorder;
        _voiceRecorder = null;
        _voiceRecording = false;
        _voiceVisualActive = false;
        HideRecordingVisual();
        _voicePointer?.Capture(null);
        _voicePointer = null;
        if (recorder is null) return;

        byte[] pcm;
        try
        {
            // 停录放后台线程：AudioQueueStop/AVAudioEngine.stop 的同步等待不再占 UI 线程
            pcm = await Task.Run(() => recorder.Stop());
        }
        catch (MicRecorderException ex)
        {
            // no-data: 回调无数据（权限静默流/转换层）；capture-error: 设备中途出错
            VoiceLog.Write($"stop failed: {ex.Reason}");
            ShowVoiceHint(I18n.T("ui.voice.silence"));
            return;
        }
        catch (Exception ex)
        {
            VoiceLog.Write($"stop failed: {ex.Message}");
            ShowVoiceHint(I18n.T("ui.voice.record.start.failed"));
            return;
        }
        // 尺寸诊断日志（只记时长/字节数，不落音频内容——脱敏红线）
        var rate = recorder.CaptureRate;
        VoiceLog.Write($"captured {VoiceInput.DurationSeconds(pcm.Length, rate):0.0}s ({pcm.Length} bytes @ {rate}Hz)");
        if (VoiceInput.TooShort(pcm.Length, rate))
        {
            ShowVoiceHint(I18n.T("ui.voice.too.short"));
            return;
        }
        pcm = VoiceInput.TruncateToMaxDuration(pcm, rate);
        if (VoiceInput.IsSilence(pcm))
        {
            ShowVoiceHint(I18n.T("ui.voice.silence"));
            return;
        }

        var transcribeToastClose = ShowToast(I18n.T("ui.voice.transcribing"), "#4a9eff", null);
        try
        {
            // 转写后台执行：sherpa 原生解码不再阻塞 UI 线程；30s 超时兜底（解码本身秒级）
            var result = await Task.Run(() => VoiceInput.TranscribeAsync(pcm, rate))
                .WaitAsync(TimeSpan.FromSeconds(30));
            VoiceLog.Write($"transcribed engine={result.Engine} ok={result.Success} textLen={result.Text?.Length ?? 0} error={result.Error}");
            transcribeToastClose?.Invoke();
            if (!result.Success)
            {
                ShowVoiceHint(I18n.T("ui.voice.failed.value", result.Error ?? "-"));
                return;
            }
            var text = result.Text?.Trim() ?? "";
            if (text.Length == 0)
            {
                ShowVoiceHint(I18n.T("ui.voice.not.recognized"));
                return;
            }
            if (App.Controller.ConversationWindow is MainWindow mainWindow)
            {
                mainWindow.AppendInputDraft(text);
                mainWindow.ShowFromBubble();
                ShowToast(I18n.T("ui.voice.filled"), "#34c759", TimeSpan.FromSeconds(2.5));
            }
        }
        catch (Exception ex)
        {
            transcribeToastClose?.Invoke();
            ShowVoiceHint(I18n.T("ui.voice.failed.value", ex.Message));
        }
    }

    // ───────────────────────── 录音视觉（气泡自身录音 / 主窗口语音输入跨窗同步共用） ─────────────────────────

    /// <summary>显示录音视觉：麦克风图标 + 红色光晕 + 呼吸膜 + 振幅波形 + 提示卡（调用方负责守卫标志）。</summary>
    private void ShowRecordingVisual(string toastText)
    {
        _voiceVisualActive = true;
        StopGearSpin();
        SetLook("Icon.Mic", "#ff5f57", "#ff8a80", "#e53935", ring: true);
        VoiceWaveStart();
        _voiceRecordingToastClose?.Invoke();
        _voiceRecordingToastClose = ShowToast(toastText, "#ff5f57", null);
    }

    /// <summary>隐藏录音视觉并按最后回合状态重放（录音期间 SetState 被守卫忽略）。</summary>
    private void HideRecordingVisual()
    {
        VoiceWaveStop();
        _voiceRecordingToastClose?.Invoke();
        _voiceRecordingToastClose = null;
        if (_lastVisualState is { } state)
        {
            SetState(state, null);
        }
    }

    /// <summary>外部同步入口（主窗口按住输入框语音输入时）：气泡同步进入录音视觉（doc/Voice/06）。</summary>
    public void BeginVoiceVisual(string toastText)
    {
        if (_voiceRecording) return;   // 气泡自身录音中，视觉已就位
        ShowRecordingVisual(toastText);
    }

    /// <summary>外部同步入口：退出录音视觉。</summary>
    public void EndVoiceVisual()
    {
        if (_voiceRecording) return;   // 气泡自身录音未结束，视觉由自身流程收
        _voiceVisualActive = false;
        HideRecordingVisual();
    }

    // ───────────────────────── 录音波形指示（振幅响应，doc/Voice/06） ─────────────────────────

    private void VoiceWaveStart()
    {
        _voiceWave ??= this.FindControl<StackPanel>("VoiceWave")!;
        _voiceWaveBars ??= _voiceWave.Children.OfType<Rectangle>().ToArray();
        _voiceWaveLevel = 0;
        foreach (var bar in _voiceWaveBars)
        {
            bar.Height = 3;
        }
        _voiceWave.IsVisible = true;
        if (_voiceWaveTimer is null)
        {
            _voiceWaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _voiceWaveTimer.Tick += VoiceWaveTick;
        }
        _voiceWaveTimer.Start();
    }

    private void VoiceWaveStop()
    {
        _voiceWaveTimer?.Stop();
        if (_voiceWave is { } wave)
        {
            wave.IsVisible = false;
            if (_voiceWaveBars is not null)
            {
                foreach (var bar in _voiceWaveBars)
                {
                    bar.Height = 3;
                }
            }
        }
        _voiceWaveLevel = 0;
    }

    private void VoiceWaveTick(object? sender, EventArgs e)
    {
        if (_voiceWaveBars is null || _voiceWaveBars.Length == 0) return;
        var level = _voiceRecorder?.Level ?? 0.0;
        // 快攻慢放包络：说话瞬时抬升，静音缓慢回落
        _voiceWaveLevel = Math.Max(level, _voiceWaveLevel * 0.82);
        _voiceWaveTickN++;
        for (var i = 0; i < _voiceWaveBars.Length; i++)
        {
            // 逐条相位差并与电平耦合：说话时整排起伏跳动，静音时仅底部微动（示意麦克风在工作）
            var phase = Math.Sin((_voiceWaveTickN + i * 2.1) * 0.55);
            var mix = Math.Clamp(_voiceWaveLevel * (0.6 + 0.4 * phase) + 0.10 * phase, 0, 1);
            _voiceWaveBars[i].Height = 3 + 11 * mix;
        }
    }

    // ───────────────────────── 门卫引导动作 ─────────────────────────

    /// <summary>未安装：确认 → 弹出设置→插件管理 → 自动安装 asr → 装完气泡提示下载语音模型（用户决策流程）。</summary>
    private void OpenVoicePluginSetup()
    {
        var settings = SettingsWindow.ShowSingle();
        settings.ShowPluginsPage();
        _ = settings.InstallVoicePluginAsync();
    }

    /// <summary>已装模型缺：确认 → 插件管理页 → 直接发起语音模型下载（进度写状态行）。</summary>
    private void OpenVoiceModelDownload()
    {
        var settings = SettingsWindow.ShowSingle();
        settings.ShowPluginsPage();
        _ = settings.DownloadVoiceModelAsync();
    }

    /// <summary>已停用：引导到插件管理页启用。</summary>
    private void OpenVoicePluginPage()
    {
        var settings = SettingsWindow.ShowSingle();
        settings.ShowPluginsPage();
    }

    private void ShowVoiceHint(string text) => ShowToast(text, "#ffb340", TimeSpan.FromSeconds(3.5));
}

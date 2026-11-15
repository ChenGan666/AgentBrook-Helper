using AgentBrook.Helper.Support;
using AgentBrook.Helper.Core.Voice;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AgentBrook.Helper.Windows;

// 主会话输入框按住说话（doc/Voice/03 主输入区语音输入，复用 06 气泡语音的采集/门卫/转写管线）：
// · InputBox 按下 450ms 未动 → 门卫 → 录音态（输入卡覆盖层 + 气泡跨窗同步录音视觉）
// · 期间移动 >8px → 取消判定（正常光标定位/文本选区不受影响）；短按 = 正常点击交还 TextBox
// · 松开 → 停录 → AsrService 转写 → 追加输入框草稿（不自动发送，D8）；<0.3s 丢弃；60s 硬上限
public partial class MainWindow
{
    private const int VoiceHoldThresholdMs = 450;
    private const double VoiceDragThresholdPx = 8.0;

    private DispatcherTimer? _voiceHoldTimer;
    private Point _voicePressPoint;
    private IPointer? _voicePointer;
    private bool _inputVoiceRecording;
    private bool _inputVoiceBusy;   // 释放后转写进行中（防重入）
    private MicRecorder? _voiceRecorder;
    private DispatcherTimer? _voiceMaxTimer;
    private Border? _voiceOverlay;
    private TextBlock? _voiceOverlayText;
    private TextBlock? _voiceOverlaySubText;

    /// <summary>ctor 调用：挂 InputBox 按住说话判定（handledEventsToo=true——TextBox 自身会处理按下做光标定位）。</summary>
    private void InitInputVoice()
    {
        _voiceOverlay = this.FindControl<Border>("VoiceOverlay");
        _voiceOverlayText = this.FindControl<TextBlock>("VoiceOverlayText");
        _voiceOverlaySubText = this.FindControl<TextBlock>("VoiceOverlaySubText");
        if (_inputBox is null) return;

        _inputBox.AddHandler(InputElement.PointerPressedEvent, OnInputVoicePressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _inputBox.AddHandler(InputElement.PointerMovedEvent, OnInputVoiceMoved,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _inputBox.AddHandler(InputElement.PointerReleasedEvent, OnInputVoiceReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnInputVoicePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_inputVoiceRecording || _inputVoiceBusy) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        _voicePressPoint = point.Position;
        _voicePointer = e.Pointer;
        if (_voiceHoldTimer is null)
        {
            _voiceHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(VoiceHoldThresholdMs) };
            _voiceHoldTimer.Tick += VoiceHoldTimerTick;
        }
        _voiceHoldTimer.Stop();
        _voiceHoldTimer.Start();
    }

    private void OnInputVoiceMoved(object? sender, PointerEventArgs e)
    {
        if (_inputVoiceRecording) return;   // 录音中移动忽略（松开即结束转写）
        if (_voiceHoldTimer is { IsEnabled: true })
        {
            // 移动超阈值 = 正常文本选区/拖选，取消语音判定（编辑体验不受影响）
            var p = e.GetPosition(this);
            var dx = p.X - _voicePressPoint.X;
            var dy = p.Y - _voicePressPoint.Y;
            if (dx * dx + dy * dy > VoiceDragThresholdPx * VoiceDragThresholdPx)
            {
                _voiceHoldTimer.Stop();
            }
        }
    }

    private void OnInputVoiceReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_inputVoiceRecording)
        {
            if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased) return;
            FinishInputVoice();
            return;
        }
        _voiceHoldTimer?.Stop();   // 未达语音门槛：正常点击/选区交还 TextBox
        _voicePointer = null;
    }

    private void VoiceHoldTimerTick(object? sender, EventArgs e)
    {
        _voiceHoldTimer?.Stop();
        _ = InputVoiceBeginAsync();
    }

    private async Task InputVoiceBeginAsync()
    {
        if (_inputVoiceRecording || _inputVoiceBusy) return;
        try
        {
            if (VoiceInput.EvaluateFromApp() != VoiceInput.Gate.Ready)
            {
                App.Controller.Bubble?.ShowVoiceGateToasts();   // 门卫引导（含确认按钮），与气泡语音共用
                return;
            }
            await StartInputRecordingAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[voice] input begin: {ex.Message}");
        }
    }

    private async Task StartInputRecordingAsync()
    {
        var recorder = new MicRecorder();
        try
        {
            recorder.Start();   // macOS 首用触发 TCC 授权
        }
        catch (MicRecorderException ex)
        {
            VoiceLog.Write($"input start failed: {ex.Reason}");
            var permissionLikely = ex.Reason.Contains("status=", StringComparison.Ordinal);
            ShowVoiceHint(I18n.T(permissionLikely ? "ui.voice.mic.permission" : "ui.voice.record.start.failed"));
            return;
        }
        catch (Exception ex)
        {
            VoiceLog.Write($"input start failed: {ex.Message}");
            ShowVoiceHint(I18n.T("ui.voice.record.start.failed"));
            return;
        }
        _voiceRecorder = recorder;
        _inputVoiceRecording = true;
        _voicePointer?.Capture(_inputBox);   // 框外松开也能收到 Released（60s 上限兜底）

        // 气泡跨窗同步录音视觉（麦克风+红光晕+波形+toast）+ 输入卡覆盖层
        App.Controller.Bubble?.BeginVoiceVisual(I18n.T("ui.voice.recording"));
        if (_voiceOverlay is { } overlay)
        {
            if (_voiceOverlayText is { } t) t.Text = I18n.T("ui.voice.recording");
            if (_voiceOverlaySubText is { } s) s.Text = I18n.T("ui.voice.overlay.hint");
            overlay.IsVisible = true;
        }

        // 60s 硬上限自动截断（doc/Voice/03 §2）
        if (_voiceMaxTimer is null)
        {
            _voiceMaxTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(MicRecorder.MaxDurationSeconds) };
            _voiceMaxTimer.Tick += (_, _) => FinishInputVoice();
        }
        _voiceMaxTimer.Stop();
        _voiceMaxTimer.Start();
    }

    private async void FinishInputVoice()
    {
        _voiceMaxTimer?.Stop();
        var recorder = _voiceRecorder;
        _voiceRecorder = null;
        _inputVoiceRecording = false;
        _voicePointer?.Capture(null);
        _voicePointer = null;
        App.Controller.Bubble?.EndVoiceVisual();
        if (_voiceOverlay is { } overlay) overlay.IsVisible = false;
        if (recorder is null) return;

        var rate = recorder.CaptureRate;

        // 停录+NAudio/MediaFoundation 归一化放后台 MTA 线程：Windows 上 MF 要求 MTA，
        // UI（STA）线程同步调用会卡住甚至崩溃（doc/Voice/06 Windows 排障）
        var pcmResult = await Task.Run(() =>
        {
            try
            {
                var pcm = recorder.Stop();
                return (Ok: true, Pcm: pcm, Error: (string?)null);
            }
            catch (MicRecorderException ex)
            {
                VoiceLog.Write($"input stop failed: {ex.Reason}");
                return (Ok: false, Pcm: Array.Empty<byte>(), Error: (string?)"silence");
            }
            catch (Exception ex)
            {
                VoiceLog.Write($"input stop failed: {ex.Message}");
                return (Ok: false, Pcm: Array.Empty<byte>(), Error: (string?)"start-failed");
            }
        });
        var pcm = pcmResult.Pcm;
        if (!pcmResult.Ok)
        {
            ShowVoiceHint(I18n.T(pcmResult.Error == "silence" ? "ui.voice.silence" : "ui.voice.record.start.failed"));
            return;
        }
        // 尺寸诊断日志（只记时长/字节数，不落音频内容——脱敏红线）
        VoiceLog.Write($"input captured {VoiceInput.DurationSeconds(pcm.Length, rate):0.0}s ({pcm.Length} bytes @ {rate}Hz)");
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

        _inputVoiceBusy = true;
        var transcribeToastClose = App.Controller.Bubble?.ShowToast(I18n.T("ui.voice.transcribing"), "#4a9eff", null);
        try
        {
            // 转写（sherpa 原生推理）同样后台执行：同步解码在 UI 线程会卡住整窗，
            // Windows 原生库异常也不至于冻结界面；30s 超时兜底（解码本身秒级，超时即异常）
            var result = await Task.Run(() => VoiceInput.TranscribeAsync(pcm, rate))
                .WaitAsync(TimeSpan.FromSeconds(30));
            VoiceLog.Write($"input transcribed engine={result.Engine} ok={result.Success} textLen={result.Text?.Length ?? 0} error={result.Error}");
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
            AppendInputDraft(text);
            _inputBox?.Focus();
        }
        catch (TimeoutException)
        {
            transcribeToastClose?.Invoke();
            VoiceLog.Write("input transcribed timeout(30s)");
            ShowVoiceHint(I18n.T("ui.voice.failed.value", "timeout"));
        }
        catch (Exception ex)
        {
            transcribeToastClose?.Invoke();
            VoiceLog.Write($"input transcribed exception: {ex.Message}");
            ShowVoiceHint(I18n.T("ui.voice.failed.value", ex.Message));
        }
        finally
        {
            _inputVoiceBusy = false;
        }
    }

    private void ShowVoiceHint(string text) => App.Controller.Bubble?.ShowToast(text, "#ffb340", TimeSpan.FromSeconds(3.5));
}

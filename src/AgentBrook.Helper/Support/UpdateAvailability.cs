using AgentBrook.Helper.Update;

namespace AgentBrook.Helper.Support;

/// <summary>
/// 「有新版本」可用状态：更新检查结果 → 主窗口顶栏更新按钮的显隐依据。
/// 无更新/未检查为 null；同一版本重复写入不触发事件（事件推进来回刷新）。
/// </summary>
internal sealed class UpdateAvailability
{
    private UpdateInfo? _info;

    /// <summary>当前可用新版本（null = 已是最新或尚未检查）。</summary>
    public UpdateInfo? Info => _info;

    /// <summary>可用状态发生变化（订阅方自行切到 UI 线程再刷界面）。</summary>
    public event Action? Changed;

    /// <summary>写入一次检查结果。</summary>
    public void Set(UpdateInfo? info)
    {
        if (_info is null && info is null)
        {
            return;
        }
        if (_info is not null && info is not null && _info.Version == info.Version)
        {
            _info = info;   // 同一版本复检：保留最新说明，但不必让 UI 重刷
            return;
        }
        _info = info;
        Changed?.Invoke();
    }
}

/// <summary>顶栏更新按钮的悬停提示：版本号 + 压成一行的更新说明。</summary>
internal static class UpdateNotice
{
    private const int NotesMaxChars = 120;

    public static string Tooltip(UpdateInfo info)
    {
        var notes = (info.ReleaseNotes ?? "")
            .Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ')
            .Trim();
        if (notes.Length > NotesMaxChars)
        {
            notes = notes[..NotesMaxChars] + "…";
        }
        var version = info.Version.ToString();
        return notes.Length == 0
            ? I18n.T("ui.update.available.tip", version)
            : I18n.T("ui.update.available.tip.notes", version, notes);
    }
}

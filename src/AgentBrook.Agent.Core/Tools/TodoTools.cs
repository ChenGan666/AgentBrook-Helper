using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace AgentBrook.Agent.Tools;

/// <summary>
/// 模型可见的任务清单（对齐 Harness todo 语义的最薄实现）：
/// todo_write 全量覆写计划，todo_list 读取当前进度。
/// 计划文本作为工具结果留在会话记录里——上下文压缩后模型重读 transcript 即可恢复进度；
/// onChanged 回调供宿主把计划同步到 UI（可折叠计划卡），为 null 时纯工具模式运行。
/// </summary>
/// <summary>计划项状态：pending（未开始）| in_progress（进行中）| done（已完成）。</summary>
public sealed record TodoItem(int Id, string Title, string Status);

public sealed class TodoTools(Action<IReadOnlyList<TodoItem>>? onChanged = null)
{

    private const int MaxItems = 30;
    private const int MaxTitleChars = 200;
    private static readonly string[] ValidStatus = ["pending", "in_progress", "done"];

    private readonly List<TodoItem> _items = new();
    private readonly Action<IReadOnlyList<TodoItem>>? _onChanged = onChanged;

    /// <summary>当前计划快照（宿主/UI 读取用）。</summary>
    public IReadOnlyList<TodoItem> Items => _items;

    [Description("建立/更新任务清单：多步骤任务开工前先用它列计划，之后每完成一项就把它标为 done。整表覆写（items_json 必须包含全部计划项，不要只发变化项）。")]
    public string todo_write(
        [Description("完整计划项列表，JSON 数组：[{\"id\":1,\"title\":\"做什么\",\"status\":\"pending|in_progress|done\"}]")] string items_json)
    {
        List<TodoItem> parsed;
        try
        {
            using var doc = JsonDocument.Parse(items_json);
            parsed = [];
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var title = el.GetProperty("title").GetString()?.Trim() ?? "";
                var status = el.GetProperty("status").GetString()?.Trim() ?? "pending";
                if (string.IsNullOrEmpty(title))
                {
                    return "解析失败：存在 title 为空的计划项，请补全后重发。";
                }
                if (!ValidStatus.Contains(status))
                {
                    status = "pending";
                }
                var id = el.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var i) ? i : parsed.Count + 1;
                parsed.Add(new TodoItem(id, title.Length > MaxTitleChars ? title[..MaxTitleChars] : title, status));
            }
        }
        catch (Exception ex)
        {
            return $"解析失败：items_json 不是合法 JSON（{ex.Message}）。请按 [{{\"id\":1,\"title\":\"...\",\"status\":\"pending\"}}] 格式重发。";
        }

        if (parsed.Count == 0)
        {
            return "解析失败：计划为空。不需要计划时请改用 todo_list 确认后直接执行。";
        }
        if (parsed.Count > MaxItems)
        {
            parsed = parsed.Take(MaxItems).ToList();
        }

        _items.Clear();
        _items.AddRange(parsed);
        _onChanged?.Invoke(_items.ToList());
        return Render("计划已更新：");
    }

    [Description("查看当前任务清单进度（自查还有哪些项未完成；上下文较长时被压缩后可用它恢复计划）。")]
    public string todo_list()
    {
        return _items.Count == 0
            ? "当前没有任务清单。多步骤任务请先调用 todo_write 列出计划。"
            : Render("当前计划：");
    }

    private string Render(string header)
    {
        var sb = new StringBuilder(header).Append('\n');
        var done = 0;
        foreach (var it in _items)
        {
            var mark = it.Status switch
            {
                "done" => "✓",
                "in_progress" => "▸",
                _ => " ",
            };
            if (it.Status == "done")
            {
                done++;
            }
            sb.Append($"{it.Id}.[{mark}] {it.Title}\n");
        }
        sb.Append($"进度：{done}/{_items.Count} 完成");
        return sb.ToString();
    }
}

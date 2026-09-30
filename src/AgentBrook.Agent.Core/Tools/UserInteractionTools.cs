using System.ComponentModel;

namespace AgentBrook.Agent.Tools;

/// <summary>
/// 用户参与 loop：模型在任务执行中途通过 ask_user 向宿主提问（IBrookInteraction 回调），
/// 回答作为工具结果回到模型上下文，形成"执行 → 提问 → 回答 → 继续"的循环。
/// 呈现方式由宿主决定（控制台提示、Web 卡片、语音播报等）。
/// </summary>
public sealed class UserInteractionTools(IBrookInteraction interaction)
{
    private readonly IBrookInteraction _interaction = interaction;

    [Description("任务执行过程中需要用户补充信息、在方案之间做选择、或确认关键决定时调用。问题会实时展示给用户并等待其输入，用户的回答会作为结果返回。不要用它闲聊，也不要在同一轮里重复问相同的问题。")]
    public async Task<string> ask_user(
        [Description("向用户提出的问题，应当具体、可直接回答")] string question,
        [Description("可选候选项，多个选项用 | 分隔，如：深色|浅色|跟随系统")] string? choices = null)
    {
        var answer = await _interaction.AskUserAsync(question, choices);

        if (string.IsNullOrWhiteSpace(answer))
        {
            return "（用户未回答，请以最合理的方式自行继续，并在最终答复中说明这一点）";
        }
        return $"用户的回答：{answer.Trim()}";
    }
}

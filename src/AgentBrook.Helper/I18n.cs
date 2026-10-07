using System.Globalization;

namespace AgentBrook.Helper;

/// <summary>
/// 界面多语言支持：以中文原文为键，英文映射翻译。
/// 语言设置：auto（跟随系统，默认）/ zh / en，持久化于 settings.json 的 UiLanguage。
/// </summary>
internal static class I18n
{
    public static string Setting { get; private set; } = "auto";   // 用户设置值
    public static string Lang { get; private set; } = "zh";        // 解析后的实际语言

    /// <summary>语言切换后触发（窗口订阅以刷新已渲染的静态文案）。</summary>
    public static event Action? LanguageChanged;

    private static readonly Dictionary<string, string> En = new()
    {
        // 状态条
        ["启动中…"] = "Starting…",
        ["启动中 · 连接 MCP…"] = "Starting · connecting MCP…",
        ["启动中 · 恢复会话…"] = "Starting · restoring session…",
        ["就绪"] = "Ready",
        ["就绪 · {0} 工具"] = "Ready · {0} tools",
        ["空闲"] = "Idle",
        ["空闲 · {0}"] = "Idle · {0}",
        ["思考中…"] = "Thinking…",
        ["干活中 · {0}"] = "Working · {0}",
        ["等你批准 · {0}"] = "Awaiting approval · {0}",
        ["等你回答"] = "Awaiting your answer",
        // 输入区
        ["输入消息，回车发送"] = "Type a message, Enter to send",
        ["完全访问"] = "Full access",
        ["每次询问"] = "Ask every time",
        ["每次询问：执行前征求同意"] = "Ask every time: confirm before running",
        ["完全访问：操作自动批准（重启复位）"] = "Full access: auto-approve (resets on restart)",
        ["完全访问·开"] = "Full access · on",
        ["开启后操作自动批准，重启复位"] = "Auto-approve actions; resets on restart",
        ["切换模型"] = "Switch model",
        ["切换会话"] = "Switch session",
        ["任务进行中，结束后可切换会话"] = "Busy — switch sessions after the current task",
        ["（暂无历史会话）"] = "(No archived sessions yet)",
        ["把当前会话成果沉淀为技能"] = "Distill this session into a skill",
        ["已开启新对话（上一个会话已归档，可在会话列表中找回）。"] = "New session started (previous one is archived and available in the session list).",
        ["已开启新对话（上一个会话已归档，可在左上角会话列表中找回）。"] = "New session started (previous one archived — see the session list, top-left).",
        ["已切换到会话「{0}」。该会话的上下文与摘要已载入，直接继续即可。"] = "Switched to session \"{0}\". Its context and summary are loaded — just continue.",
        ["✔ 当前会话成果已沉淀为技能「{0}」。其他会话中可直接让我 load_skill 调用这些成果。"] = "✔ Session distilled into skill \"{0}\". In any other session, just ask me to load_skill it.",
        ["沉淀失败：{0}"] = "Distill failed: {0}",
        ["每次询问（更安全）"] = "Ask every time (safer)",
        ["完全访问（自动批准，重启复位）"] = "Full access (auto-approve, resets on restart)",
        ["添加附件（或直接粘贴图片）"] = "Add attachment (or paste image)",
        ["发送"] = "Send",
        // 审批卡
        ["想执行一个操作，需要你批准"] = "wants to perform an action and needs your approval",
        ["查看原始指令"] = "View raw command",
        ["操作类型：{0}"] = "Action type: {0}",
        ["拒绝的话可以写理由（可选）"] = "Reason for rejection (optional)",
        ["✓ 批准"] = "✓ Approve",
        ["✗ 拒绝"] = "✗ Reject",
        ["批准后 {0} 才会真正执行这个操作。"] = "{0} will only run this after your approval.",
        ["✔ 已批准"] = "✔ Approved",
        ["已开启完全访问：本次及后续操作自动批准（重启后复位）"] = "Full access granted: actions auto-approved this session (resets on restart)",
        ["✘ 已拒绝：{0}"] = "✘ Rejected: {0}",
        ["已批准"] = "Approved",
        // ask 卡
        ["有问题想问你"] = "has a question for you",
        ["输入你的回答…"] = "Type your answer…",
        ["回答"] = "Answer",
        ["你回答了：{0}"] = "You answered: {0}",
        // 其他
        ["出错了：{0}"] = "Error: {0}",
        ["执行过程 · {0} 步"] = "Process · {0} steps",
        ["思考 · 持续了 {0} 秒"] = "Thinking · {0}s",
        ["附件：{0}"] = "Attachment: {0}",
        ["（已开启新对话）"] = "(New conversation started)",
        ["就绪。"] = "Ready.",
        // 气泡菜单
        ["打开对话窗体"] = "Open chat window",
        ["关闭完全访问"] = "Disable full access",
        ["开启完全访问（审批自动通过）"] = "Enable full access (auto-approve)",
        ["退出 {0}"] = "Quit {0}",
        // 设置窗口
        ["设置"] = "Settings",
        ["基础设置"] = "Basic",
        ["常规"] = "General",
        ["外观"] = "Appearance",
        ["模型设置"] = "Models",
        ["模型"] = "Models",
        ["助手身份与交流偏好。"] = "Assistant identity & reply language.",
        ["助手名字"] = "Assistant name",
        ["交流语言（影响模型回复语言）"] = "Reply language (affects model output)",
        ["默认提示词（附加到系统指令，每轮生效）"] = "Custom prompt (appended to system instructions)",
        ["保存常规设置"] = "Save general",
        ["气泡图标"] = "Bubble icon",
        ["气泡位置"] = "Bubble position",
        ["悬浮气泡的个性化显示。"] = "Personalize the floating bubble.",
        ["管理模型供应商，配置后即可在底部切换使用。"] = "Manage model providers, then switch from the bottom bar.",
        ["保存并应用"] = "Save & apply",
        ["关闭"] = "Close",
        ["供应商名称"] = "Provider name",
        ["API 地址（OpenAI 兼容端点）"] = "API endpoint (OpenAI-compatible)",
        ["模型清单（逗号分隔，第一个为默认）"] = "Models (comma-separated, first is default)",
        ["删除此供应商"] = "Delete this provider",
        ["＋ 添加供应商"] = "＋ Add provider",
        ["快速添加（点选模板）"] = "Quick add (templates)",
        ["（启动中…）"] = "(Starting…)",
        ["管理模型…"] = "Manage models…",
        ["模型"] = "Models",
        ["（使用中）"] = " (in use)",
        ["个性化"] = "Personalization",
        ["助手身份与交流偏好。"] = "Assistant identity & reply preferences.",
        ["交流语言（影响模型回复语言）"] = "Reply language (affects model output)",
        ["默认提示词（附加到系统指令，每轮生效）"] = "Custom prompt (appended to system instructions, applies every turn)",
        ["界面语言（UI 显示；切换后重启应用完全生效）"] = "UI language (full effect after app restart)",
        ["开机自启动"] = "Launch at login",
        ["登录系统后自动启动，气泡常驻待命"] = "Start automatically after login; the bubble stays resident",
        ["✔ 已开启：下次登录自动启动"] = "✔ Enabled: will start on next login",
        ["已关闭开机自启动"] = "Auto-start disabled",
        ["设置失败：{0}"] = "Failed to apply: {0}",
        ["提示音"] = "Notification sound",
        ["复制"] = "Copy",
        ["复制全文"] = "Copy all",
        ["复制表格"] = "Copy table",
        ["任务完成且对话窗不在前台时播放"] = "Plays when a task finishes while the chat window is in the background",
        ["叮"] = "Ding",
        ["玻璃"] = "Glass",
        ["和弦"] = "Chord",
        ["批准"] = "Approve",
        ["拒绝"] = "Reject",
        ["去回答"] = "Answer",
        ["跟随系统"] = "Follow system",
        ["{0} 助手"] = "{0} Assistant",
        ["你好，我是 {0}。\n点屏幕上的悬浮气泡打开这里，直接打字聊天。命令执行前我会先征求你的同意。"] =
            "Hi, I'm {0}.\nClick the floating bubble to open this window and just type to chat. I'll ask for your approval before running any commands.",
        ["＋ 创建自定义供应商"] = "＋ Create custom provider",
        ["执行过程"] = "Process",
        // 思考行 / Token 行
        ["思考"] = "Thinking",
        ["思考 · 持续了 {0} 秒"] = "Thinking · {0}s",
        ["Token 本轮 输入 {0} / 输出 {1} / 合计 {2} ｜ 应用累计 {3}"] = "Tokens this turn in {0} / out {1} / total {2} ｜ app total {3}",
        ["添加附件"] = "Add attachments",
        // 审批提要：工具
        ["查看工作区文件"] = "View a workspace file",
        ["查看文件「{0}」的内容"] = "View contents of \"{0}\"",
        ["往文件追加内容"] = "Append to a workspace file",
        ["往文件「{0}」末尾追加内容"] = "Append to \"{0}\"",
        ["写入工作区文件"] = "Write a workspace file",
        ["写入文件「{0}」"] = "Write file \"{0}\"",
        ["查看工作区目录"] = "List the workspace directory",
        ["查看目录「{0}」"] = "List directory \"{0}\"",
        ["读取 {0} 的长期记忆"] = "Read {0}'s long-term memory",
        ["更新 {0} 的长期记忆"] = "Update {0}'s long-term memory",
        ["创建一个新技能"] = "Create a new skill",
        ["创建新技能「{0}」"] = "Create skill \"{0}\"",
        ["从技能市场安装技能"] = "Install a skill from the market",
        ["安装技能「{0}」"] = "Install skill \"{0}\"",
        ["加载/查看可用技能"] = "Load / list available skills",
        ["添加 MCP 插件"] = "Add an MCP plugin",
        ["添加 MCP 插件「{0}」"] = "Add MCP plugin \"{0}\"",
        ["查看已安装的 MCP 插件"] = "List installed MCP plugins",
        ["派出一个工作助手"] = "Spawn a worker agent",
        ["派出工作助手「{0}」"] = "Spawn worker \"{0}\"",
        ["给工作助手分派任务"] = "Assign a task to a worker",
        ["给工作助手发消息"] = "Message a worker",
        ["给所有工作助手广播消息"] = "Broadcast to all workers",
        ["查看协作状态"] = "Check team status",
        ["打开网页"] = "Open a web page",
        ["打开网页 {0}"] = "Open web page {0}",
        ["向你提问"] = "Ask you a question",
        ["调用 {0}"] = "Invoke {0}",
        ["调用 {0}（{1}）"] = "Invoke {0} ({1})",
        // 审批提要：命令
        ["在终端执行命令"] = "Run a terminal command",
        ["打开 {0}"] = "Open {0}",
        ["查看目录 {0}"] = "List directory {0}",
        ["查看文件 {0}"] = "View file {0}",
        ["创建文件夹 {0}"] = "Create folder {0}",
        ["新建文件 {0}"] = "Create file {0}",
        ["删除 {0}"] = "Delete {0}",
        ["移动/重命名 {0}"] = "Move/rename {0}",
        ["复制 {0}"] = "Copy {0}",
        ["查看当前时间"] = "Check the current time",
        ["查看当前目录"] = "Show current directory",
        ["输出一段文字"] = "Print a piece of text",
        ["从网络获取 {0}"] = "Fetch from the web: {0}",
        ["执行 git {0}"] = "Run git {0}",
        ["运行脚本 {0}"] = "Run script {0}",
        ["搜索 {0}"] = "Search for {0}",
        ["切换目录 {0}"] = "Change directory to {0}",
        ["修改文件权限 {0}"] = "Change permissions of {0}",
        ["在终端执行：{0}"] = "In terminal: {0}",
        ["在终端开始执行：{0}（之后还有后续步骤）"] = "In terminal, starting with: {0} (more steps follow)",
        ["在终端执行：{0}"] = "In terminal: {0}",
        ["气泡图标"] = "Bubble icon",
        ["气泡位置"] = "Bubble position",
        ["悬浮气泡的个性化显示。"] = "Personalize the floating bubble.",
        ["管理模型供应商，配置后即可在底部切换使用。"] = "Manage model providers, switch from the bottom bar.",
        ["供应商名称"] = "Provider name",
        ["API 地址（OpenAI 兼容端点）"] = "API endpoint (OpenAI-compatible)",
        ["模型清单（逗号分隔，第一个为默认）"] = "Models (comma-separated, first is default)",
        ["删除此供应商"] = "Delete this provider",
        ["＋ 添加供应商"] = "＋ Add provider",
        ["快速添加（点选模板）"] = "Quick add (templates)",
        ["模型设置"] = "Models",
        ["外观"] = "Appearance",
        ["常规"] = "General",
        // 外观页 / 常规页遗漏
        ["重置到默认位置（右上角）"] = "Reset to default position (top-right)",
        ["充值到默认位置（右上角）"] = "Reset to default position (top-right)",
        ["例如：回答尽量简短；优先用表格展示数据…"] = "e.g. Keep answers brief; prefer tables for data…",
        ["个性化已保存并生效（模型指令已热更新，对话上下文保留）。"] = "Personalization saved & applied (model instructions hot-updated, conversation preserved).",
        ["个性化已应用到界面；模型指令应用失败：{0}"] = "Personalization applied to UI; failed to update model instructions: {0}",
        // 模型页
        ["创建自定义供应商"] = "Create custom provider",
        ["（使用中）"] = " (in use)",
        // 设置窗口 Hint 消息
        ["已保存并热应用：当前对话上下文已迁移到新的模型客户端。"] = "Saved & applied: conversation migrated to the new model client.",
        ["保存失败：{0}"] = "Save failed: {0}",
        ["供应商名称「{0}」重复，请改名后再保存。"] = "Provider name \"{0}\" is duplicated; rename it before saving.",
        ["存在未命名的供应商。"] = "There is an unnamed provider.",
        ["已添加「{0}」，填写 API Key 后点「保存并应用」。（模型管理功能不校验端点可用性，配置错误会在对话时报错）"] = "Added \"{0}\". Fill in the API Key and click \"Save & apply\". (Endpoint availability is not verified; misconfiguration surfaces as chat errors.)",
        ["已删除「{0}」（点「保存并应用」生效）。"] = "Deleted \"{0}\" (takes effect when you click \"Save & apply\").",
        ["至少保留一个供应商（可改为编辑现有项）。"] = "Keep at least one provider (edit the existing one instead).",
        ["气泡位置已重置。"] = "Bubble position has been reset.",
        ["重置失败：{0}"] = "Reset failed: {0}",
        ["有未保存的修改已丢弃（可重新打开编辑）。"] = "Unsaved changes were discarded.",
        ["新供应商"] = "New provider",
        // 主窗其他
        ["切换供应商失败：{0}"] = "Provider switch failed: {0}",
        ["界面渲染异常（已拦截）：{0}"] = "UI render error (intercepted): {0}",
        ["开启新对话"] = "New chat",
        ["停止当前任务"] = "Stop the current task",
        ["已排队 {0} 条，回合结束后自动发送"] = "{0} queued; will send after the current turn",
        ["开启后操作自动批准，重启复位"] = "Auto-approve actions while on; resets on restart",
        ["（启动中…）"] = "（Starting…）",
        ["管理模型…"] = "Manage models…",
        ["模型"] = "Model",
        ["操作"] = "action",
        ["完全访问·开"] = "Full access · on",
    };

    /// <summary>应用语言设置（auto/zh/en）。auto 跟随系统 UI 文化。</summary>
    public static void SetLanguage(string setting)
    {
        Setting = string.IsNullOrWhiteSpace(setting) ? "auto" : setting;
        var prev = Lang;
        if (Setting == "auto")
        {
            var sys = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            Lang = sys == "zh" ? "zh" : "en";
        }
        else
        {
            Lang = Setting;
        }
        if (Lang != prev)
        {
            AgentBrook.Agent.Infrastructure.CoreStrings.Lang = Lang;   // Core 日志语言同步
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>翻译：中文原文为键，args 填充 {0}… 占位。</summary>
    public static string T(string zh, params object[] args)
    {
        var s = Lang == "en" ? En.GetValueOrDefault(zh, zh) : zh;
        return args.Length > 0 ? string.Format(s, args) : s;
    }
}

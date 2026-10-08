<div align="center">

# AgentBrook Helper

**跨平台桌面 AI 智能体 —— 一个悬浮气泡，打开即是一个完整的智能体运行时。**

<img src="assets/banner.webp" alt="AgentBrook Helper —— 基于 Microsoft Agent Framework 的桌面 AI 智能体" width="100%">

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](./LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/download)
[![Platform](https://img.shields.io/badge/platform-macOS%20%7C%20Windows-000000.svg)]()

[English](./README.md) · 简体中文

</div>

---

## 这是什么

AgentBrook Helper 是一个真正跑在你机器上的 AI 智能体的桌面客户端。屏幕角落躺着一个可拖拽的小气泡，双击它，对话窗就打开了。窗口背后是一套完整的智能体运行时——长期记忆、能自我扩展的技能系统、MCP 服务器、文件与命令行工具、人机协同审批，以及多智能体团队协作。

它构建在 **.NET 10** 与 **Microsoft Agent Framework (MAF)** 之上，通过 OpenAI 兼容端点接入任意模型供应商（DeepSeek、OpenAI、Kimi、智谱 GLM、OpenRouter……）。

它不是套在 API 外面的聊天壳子。桌面外壳只是一层薄胶水——大约 300 行——底下是与宿主无关的智能体核心，控制台客户端共用同一份。

## 界面预览

| 常驻气泡 · 启动问候 | 审批后才执行 |
|---|---|
| <img src="assets/ui-bubble-greeting.png" width="300"> | <img src="assets/ui-approval.png" width="300"> |
| **处理中：齿轮自转 + 工具轨道** | **完整对话：Markdown 表格回复** |
| <img src="assets/ui-bubble-gear.png" > | <img src="assets/ui-chat.png" width="480"> |

**设置**：主工作空间 / 助手身份 / 提示音 / 开机自启动，一站式配置。

<img src="assets/ui-settings.png" width="480">


## 为什么需要它

智能体运行时本身能力很强，但它们住在终端里。终端是你会「走过去用」的工具，而桌面助手是「已经在那儿」的那一个。

差距不在模型质量，而在距离。所以这个项目做的事是：把一颗智能体内核放到它该待的地方——始终可见、从不碍事，并且在动手做任何要紧事之前先问你。

## 你会得到什么

<img src="assets/features.webp" alt="三个核心结果：常驻气泡、完整运行时、审批后才执行" width="100%">

### 常驻气泡

一个可拖拽、始终置顶、不抢占焦点的气泡。空闲时呼吸，思考时旋转，需要你的时候变成琥珀色，并自动弹出对话窗。关掉窗口只是隐藏，气泡继续待着。输入框支持 Ctrl/⌘+V 直接粘贴剪贴板截图为附件。

### 气泡背后的完整运行时

气泡打开的是一个会记住事情的智能体。`workspace/memory/` 下的 Markdown 记忆文件每轮都会注入上下文，模型自己也能写入。技能从 `SKILL.md` 渐进式加载——并且智能体可以把**它实际验证过**的工作流沉淀成新技能。MCP 服务器经 stdio 接入。项目功能让它能把一个目标拆给多个各自拥有工作区的工作智能体，并通过消息总线协调。

### 不经过你，什么都不会执行

命令行执行、技能安装、MCP 服务器注册、派生工作智能体——全都会先停下来，弹一张审批卡片。卡片顶部是「人话提要」，原始指令折叠在下层。你可以批准、拒绝并附上理由（理由会回传给模型让它重新规划），或者开启本次会话的完全访问——重启即复位。

### 随时打断、排队与多会话

回合进行中发送按钮变成停止键（按 Esc 同样有效），已完成的部分保留；忙碌时继续输入会自动排队，回合结束后逐条发送。多会话由顶栏切换器管理：会话按首条消息自动命名，「新对话」归档旧会话而不是清空历史，切换后完整回放历史消息、上下文与前情摘要一起跟随。任何会话都能一键把成果沉淀为 `session-` 前缀技能——其他会话 `load_skill` 即可复用这些结论与产出，会话删除后技能依然保留。

### 上下文按需加载

固定 token 成本被刻意压低：MCP 工具的 JSON Schema 不整体常驻上下文，主索引只列「工具名 + 一句话用途」，模型按需 `mcp_call` 调用、`mcp_tool_help` 查参数；复杂子任务（调研、长报告、批量整理）可委派给拥有独立上下文的子智能体 `delegate_task`，只有最终结论回到主对话；子智能体与会话摘要走模型清单中的轻量模型（分级用模，可在 appsettings 配置）；历史超预算时自动把早期对话浓缩为滚动摘要注入头部，长会话与重启恢复都保持精干。

## 工作方式

三个宿主共用一颗内核。`AgentBrook.Agent.Core` 对外暴露 `BrookAgent.RunAsync`，产出一串事件流——状态变化、文本增量、工具开始/完成、Token 用量、回合结束。宿主消费这条流，各自决定怎么渲染。

任何需要真人的环节都经由 `IBrookInteraction` 接口：审批请求与 `ask_user` 都会挂起内核、等待宿主。控制台客户端打印提示，桌面客户端弹出卡片。正是这一层抽象，让两套完全不同的 UI 不必各自复制一遍智能体逻辑。

| 模块 | 职责 |
|---|---|
| `AgentBrook.Agent.Core` | 智能体、工具、记忆、技能、MCP、团队编排 |
| `AgentBrook.Agent` | 控制台宿主（REPL） |
| `AgentBrook.Helper` | 桌面宿主（Avalonia）—— macOS 有打包脚本，Windows 可直接运行 |

## 快速开始

前置条件：[.NET 10 SDK](https://dotnet.microsoft.com/download)。在 macOS 上开发与测试；桌面客户端同样可在 Windows 上运行。

```bash
git clone https://github.com/ChenGan666/AgentBrook-Helper.git
cd AgentBrook-Helper
```

**1. 配置模型供应商。** 启动后在「设置 → 模型设置」里填写（推荐），或编辑 `src/AgentBrook.Helper/appsettings.json` 填入 Key。仓库内该文件已脱敏，首次运行会派生出默认供应商。

**2. 构建并启动。**

```bash
cd src
dotnet build AgentBrook.slnx
```

**macOS** —— 打包并签名 `.app`：

```bash
cd AgentBrook.Helper
./build-mac-app.sh        # 生成并签名 macos/AgentBrook.Helper.app
open macos/AgentBrook.Helper.app
```

**Windows** —— 直接运行桌面客户端（无需打包脚本）：

```bat
cd AgentBrook.Helper
dotnet run
```

**3. 屏幕右上角出现悬浮气泡。** 双击它，开始对话。

<details>
<summary>或者改用控制台宿主</summary>

```bash
cd src/AgentBrook.Agent && dotnet run
```

</details>

> 三个项目均通过 Avalonia 桌面包面向 `net10.0`，未涉及任何平台专属 API。macOS 附带打包脚本；Windows 上直接 `dotnet run` 即可运行。Linux 理论上同样可用但尚未验证——欢迎反馈与贡献。

## 配置说明

```jsonc
{
  "LLM": {
    "BaseUrl": "https://api.deepseek.com/v1",
    "ApiKey": "sk-请填入你自己的APIKey",   // 或环境变量 DEEPSEEK_API_KEY / AGENTBROOK__LLM__APIKEY
    "DefaultModel": "deepseek-flash",
    "Models": ["deepseek-flash", "deepseek-v4-pro"]
  },
  "Agent": {
    "Instructions": "…",                 // 能力边界 + 工作准则
    "WorkspaceRoot": "../AgentBrook.Agent/workspace",
    "RequireApprovalTools": ["run_command", "create_skill", "install_skill", "mcp_add_server", "spawn_worker"]
  },
  "MCP": { "Servers": [ /* filesystem 已启用；fetch 与 github 为示例 */ ] }
}
```

运行期数据自动生成，不入版本库：

| 路径 | 内容 |
|---|---|
| `workspace/session.json` | 会话存档，重启恢复 |
| `workspace/models.json` | 模型供应商配置 |
| `workspace/settings.json` | 助手名字、气泡图标、语言、默认提示词 |
| `workspace/memory/`、`skills/`、`attachments/`、`projects/` | 记忆、技能、附件、团队项目 |

## 内置工具

核心内置约 55 个工具，按能力面分组：

| 能力面 | 工具 |
|---|---|
| **文件** | `list_dir` `read_file` `write_file` `append_file` —— 全部沙箱在 workspace 内 |
| **命令行** | `run_command` —— 超时、输出截断、拒绝高危模式，需审批 |
| **记忆** | `memory_read` `memory_write` `memory_append` |
| **技能** | `list_skills` `load_skill` `create_skill` `install_skill` `skill_market` |
| **MCP** | `mcp_list_servers` `mcp_add_server` —— 外加已配置服务器暴露的全部工具 |
| **团队** | `start_project` `spawn_worker` `assign_task` `send_message` `broadcast_to_workers` `list_workers` `read_shared` `check_messages` |
| **交互** | `ask_user` —— 挂起当前回合，等一个真实回答 |

## 仓库结构

```
AgentBrook-Helper/
├── AgentBrook.slnx                  ← 解决方案
├── assets/                          ← README 视觉资产
└── src/
    ├── AgentBrook.Agent.Core/       ← 智能体内核（所有宿主共用）
    │   ├── BrookAgent.cs            ← 事件驱动智能体，RunAsync 产出 BrookEvent 流
    │   ├── IBrookInteraction.cs     ← HITL 抽象（审批 / ask_user）
    │   ├── Infrastructure/          ← 工作区沙箱、AgentFactory、ProviderSettings
    │   ├── Memory/  Skills/  Tools/  Team/  Mcp/  Configuration/
    ├── AgentBrook.Agent/            ← 控制台宿主（REPL）
    └── AgentBrook.Helper/           ← 桌面宿主（Avalonia）；macOS + Windows
        ├── BubbleWindow.axaml(.cs)  ← 悬浮气泡：状态动画、拖拽、位置记忆
        ├── MainWindow.axaml(.cs)    ← 对话窗：聊天流、状态条、审批卡片
        ├── ModelSettingsWindow      ← 设置窗口：常规 / 外观 / 模型设置
        ├── MarkdownView.cs          ← 自研 Markdown → Avalonia 渲染器
        ├── AssistantController.cs   ← 气泡与对话窗共享的控制器
        ├── UiPrefs.cs               ← UI 偏好持久化
        └── build-mac-app.sh         ← macOS .app 打包签名脚本
```

## 文档


## 安全注意事项

- **命令行工具具备真实系统能力。** 已用超时、输出截断、高危拒绝与审批兜底；生产环境建议容器化或以受限账户运行。
- **文件工具沙箱。** 所有路径解析后必须位于 workspace 内；二进制文件拒绝以文本读取。
- **切勿提交真实 API Key。** 仓库内 `appsettings.json` 已脱敏，建议使用环境变量注入。

## 许可证

[MIT](./LICENSE)

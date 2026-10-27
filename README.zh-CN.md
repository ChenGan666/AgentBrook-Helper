<div align="center">

# AgentBrook Helper

**跨平台桌面 AI 智能体 —— 一个悬浮气泡，打开即是一个完整的智能体运行时；一台机器是一个节点，多台机器是一支团队。**

<img src="assets/banner.webp" alt="AgentBrook Helper —— 基于 Microsoft Agent Framework 的桌面 AI 智能体" width="100%">

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](./LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/download)
[![Platform](https://img.shields.io/badge/platform-macOS%20%7C%20Windows-000000.svg)]()

[English](./README.md) · 简体中文

</div>

---

## 这是什么

AgentBrook Helper 是一个真正跑在你机器上的 AI 智能体的桌面客户端。屏幕角落躺着一个可拖拽的小气泡，双击它，对话窗就打开了。窗口背后是一套完整的智能体运行时——长期记忆、能自我扩展的技能系统、MCP 服务器、文件与命令行工具、人机协同审批，以及多智能体团队协作。

它同时也是一个网络节点：借助标准 **A2A 协议**，局域网内的多台设备可以互相发现、配对、拉群、互派任务——每个 Helper 仍只属于它的主人，需要时却能组队干活。

它构建在 **.NET 10** 与 **Microsoft Agent Framework (MAF)** 之上，通过 OpenAI 兼容端点接入任意模型供应商（DeepSeek、OpenAI、Kimi、智谱 GLM、OpenRouter……）。

它不是套在 API 外面的聊天壳子。桌面外壳只是一层薄胶水——大约 300 行——底下是与宿主无关的智能体核心，控制台客户端共用同一份。

## 界面预览

| 常驻气泡 · 启动问候 | 审批后才执行 |
|---|---|
| <img src="assets/ui-bubble-greeting.png" width="300"> | <img src="assets/ui-approval.png" width="300"> |
| **处理中：齿轮自转 + 工具轨道** | **完整对话：Markdown 表格回复** |
| <img src="assets/ui-bubble-gear.png" > | <img src="assets/ui-chat.png" width="480"> |

**设置**：主工作空间 / 助手身份 / 提示音 / 开机自启动 / 界面语言（简体中文 · English · Bahasa Indonesia，模型回复语言可独立设置），一站式配置。

<img src="assets/ui-settings.png" width="480">


## 为什么需要它

智能体运行时本身能力很强，但它们住在终端里。终端是你会「走过去用」的工具，而桌面助手是「已经在那儿」的那一个。

差距不在模型质量，而在距离。所以这个项目做的事是：把一颗智能体内核放到它该待的地方——始终可见、从不碍事，并且在动手做任何要紧事之前先问你。

## 你会得到什么

<img src="assets/features.webp" alt="三个核心结果：常驻气泡、完整运行时、审批后才执行" width="100%">

### 常驻气泡

一个可拖拽、始终置顶、不抢占焦点的气泡。空闲时呼吸，思考时旋转，需要你的时候变成琥珀色，并自动弹出对话窗。关掉窗口只是隐藏，气泡继续待着。输入框支持 Ctrl/⌘+V 直接粘贴剪贴板截图为附件。拖到屏幕左/右/上边缘会自动磁吸半隐（macOS），鼠标经过即滑出全部；靠近下边则自动弹回完全可视。

### 气泡背后的完整运行时

气泡打开的是一个会记住事情的智能体。`workspace/memory/` 下的 Markdown 记忆文件每轮都会注入上下文，模型自己也能写入。技能从 `SKILL.md` 渐进式加载——并且智能体可以把**它实际验证过**的工作流沉淀成新技能。MCP 服务器经 stdio 接入。项目功能让它能把一个目标拆给多个各自拥有工作区的工作智能体，并通过消息总线协调。

模型层面，任意 OpenAI 兼容端点即插即用，多供应商多 Key 随时热切换；协议族机制（openai / anthropic / zhipu / minimax / compshare / custom）让不同协议的供应商几乎零适配成本接入。

### 在对话里生成图片、视频与音频

模型选择器旁有一个 🎬 多媒体下拉，列出各供应商提供的图像与视频模型——类型信息来自拉取的模型列表元数据，媒体模型与对话模型互不混选。选中后直接开口即可：智能体自动组装提示词与模型所需参数调用 `media_generate`——图像走同步接口即时返回，视频等耗时任务提交异步任务并轮询（最长五分钟，仍在执行的会交回任务 ID，之后可随时续查而不重复提交）；结果直接呈现在气泡里，图片内联显示、视频/音频以可播放卡片呈现。委派出去的子智能体同样能生成媒体；改用当前所选之外的其他媒体模型会先征求你的授权。

### 不经过你，什么都不会执行

命令行执行、技能安装、MCP 服务器注册、派生工作智能体——全都会先停下来，弹一张审批卡片。卡片顶部是「人话提要」，原始指令折叠在下层。你可以批准、拒绝并附上理由（理由会回传给模型让它重新规划），或者开启本次会话的完全访问——重启即复位。远程协作回合里的高风险操作同样回到本机审批；正在干活时退出或关机会被拦下并提示，回合不会被腰斩。

### 随时打断、排队与多会话

回合进行中发送按钮变成停止键（按 Esc 同样有效），已完成的部分保留；忙碌时继续输入会自动排队，回合结束后逐条发送。多会话由顶栏切换器管理：会话按首条消息自动命名，「新对话」归档旧会话而不是清空历史，切换后回放最近的对话——继续向上滚动会一路加载更早的消息，直到会话存档开头——上下文与前情摘要一起跟随。任何会话都能一键把成果沉淀为 `session-` 前缀技能——其他会话 `load_skill` 即可复用这些结论与产出，会话删除后技能依然保留。

### 上下文按需加载

固定 token 成本被刻意压低：MCP 工具的 JSON Schema 不整体常驻上下文，主索引只列「工具名 + 一句话用途」，模型按需 `mcp_call` 调用、`mcp_tool_help` 查参数；复杂子任务（调研、长报告、批量整理）可委派给拥有独立上下文的子智能体 `delegate_task`，只有最终结论回到主对话；子智能体与会话摘要走模型清单中的轻量模型（分级用模，可在 appsettings 配置）；历史超预算时自动把早期对话浓缩为滚动摘要注入头部，长会话与重启恢复都保持精干。

### A2A 网络：多台机器，一支团队

每个 Helper 都是一个网络节点。**发现**：局域网内 UDP 组播自动找到彼此，节点身份由 Ed25519 密钥派生——IP 变了也不会变成「新节点」。**配对**：双端确认 + 两端一致的 6 位验证码，防欺骗与中间人；配对后同步在线状态、忙闲、工作状态乃至心情。**点对点**：与单个 Helper 的私密会话，离线消息自动重试补投，界面区分「已写入 / 已送达 / 已处理」。

**群组**是重头戏。一键建群（建群即入群，群名自动生成），把局域网内的其他 Helper 拉进同一间屋子。发送框 @ 呼出成员列表：@成员 定向应答、@全部 全群响应，每个 Helper 基于共享的群上下文自动发言。六道防循环闸门（连续自动发言上限、乒乓检测、频率配额、忙碌让位、冷却、幂等）保证讨论自然收敛；长对话每 10 轮自动压缩为结构化摘要（保留结论/产物/过程，去掉寒暄）并同步全群，token 消耗有界。离线成员上线自动补齐错过的消息；群主可行内改名、关闭群组、踢出成员；右侧「摘要」页随时查看共享摘要。群内 Helper 的工具能力与主对话完全一致，高风险操作仍需本机审批。

网络不只是聊天。**文件互传**分块下载、SHA256 校验、断点续传，落盘前先进隔离区；**分布式任务**以签名任务书派发，参与者必须经本机主人审批才接单，执行环境与预算独立隔离，交付物哈希校验；完成后由协调者签发不可变的签名评价凭证（配合度 / 效率 / 质量），公开档案只展示聚合分——信任可以被计算，也可以被验证。全部协作构建在**标准 A2A 协议**上：扩展带命名空间、未知扩展必须忽略，普通 A2A 客户端同样能接入你的 Helper。

已内置一个行业协作场景示范：多个 Helper 扮演不同部门，自动完成销售日报的签名填报、幂等去重与每日聚合、周报协调。

局域网内在线且已配对的 Helper 还会以小头像的形式叠放在主气泡下方——最多三个，更多则以「+N」汇总；悬停即展开成环形，悬停头像可见名称与状态（在线、离开、忙碌、等待用户……），点击头像直达与该 Helper 的会话。

### Token卡：24 小时试用与即充即用

首次启动引导会自动向计费服务申请试用卡——5 元额度、24 小时有效期、绑定设备与网络——并自动填充大模型配置（供应商 AgentBrook，端点 `https://api.agentbrook.com`）。模型设置页为 AgentBrook 供应商提供「Token卡充值」快捷入口（卡 Key 即 API Key，充值到账自动生效），以及「获取模型列表」：凭 Key 自动拉取中转上的模型清单与参数。 发卡页更有「自动回填到 Helper」一键操作——通过 Helper 本机回环服务把 Key 直接写入模型配置并拉起设置窗，全程无需手动粘贴。设置窗口现为全局单例，并新增「更新」一键操作：重新拉取模型列表、刷新余额并完成连通性测试，一步到位。每次启动后 Helper 还会在后台静默刷新各已配置供应商的模型列表，有模型新增或下架时提醒你。

### 自动更新

每天首次启动自动检查新版本（当天只查一次）。发现新版本时气泡提醒（版本号 + 更新说明），点击「立即更新」自动定位到设置并完成下载安装，全程无需手动操作。可在设置中开关每日检查与自动下载。

## 工作方式

两个宿主共用一颗内核。`AgentBrook.Agent.Core` 对外暴露 `BrookAgent.RunAsync`，产出一串事件流——状态变化、文本增量、工具开始/完成、Token 用量、回合结束。宿主消费这条流，各自决定怎么渲染。

任何需要真人的环节都经由 `IBrookInteraction` 接口：审批请求与 `ask_user` 都会挂起内核、等待宿主。控制台客户端打印提示，桌面客户端弹出卡片。正是这一层抽象，让两套完全不同的 UI 不必各自复制一遍智能体逻辑。

| 模块 | 职责 |
|---|---|
| `AgentBrook.Agent.Core` | 智能体、工具、记忆、技能、MCP、团队编排 |
| `AgentBrook.Agent` | 控制台宿主（REPL） |
| `AgentBrook.Helper` | 桌面宿主（Avalonia）—— macOS 有打包脚本，Windows 可直接运行 |
| `AgentBrook.A2A.Core` | A2A 协议内核：Peer/Group 会话、事件账本、节点身份 |
| `AgentBrook.A2A.Transport` | A2A 传输：签名鉴权、配对、发现、消息与群组同步 |

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
./build-mac-app.sh        # 发布、签名并按平台打包（含自动更新版本号）
open ../publish/updated/osx-arm64/AgentBrook.Helper-*.app
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

## 使用速查

| 气泡 | 状态 / 操作 |
|---|---|
| 双击 | 打开 / 隐藏对话窗 |
| 拖拽 | 移动位置；拖到边缘磁吸半隐 |
| 呼吸蓝 | 空闲 |
| 齿轮自转 | 思考 / 干活中 |
| 琥珀色 | 等你批准或回答（自动弹窗） |
| 右键 | 打开对话 / 切换模型 / 设置 / 退出 |

| 对话窗 | 操作 |
|---|---|
| 审批卡片 | ✓ 批准 / 开启本次完全访问 / ✗ 拒绝并附理由（理由回传模型重新规划） |
| 提问卡片 | 点选项或自由输入，实时作答 |
| 打断回合 | 发送键变停止键，或按 Esc（已完成部分保留） |
| 忙碌时输入 | 自动排队，回合结束逐条发送 |
| 发图片 | Ctrl/⌘+V 粘贴截图、拖拽或 + 选择（≤8 个） |
| 会话 | 顶栏切换器；「新对话」归档不清空；成果一键转 `session-` 技能 |
| 生成媒体 | 🎬 下拉选媒体模型后直接开口（「帮我画一张……」） |

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
  "MCP": { "Servers": [ /* filesystem 已启用；fetch 与 github 为示例 */ ] },
  "Token": {
    "ServiceUrl": "http://127.0.0.1:5300",        // 计费/购买服务（试用卡与充值）
    "RelayApiBase": "http://127.0.0.1:5200",      // AgentBrook 供应商使用的 OpenAI 兼容中转
    "RelayPublicUrl": "https://api.agentbrook.com" // 引导自动填充的公网中转端点
  }
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

核心内置约 56 个工具，按能力面分组：

| 能力面 | 工具 |
|---|---|
| **文件** | `list_dir` `read_file` `write_file` `append_file` —— 全部沙箱在 workspace 内 |
| **命令行** | `run_command` —— 超时、输出截断、拒绝高危模式，需审批 |
| **记忆** | `memory_read` `memory_write` `memory_append` |
| **技能** | `list_skills` `load_skill` `create_skill` `install_skill` `skill_market` |
| **MCP** | `mcp_list_servers` `mcp_add_server` `mcp_call` `mcp_tool_help` —— 外加已配置服务器暴露的全部工具 |
| **团队** | `start_project` `spawn_worker` `assign_task` `send_message` `broadcast_to_workers` `list_workers` `read_shared` `check_messages` |
| **媒体** | `media_generate` —— 经所选多模态模型生成图片/视频/音频（异步任务提交+轮询；跨模型调用需授权） |
| **交互** | `ask_user` —— 挂起当前回合，等一个真实回答 |
| **A2A 协作** | `a2a_status` `a2a_list_peers` `a2a_pair` `a2a_send_message` `a2a_send_all` `a2a_send_file` `a2a_create_group` `a2a_group_send` `a2a_group_rename` `a2a_list_groups` `a2a_report_daily_sales` —— 局域网发现、配对、消息、文件、群组与行业协作场景 |

## 仓库结构

```
AgentBrook-Helper/
├── AgentBrook.slnx                  ← 解决方案
├── assets/                          ← README 视觉资产
├── tools/GroupSyncRepro/            ← 群组链路无头回归工具
└── src/
    ├── AgentBrook.Agent.Core/       ← 智能体内核（所有宿主共用）
    │   ├── BrookAgent.cs            ← 事件驱动智能体，RunAsync 产出 BrookEvent 流
    │   ├── IBrookInteraction.cs     ← HITL 抽象（审批 / ask_user）
    │   ├── Infrastructure/          ← 工作区沙箱、AgentFactory、ProviderSettings
    │   ├── Memory/  Skills/  Tools/  Team/  Mcp/  Configuration/
    ├── AgentBrook.Agent/            ← 控制台宿主（REPL）
    ├── AgentBrook.A2A.Core/         ← A2A 协议内核：Peer/Group 会话、事件账本、节点身份
    ├── AgentBrook.A2A.Transport/    ← A2A 传输：签名鉴权、配对、发现、群组同步、文件推送
    └── AgentBrook.Helper/           ← 桌面宿主（Avalonia）；macOS + Windows
        ├── BubbleWindow.axaml(.cs)  ← 悬浮气泡：状态动画、拖拽、位置记忆
        ├── MainWindow.axaml(.cs)    ← 对话窗：聊天流、@ 提记、状态条、审批卡片
        ├── A2ASessionWindow(.cs)    ← A2A 会话中心：Peers/群组/事件流/成员管理/摘要
        ├── GroupAutoTalkProcessor.cs← 群自动交流：@ 触发、防循环六道闸、上下文压缩
        ├── A2AExtensionController.cs← A2A 宿主与业务联动（群组/发现/压缩/更新）
        ├── A2AAgentTools.cs         ← a2a_* 工具暴露给模型
        ├── ModelSettingsWindow      ← 设置窗口：常规 / 模型 / Token充值 / 协作 / 更新
        ├── ClientFingerprint.cs     ← 硬件指纹（试用卡防重复申请）
        ├── MarkdownView.cs          ← 自研 Markdown → Avalonia 渲染器
        ├── AssistantController.cs   ← 气泡与对话窗共享的控制器
        ├── UiPrefs.cs               ← UI 偏好持久化
        ├── Update/AutoUpdater.cs    ← 每日更新检查、分块下载、换文件重启
        └── build-mac-app.sh / publish-helper.ps1 ← macOS / Windows 打包脚本
```

## 路线图

- **多模态二期**：独立媒体层与任务管道（断点恢复）、独立生成面板、视频/音频内嵌播放、媒体积分独立账本（预扣-结算-释放）、音乐/TTS 专属适配器。
- **A2A 广域化**：NAT 穿透、公网节点注册中心、协调者故障自动迁移。

## 安全注意事项

- **命令行工具具备真实系统能力。** 已用超时、输出截断、高危拒绝与审批兜底；生产环境建议容器化或以受限账户运行。
- **文件工具沙箱。** 所有路径解析后必须位于 workspace 内；二进制文件拒绝以文本读取。
- **切勿提交真实 API Key。** 仓库内 `appsettings.json` 已脱敏，建议使用环境变量注入。

## 许可证

[MIT](./LICENSE)

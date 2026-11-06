# 19 · Helper 插件开发指南（面向第三方）

> 面向第三方开发者的 Helper 插件开发规范与接口参考。
> 契约工程：`src/AgentBrook.Plugin.Abstractions`（NuGet 引用，随宿主演进）；参考实现：`src/AgentBrook.Plugin.PrivacyMasking`（隐私脱敏插件，1.0.14）。
> 设计背景见 [doc/18](18-helper-plugin-system-design.md)；发布合规见 §9 检查单。
>
> **⚠️ 契约依据注记（2026-11-03，IM Pipeline v4/v4.1）**：本文的"启停/卸载/重装**即时生效**"（§1）与"停用后**零残留活动线程**"（§3/合规检查单）适用于当前**简单插件**（装饰器/设置类）的协作式停止——可信 ALC 只承诺依赖隔离与协作式生命周期，**不承诺即时停止与零残留**（不合作线程无法强杀）。IM 通道类**常驻插件**走新契约：活动租约、停用五步语义（超时进 Failed）、进程外受限 worker 模式，见 [doc/IM pipeline/im-pipeline-design.md](IM%20pipeline/im-pipeline-design.md) §15（待实现；落地前 IM 插件开发以该文档为准，本文后续版本将合并两套契约）。

---

## 0. 开发前必读：强制规范（MUST，非建议）

> **插件开发必须严格遵循本指南。** 下列 8 条是红线——违反任一条会导致**插件加载失败、宿主进程崩溃或线上安装失败**（每一条都有真机事故对应，详见 §11），此类插件不予上架。

| # | 红线 | 违反后果 |
|---|---|---|
| 1 | 禁止自建 `HttpClient`，网络请求一律走 `host.Download` | 违反下载服务标准（超时/进度/原子落盘集中维护），安装审计不通过 |
| 2 | `ShutdownAsync` 必须幂等、尽快返回，停用后**零残留**（无线程/定时器/原生会话） | 卸载失败（DLL 被占用）、内存常驻、进程退出被拖住 |
| 3 | **不得**随包分发宿主持有的契约程序集（`AgentBrook.Plugin.Abstractions` 等）；插件内对其引用版本必须是 `1.0.0.0` | 加载时报 `Could not load ... Version=x.y.z`，`GetTypes()` 失败 |
| 4 | 契约对象**一律属性式**（`init` 属性）；禁止"位置参数 record + 默认值"做演进 | 后续加参数=位置破坏，源码级/二进制级双重 breaking |
| 5 | 新增契约类型只能落在**被依赖方**程序集（最底层），禁止反向引用 | 程序集循环依赖，编译不过 |
| 6 | 原生库必须在 `deps.json` 声明 `runtimes/{rid}/native`，且**同进程原生运行时版本与既有插件统一** | `ExecutionEngineException` / native 崩溃（版本冲突，见 §11.4） |
| 7 | 模型/数据包由**服务端统一发布**并在插件内做恒定 SHA-256 校验；禁止运行时外链第三方源 | 供应链不可控、校验漂移、离线/墙内不可用 |
| 8 | 发布包 `index.json` 的 `sha256`/`size` 必须由**实际 zip** 计算（用 `rebuild-plugin-index.py`） | 用户端"插件包 SHA-256 校验失败"，安装率归零 |

红线 3/4/5/6 的完整取证、复现方法与检查命令见 **§11 契约、依赖与原生库**；红线 8 的操作流程见 **§8 打包与发布**。

---

## 1. 概述

Helper 的能力扩展以**插件**形式提供：每个插件是独立 ClassLib（net10.0），编译为 DLL 后由发布工具打成按 RID 的 zip 包，上传到插件服务器；用户在「设置 → 插件管理」中一键安装。宿主以**可收集 AssemblyLoadContext** 隔离加载插件——启停、卸载、重装**即时生效，无需重启 Helper**。

插件与宿主的边界：

- **宿主提供**：加载/卸载/启停生命周期、设置持久化、统一下载服务、日志出口、显示层一次性还原、回合遥测查询
- **插件提供**：能力实现（对话管线装饰、设置页声明、具名操作、文本还原、图标与元数据）
- **插件禁止**：自建 HttpClient（用宿主下载服务）、访问他插件目录、后台常驻线程（停用后须归零）

---

## 2. 快速开始（最小插件）

```csharp
// 1) 引用 AgentBrook.Plugin.Abstractions（ProjectReference 或 NuGet）
using AgentBrook.Plugin.Abstractions;

// 2) 标注入口 + 实现生命周期
[PluginEntry("hello-world", Icon = "👋")]
public sealed class HelloWorldPlugin : IHelperPlugin
{
    public string Id => "hello-world";
    public string DisplayName => "你好世界";
    public string Version => "1.0.0";
    public string Icon => "👋";

    public async Task InitializeAsync(IPluginHostContext host, CancellationToken ct = default)
    {
        host.Log("hello-world 已初始化");
    }

    public Task ShutdownAsync() => Task.CompletedTask;
}
```

```xml
<!-- 3) csproj：Abstractions 的 PrivateAssets 设 all（宿主已有，不入插件包） -->
<ItemGroup>
  <ProjectReference Include="..\AgentBrook.Plugin.Abstractions\AgentBrook.Plugin.Abstractions.csproj"
                    PrivateAssets="all" ExcludeAssets="runtime" />
</ItemGroup>
```

打包发布见 §8（参考 `tools/plugin-publish/build-privacy-plugin.sh`）。

---

## 3. 生命周期

```
安装（下载→SHA-256→解压→入位）
   └→ InitializeAsync(host)          // 一次；抛异常 = 加载失败，不影响宿主
        ├→ [启用] 装饰管线 / 提供服务
        ├→ [停用] SetEnabledAsync(false) → 装饰链即时移除，实例保留
        ├→ [卸载] ShutdownAsync() → ALC 卸载，DLL 可删除
        └→ [重装] 同卸载
```

- `ShutdownAsync` **必须幂等**并尽快返回：停用后台活动、释放非托管资源
- 停用后**零残留活动线程**（合规检查单第 4 条）
- 热插拔由宿主保证：`Registry` 版本号变化 → 管线宿主重建装饰链

---

## 4. 契约接口参考

### 4.1 IHelperPlugin（必须）

| 成员 | 语义 |
|---|---|
| `string Id` | 唯一 id（服务器目录名 / manifest.id 一致，如 `privacy-masking`） |
| `string DisplayName` | 显示名（插件列表 / 设置标题） |
| `string Version` | SemVer，与 manifest.version 一致 |
| `string Icon` | **图标（v1.1 新增）**：单 emoji 或符号，显示在插件名称前（已装/可用列表、设置对话框标题）。默认 `""` = 无图标 |
| `Task InitializeAsync(IPluginHostContext host, CancellationToken)` | 初始化（读设置/预热/注册能力） |
| `Task ShutdownAsync()` | 停用前调用（幂等、尽快返回） |

```csharp
public string Icon => "🛡";   // 隐私脱敏插件示例
```

### 4.2 IPluginHostContext（宿主注入，InitializeAsync 参数）

| 成员 | 语义 |
|---|---|
| `string WorkspaceRoot` | 工作区根（插件数据解析 `{root}/plugins/{id}/`、自有数据放 `{root}/privacy/` 等自有子目录） |
| `string Language` | 宿主 UI 语言（`zh` / `en` / `id`）——插件用户可见文案按此三语 |
| `IPluginSettingsStore Settings` | 插件设置持久化（Get/SetSettingsJson，值存宿主 state.json） |
| `IPluginDownloadService Download` | **统一下载服务**（§6，插件禁自建 HttpClient） |
| `void Log(string)` | 宿主日志出口（设置→环境检测可查） |
| `void RequestPipelineRebuild()` | 请求重建对话管线（设置影响装饰形态时调用，热生效） |

### 4.3 ISettingsContributor（可选能力：设置页）

声明式设置页 + 具名操作。宿主渲染三标签页基础结构，插件按 **schema** 贡献内容：

```csharp
public string SettingsSchemaJson() => JsonSerializer.Serialize(new
{
    groups = new object[]
    {
        new
        {
            title = "ui.plugins.privacy.basic",          // 组标题：I18n 键（空串 = 不渲染组头）
            items = (object[])
            [
                new { key = "privacy.enabled", type = "toggle", title = "ui.plugins.privacy.master" },
                new { key = "privacy.ner.threshold", type = "slider", title = "…",
                      min = 0.6, max = 0.95, step = 0.05 },
            ],
        },
    };
});

public string GetSettingsJson() => /* 当前值 JSON（键与 schema.key 一致） */;
public void SetSettingsJson(string json) { /* 写真源 + 热更新引擎 */ }

public IReadOnlyList<string> Actions => ["install-model", "model-ready"];
public async Task<PluginActionResult> RunActionAsync(string action, string? argumentsJson = null,
    IProgress<string>? progress = null, CancellationToken ct = default) { /* 具名操作 */ }
```

**item 类型（v1 全集）**：

| type | 字段 | 渲染 |
|---|---|---|
| `toggle` | key, title | 开关（bool） |
| `slider` | key, title, min, max, step | 滑杆（double，吸附 step） |
| `choice` | key, title, choices[] | 下拉（字符串） |
| `action` | action, title | 按钮 → 调 `RunActionAsync(action)`，进度走状态栏 |
| `note` | title | 灰色说明行（纯展示） |
| `model` | title, name, installAction, checkAction, readyAction | **识别模型复合行**：模型名 + 安装模型（就绪后变"已安装"禁用）+ 测试（自检）；就绪查询走 readyAction（Success = 已就绪） |

约定：
- 组 `title` 以 `ui.` 开头视为 **I18n 键**；未知 item 类型渲染为翻译后的标题行（老宿主兼容不裸显键名）
- 写回 `SetSettingsJson` 由宿主在**后台线程**调用——禁止同步等待门闩/引擎（卡死教训，见 §10）
- 影响"是否装饰管线"的开关（如总开关）变化后必须调 `host.RequestPipelineRebuild()`

### 4.4 IChatClientDecorator（可选能力：对话管线装饰）

包裹模型客户端，出站脱敏 / 入站还原 / 工具实参按 EgressPolicy 处理：

```csharp
public IChatClient? Decorate(IChatClient inner)
{
    return engine.Snapshot.MasterEnabled
        ? new SensitiveMaskingChatClient(inner, engine, _host.Language, OnTurnEnd)
        : null;   // 总开关关闭 → null（零开销直通）
}
```

**要点**：装饰在 FICC（FunctionInvokingChatClient）内侧——工具调用实参经你处理前后，工具仍拿真实值执行；`FunctionCallContent`/`ToolApprovalRequestContent` 的实参还原必须**即时**（工具执行不能等回合结束）；纯文本**原样透传**（还原由显示层一次性完成，见 §4.5）。

### 4.5 IDisplayTextRestorer（可选能力：显示层一次性还原）

**v1.0.9 起文本还原的唯一正确姿势**：流式期间占位符原样输出（分片怎么切都无关），回合结束时宿主把最终回答交给你还原：

```csharp
public string RestoreDisplayText(string text)
{
    return engine.UnmaskText(text, sessionEmitted, sessionEscapes);
}
```

宿主在 `TurnCompleted` 前对 `TurnAnswer` 调用——转录/UI/阶段全部使用还原后文本。会话映射建议**插件级共享**（同进程所有客户端实例共用一份，见 §7）。

### 4.6 IPluginTurnTelemetry（可选能力：回合遥测）

```csharp
public PluginTurnStats? GetLastTurnStats();   // (MaskedCount, MaskedLastUserText, ImagesPresent, NoteInjected)
```

宿主在回合结束后查询，驱动「🛡 已脱敏 N 处」徽标与悬停（模型实际收到的文本）。

### 4.7 IPluginDownloadService（宿主注入，经 IPluginHostContext.Download）

```csharp
Task DownloadFileAsync(string url, string destPath,
    IProgress<PluginDownloadProgress>? progress = null, CancellationToken ct = default);
Task<string> DownloadStringAsync(string url, CancellationToken ct = default);
```

共享 HttpClient（30 分钟超时）、分块落盘（`.part` 原子改名）、≥256KB 进度上报、失败自清。**97MB 模型 / 10MB+ 插件包大文件由此兜底**。

---

## 5. 图标（Icon）

- **唯一展示通道 = icon.png（v1.0.15 定稿）**：插件工程根放 64×64 PNG，csproj `<None Include="icon.png" CopyToOutputDirectory="PreserveNewest" />` 随包分发——宿主在已装插件列表名称前、回合徽标处渲染为图片
- `string Icon`（emoji）**仅为元数据，UI 不渲染**（真机曾因 emoji 与图片并存出现双图标，已收敛）
- **显示位置**：已装插件列表名称前 / 回合徽标（气泡轨道芯片同样用 icon.png 图片）/ 插件设置对话框任务栏图标
- **宿主解析**：`PluginManager.GetIconPath(id)` → 已装目录根 `icon.png`；缺失时回退扫描同 id 兄弟目录（`-rN` 重装后缀）取最新；全无 → 不显示图标
- **展示形态（宿主定稿）**：已装列表 / 回合轨道芯片 = 图标本体（透明底、无边框、20×20）；**悬停放大 20%**（24×24）；index.json 的 `icon` 字段仅为元数据保留，UI 不渲染 emoji
- **选型建议**：64×64 PNG（过大请自行缩放）；图形避免依赖透明度以外特效；安装前（可用列表）不显示图标
- index.json 的 `icon` 字段仅为元数据保留，UI 不渲染

---

## 6. 会话映射与跨进程还原（文本脱敏类插件专用）

占位符标签（`[PERSON_1]`）与真实值的映射若需**跨重启/重装**还原（工作区文件里的字面占位符），使用宿主持久化约定：

- 位置：`{WorkspaceRoot}/privacy/session-maps.json`（`{ "emitted": {tag: value}, "escapes": {} }`）
- 宿主在**回合结束**后由插件回调 `PersistSessionMaps` 原子落盘；启动时 `LoadSessionMaps` 种入插件级共享映射
- 无映射覆盖的旧标签按 fail-open 原样显示（真实值未落盘即随进程消亡——隐私边界，非缺陷）

---

## 7. 并发与线程模型（血泪教训）

- **SetSettingsJson 在宿主后台线程调用**——内部禁止同步等待门闩/引擎刷新（真机卡死案例）；重活 `Task.Run` + 合并（Interlocked 排队）
- 离开 UI 线程必须 `Task.Run`（`Task.Yield` 在 UI SyncContext 下原地排队，无效）
- 引擎/壳的快照与合并语义：MaskText 只写**增量**到 outcome.Emitted，调用方负责 MergeDeltas 进共享映射
- 会话映射为插件级单例：任何壳实例的写入对 RestoreDisplayText 立即可见

---

## 8. 打包与发布

1. `dotnet publish -c Release -f net10.0 -r {rid}` × 4（win-x64 / win-arm64 / osx-x64 / osx-arm64）
2. 排除宿主共享契约程序集（`AgentBrook.Plugin.Abstractions` 等，不入包），并**反射校验插件 dll 对它们的引用版本 = `1.0.0.0`**（§11.3）
3. zip → manifest.json（逐文件 SHA-256）→ **顶层 index.json 由权威工具重建**：

```bash
# index 的 sha256/size 一律从磁盘实际 zip 重算（禁止手写/复制旧值）
python3 tools/plugin-publish/rebuild-plugin-index.py            # 重建顶层 index.json
python3 tools/plugin-publish/rebuild-plugin-index.py --check    # 上传前自检：index 与 zip 必须零漂移
```

4. 上传服务器 `updated/plugin/{id}/{version}/`（+ 模型目录 `{id}/models/` 若有）+ **顶层 index.json**；多插件部署必须用合并版 index（单插件 index 覆盖会丢掉其他插件条目）
5. 参考脚本：`tools/plugin-publish/build-privacy-plugin.sh` / `build-asr-plugin.sh` / `tools/im/publish-im-plugins.sh`（均已在末尾挂载 index 重建）

**⚠️ 打包脚本强制要求**：
- 开头 `unset VERSION`（环境变量会泄漏进 `dotnet publish` 并改写契约程序集版本，见 §11.3）；内部版本变量改用 `PKG_VERSION`
- 上传前在本地镜像目录跑一次 `rebuild-plugin-index.py --check`（零漂移才允许上传）

服务端 index 条目示例：

```json
{
  "id": "privacy-masking", "name": "隐私脱敏", "latest": "1.0.14",
  "icon": "🛡", "minHost": "1.1.0",
  "capabilities": ["chat-client-decorator", "settings-contributor", "display-text-restorer", "turn-telemetry"],
  "packages": { "win-x64": { "url": "…", "sha256": "…", "size": 123 } }
}
```

---

## 9. 合规检查单

见 [doc/18 §7.2](18-helper-plugin-system-design.md)（9 条，含下载与文本还原新条目）。

## 10. 调试

- `tools/PipelineProbe`：全栈无头探针（真实模型 + 插件管线，逐事件计时）
- 宿主日志：`[privacy-masking]` 前缀（host.Log 出口）
- 对话框构建标记：`[plugins-dialog] build r2`
- 真机卡死归因三步：PipelineProbe 复现 → `[ui-stall]` 看门狗 → 断点验证

## 11. 契约、依赖与原生库（强制规范详解）

> 本节每条都是 2026-11 真机事故的取证结论，附复现与检查方法。**第三方插件违反其中任一条都会在上架审核中被拒。**

### 11.1 契约对象：一律属性式，禁止位置参数演进

```csharp
// ✅ 正确：属性式（加字段 = additive，不破坏任何调用方）
public sealed class AsrCapabilityRequest
{
    public required ReadOnlyMemory<byte> Pcm16kMono { get; init; }
    public int SampleRate { get; init; } = 16000;
    public string? LanguageHint { get; init; }
}

// ❌ 错误：位置参数 record + 默认值——将来加参数就是位置破坏
public sealed record BadRequest(ReadOnlyMemory<byte> Pcm, int SampleRate = 16000, string? Hint = null);
```

**存量位置 record 的扩展方式**：不得追加位置参数（会让所有插件的构造点源码 + 二进制双重 breaking），只能加**非位置 `init` 属性**：

```csharp
public sealed record ImAttachmentDescriptor(/* 10 个既有位置参数保持不动 */)
{
    public ImAttachmentTranscript? Transcript { get; init; }   // additive，旧 JSON 缺字段 → null
}
```

检查：契约类是否全部为 `class`/`record` + `init` 属性；新增成员是否全部非位置。

### 11.2 程序集依赖方向：新类型只能往最底层放

宿主契约分层（**自底向上，禁止反向引用**）：

```
Runtime.Abstractions   ← 最底层（Execution/TurnExecutionAttachment/ImAttachmentTranscript 等纯数据契约）
   ↑
Messaging.Abstractions ← 通道/描述符契约（ImAttachmentDescriptor 等）
   ↑
Messaging / Agent.IM / Plugin.Abstractions / Plugin.Hosting …
   ↑
Agent.Core → Helper（宿主）
```

**真实案例**：`ImAttachmentTranscript` 最初计划放在 `Messaging.Abstractions/Inbound.cs`，但 `Runtime.Abstractions/Execution.cs` 的 `TurnExecutionAttachment.Transcript` 需要引用它——而 `Inbound.cs` 已经 `using AgentBrook.Runtime.Abstractions`，反放即成**程序集循环**（编译失败）。最终落 `Runtime.Abstractions`。

检查：`dotnet build` 全链 0 错误；新增 `using` 是否形成反向引用。

### 11.3 契约版本：插件内的引用版本必须是 `1.0.0.0`

插件包**不得**携带 `AgentBrook.Plugin.Abstractions.dll` 等宿主共享程序集（见 §8 步骤 2）；同时，插件 dll 对这些程序集的**引用版本**必须是 `1.0.0.0`——否则宿主加载时报：

```
Could not load file or assembly 'AgentBrook.Plugin.Abstractions, Version=1.0.17.0'
→ Unable to load one or more of the requested types
```

**真实事故根因**：打包脚本被以 `VERSION=1.0.17 bash build-xxx.sh` 方式调用，环境变量 `VERSION` **泄漏进 `dotnet publish`**（.NET SDK 把 env `VERSION` 映射为 MSBuild `$(Version)` 属性）→ 契约程序集的 `AssemblyVersion` 被编成 `1.0.17.0` → 与宿主（正常构建 = `1.0.0.0`）不一致。

**强制做法**：
- 打包脚本开头 `unset VERSION`，内部版本变量用自定义名（如 `PKG_VERSION`）；
- 发布后**反射校验**每个包的引用版本：

```csharp
var asm = Assembly.LoadFrom(插件 dll 解包路径);
foreach (var r in asm.GetReferencedAssemblies())
    if (r.Name!.StartsWith("AgentBrook."))
        Console.WriteLine($"{r.Name} = {r.Version}");   // 必须全部 = 1.0.0.0
```

### 11.4 原生库（native）：同进程唯一版本

**规则**：宿主进程内，同一原生运行时（典型：OnnxRuntime）**只能有一个实际生效版本**——后加载的库不会覆盖先加载的。

**真实事故（2026-11-06）**：
- 隐私脱敏插件携带 OnnxRuntime **1.20.1**（C-API 到 20），本地语音识别插件（sherpa-onnx）需要 OnnxRuntime **C-API 28**；
- 宿主先加载 1.20.1 → sherpa 调 `GetApi(28)` 落到 1.20.1 → native 抛
  `The requested API version [28] is not available … Current ORT Version is: 1.20.1` → 冒泡为托管层 `System.ExecutionEngineException`；
- **dylib 改名隔离单独不够**（复现实验证实）：托管侧 P/Invoke 名固定（如 `onnxruntime`），改名后仍会命中已加载的同名模块。

**强制做法**：
1. 声明原生资产：`deps.json` 必须包含 `runtimes/{rid}/native/...`（`AssemblyDependencyResolver` 据此解析，`PluginLoadContext.LoadUnmanagedDll` 已接线）；
2. **与已有插件统一原生运行时版本**——升级方向选"高版本兼容低"（OnnxRuntime 1.28 同时满足 API 20 与 28）；
3. 新增插件如引入新的原生运行时（ORT/PortAudio/ffmpeg 等），必须在 PR/发布说明中列出**与既有插件的版本兼容矩阵**；
4. 排查特征：`ExecutionEngineException`、native 崩溃、`API version [N] is not available` → 优先怀疑原生版本冲突。

### 11.5 设置页文案：按 `host.Language` 给三语字面量

宿主渲染 `ISettingsContributor` 的 `title` 时会走宿主 `I18n.T(key)`，**未命中的键原样返回**。因此插件不要把自有键当作"宿主一定有翻译"——正确做法是按 `host.Language`（`zh`/`en`/`id`）直接给三语字面量：

```csharp
private string L(string zh, string en, string id) => _host?.Language switch
{
    "en" => en, "id" => id, _ => zh,
};
// schema: new { key = "asr.model", type = "model", title = L("转写模型", "Transcription model", "Model transkripsi"), … }
```

`model` 行的按钮文案（安装/已安装/测试）由宿主内置，插件只需给 `installAction`/`checkAction`/`readyAction`。

### 11.6 模型/数据包分发：服务端统一发布

**规则**：随插件使用的模型不得打进插件包、不得在运行时外链第三方源（如 huggingface）。

服务端目录结构（以 ASR 插件为例）：

```
updated/plugin/asr/models/models.json                       ← 清单（含逐文件 SHA-256）
updated/plugin/asr/models/sensevoice-small-int8/model.int8.onnx
updated/plugin/asr/models/sensevoice-small-int8/tokens.txt
```

插件侧要求：
- 基址常量默认生产域名（`https://helper.agentbrook.com/updated/plugin/{id}`），本地联调可 setter 改写；
- 下载一律 `host.Download`，落工作区共享区（如 `workspace/models/asr/`，插件升级不重下）；
- **内置恒定 SHA-256**（发布脚本附带"打包源 SHA 与插件内置值漂移"一致性检查）；
- 提供 `remove-model` 语义（删文件 + 清单），重装需重新下载。

# 🧪 Codex 任务助理配置指南

示例号码 `10088` 会把电话里明确交办的任务交给本机 Codex。实现位于两个独立程序集：

- `Agent.Telephone.Codex.Abstractions`：`ICodexFunction`、请求和结果契约；
- `Agent.Telephone.Codex`：App Server、SQLite Thread 映射和 `WithCodexAssistant` 扩展。

Demo 不再包含 Codex Function 源码；只需要注册一个扩展并提供运行配置。

## 📦 1. 注册

`Program.cs` 中的最小配置如下。Windows 会自动定位 Codex Desktop 安装的 CLI；macOS 与 Linux 使用 PATH 中的 `codex`。如需指定其他可执行文件，可把 `ExecutablePath` 改为该系统上的稳定绝对路径。

```csharp
serverHost = serverBuilder
    .Initialize(config, messageStore)
    .WithCodexAssistant(options =>
    {
        options.ThreadDatabasePath = new SqliteMessageStoreOptions().DatabasePath;
        options.ModelName = "gpt-5.6-luna";
        options.ReasoningEffort = "medium";
    })
    .Build();
```

`WorkingDirectory` 不需要在示例中设置。扩展的默认值固定为：

```text
<程序运行目录>/data/codex
```

扩展会把它规范化为绝对路径，并同时作为子进程的工作目录以及每个 `thread/start` / `thread/resume` 请求的 `cwd`。目录不存在时会自动创建，因此所有电话任务与新建 Thread 都使用同一个 Codex 工作目录。

不要把 `WorkingDirectory` 改为源代码根目录、用户目录、共享盘或系统目录。默认目录是只读任务的受控工作区；需要让 Codex 读取的文件，应显式放入其中。

## 🔐 2. 前置条件与操作系统

在运行电话服务的**同一个操作系统账户**下安装、登录并验证 Codex CLI。默认命令在各平台相同：

```powershell
# Windows PowerShell
Get-Command codex
codex --version
codex login status
```

```bash
# macOS / Linux
command -v codex
codex --version
codex login status
```

电话服务如果以另一服务账户启动，会使用该账户独立的登录和 Thread 历史，需单独完成登录。不要硬编码 Codex Desktop 内部带版本 hash 的可执行路径，Desktop 更新后该路径可能失效。

在 Windows，扩展默认从 `%LOCALAPPDATA%\OpenAI\Codex\bin` 下定位最新修改的 `codex.exe`，避免把 Desktop 更新后变化的版本目录写入配置。若未安装 Desktop，会回退为 PATH 中的 `codex`；macOS 和 Linux 始终使用该回退方式。

运行时通过 `codex app-server` 建立 JSON-RPC stdio 通道。`ProcessStartInfo`、`Path.Combine`、SQLite 与标准输入输出均不依赖 Windows；工作目录会按当前系统路径规则解析。App Server 使用 `approvalPolicy: "never"` 和 `sandbox: "read-only"`，系统不会替电话用户批准操作、回答 Codex 的交互式追问或升级写入权限。协议说明见 [OpenAI 官方 App Server 文档](https://developers.openai.com/codex/app-server)。

Windows 下会尝试通过 Desktop 的内部命名管道刷新 Thread 列表；这是失败可忽略的显示优化。macOS 与 Linux 会跳过该步骤，电话任务、SQLite 映射和 CLI Thread 续接不受影响。

## 🧵 3. Thread 与 Desktop 历史

首次调用会创建持久化 Thread，并立即按 `UserAor + AssistantNumber` 写入 `CodexThreads` 表。后续来电续接该 Thread；`startNewTask: true` 仅在新 Thread 创建成功后替换当前映射。不同来电用户和不同 Assistant 不共享上下文。

Thread 历史可由同一账户的 Codex Desktop 在刷新或重新打开后读取。`cwd` 统一为 `data/codex`，但 Desktop 的 Project 侧栏归类是 Desktop 自己维护的状态；外部 App Server 创建的 Thread 不能承诺即时刷新或必然显示在某个 Project 分组中。不要把这一 UI 行为作为电话任务成功与否的依据。

## ⏱️ 4. 电话行为

`RunCodexTaskAsync` 只发送当前明确的任务指令，不附带普通电话聊天历史。它会等待最终 assistant 文本；用户新说话、角色切换或服务停止时会取消并终止对应 App Server 子进程。普通 SIP 挂断不会取消已提交的离线 Turn，完成结果仍会走现有回拨或未读留言流程。

同一服务进程中的 Codex 调用会串行化，以防止两个通话同时续接同一 Thread；等待队列同样响应取消。

## ✅ 5. 验证

```powershell
dotnet restore Agent.Telephone.sln
dotnet build Agent.Telephone.sln --no-restore
dotnet test Agent.Telephone.sln --no-build --filter "FullyQualifiedName~CodexAssistantTests"
```

之后启动 Demo、拨打 `10088`，先交办一个只读且不需要中途确认的任务，再拨打一次要求续接；最后明确“开始一个新任务”。若出现失败，请检查当前服务账户的 CLI 登录、`WithCodexAssistant` 中的模型/推理强度、`data/codex` 与 SQLite 文件权限，不要把认证材料、电话内容或完整 stderr 写进日志。

上一篇：[09-项目目录、外部资源与内置提示音.md](09-项目目录、外部资源与内置提示音.md)<br />
下一篇：[文档索引](README.md)

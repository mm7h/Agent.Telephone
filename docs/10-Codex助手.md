# 🧪 Codex 任务助理配置指南

示例号码 `10088` 可以把电话里说出的任务交给本机 Codex 执行。本章按 Windows 环境编写，第一次配置时依次完成下面四件事即可：

1. 安装并登录 Codex CLI。
2. 告诉项目“Codex 程序安装在哪里”。
3. 设置一个固定的 Project 名称，让 Codex 知道在哪里工作。
4. 选择 Codex 使用的模型和思考强度。

配置完成后，Thread 会由程序自动创建和续接，不需要手工复制任何 Thread ID。

开始前，建议先按照 [快速开始与部署](00-快速开始与部署.md) 启动电话服务，并确认普通 Assistant 可以正常接听。这样如果 `10088` 无法工作，就可以把问题集中在 Codex 配置上，而不是同时排查电话、模型和网络。

> Codex 任务助理适合在受控电脑上执行耗时较长的只读任务。当前示例不能通过电话批准操作、回答 Codex 的临时追问或开放写入权限，不要把它直接当作生产服务器管理工具。

## 🧩 1. 先理解三个名称

这三个名称看起来相似，但用途不同：

| 名称 | 可以把它理解成 | 需要手工配置吗 |
| --- | --- | --- |
| Codex 执行路径 | Codex 程序在电脑上的地址，例如 `C:\Users\你的用户名\AppData\Roaming\npm\codex.cmd`。 | 需要。 |
| Project | Codex 工作时使用的固定文件夹，类似一张公共工作台。 | 只需设置名称，文件夹会自动创建。 |
| Thread | 某位电话用户的一项连续任务，类似放在工作台上的一本任务笔记。 | 不需要，程序会自动管理。 |

所有电话用户都会在同一个 Project 文件夹中工作，但不会共用同一本 Thread。程序会按照“电话用户 + Assistant 号码”区分 Thread。

## 📦 2. 安装并登录 Codex CLI

如果电脑已经可以在 PowerShell 中运行 `codex`，可以直接跳到下一节。

### 2.1 安装

打开 PowerShell，使用 OpenAI 官方提供的 Windows 安装命令：

```powershell
powershell -ExecutionPolicy ByPass -c "irm https://chatgpt.com/codex/install.ps1 | iex"
```

也可以使用 npm 安装：

```powershell
npm install -g @openai/codex
```

安装后关闭并重新打开 PowerShell，再执行：

```powershell
codex --version
```

如果能看到版本号，说明安装成功。安装方式和最新命令以 [OpenAI 官方 Codex CLI 文档](https://learn.chatgpt.com/docs/codex/cli.md) 为准。

### 2.2 登录

在 PowerShell 中执行：

```powershell
codex login
```

按照打开的浏览器页面完成登录，然后检查登录状态：

```powershell
codex login status
```

必须使用**运行电话服务的同一个 Windows 账户**完成安装和登录。例如平时用自己的账户启动示例，就在自己的账户下登录；如果以后改成 Windows 服务，也要为实际运行服务的账户准备 Codex 登录信息。详细登录方式见 [OpenAI 官方身份验证文档](https://learn.chatgpt.com/docs/auth.md)。

## 🔎 3. 配置 Codex 执行路径

### 3.1 找到真实路径

在 PowerShell 中执行：

```powershell
where.exe codex
```

命令可能返回一行或多行路径。请选择以 `codex.cmd` 或 `codex.exe` 结尾的完整路径，不要选择 `codex.ps1`。例如：

```text
C:\Users\XiaoMing\AppData\Roaming\npm\codex.cmd
```

复制路径后，先直接测试一次：

```powershell
& "C:\Users\XiaoMing\AppData\Roaming\npm\codex.cmd" --version
& "C:\Users\XiaoMing\AppData\Roaming\npm\codex.cmd" login status
```

两条命令都能正常返回，才继续下一步。

### 3.2 写入项目

打开 [CodexAssistant.cs](../demo/Agent.Telephone.Sample.Server/FunctionTools/Codex/CodexAssistant.cs)，找到文件开头的 `CODEX_PATH`：

```csharp
private const string CODEX_PATH = "D:\\nodejs\\node_global\\codex.cmd";
```

把引号中的内容替换为刚才找到的真实路径。例如：

```csharp
private const string CODEX_PATH = "C:\\Users\\XiaoMing\\AppData\\Roaming\\npm\\codex.cmd";
```

C# 字符串中的每个反斜杠都要写成两个 `\\`。如果复制的是 `C:\Users\XiaoMing\...`，写入代码时应变成 `C:\\Users\\XiaoMing\\...`。

## 📁 4. 配置 Project

同一个文件中还有一个 `CODEX_PROJECT_NAME`：

```csharp
private const string CODEX_PROJECT_NAME = "电话对话助手";
```

它不是代码仓库名称，也不是 Thread 名称，只是 Codex 固定工作文件夹的末级名称。可以保留默认值，也可以改成容易识别的名称，例如：

```csharp
private const string CODEX_PROJECT_NAME = "家庭电话助手";
```

以默认值为例，程序会使用下面的文件夹：

```text
data\codex\电话对话助手
```

这是相对示例宿主工作目录的路径。按照本章的启动方式运行时，它实际位于 `demo/Agent.Telephone.Sample.Server\data\codex\电话对话助手`。不需要提前创建该文件夹，第一次执行任务时程序会自动创建。当前示例允许这个文件夹不是 Git 仓库；它仍使用 `codex exec` 默认的只读沙箱。相关规则见 [OpenAI 官方非交互模式文档](https://learn.chatgpt.com/docs/non-interactive-mode.md)。

如果希望在 Codex Desktop 中找到这个 Project，可以在 Desktop 中手动添加上述文件夹。程序不会自动打开 Desktop，也不会让 Desktop 实时显示电话执行过程。

## 🧠 5. 指定模型和思考强度

电话中的 Codex 任务实际会经过两个模型，不要把它们混在一起：

| 配置位置 | 负责什么 |
| --- | --- |
| `config.json` 中 `10088` 的 `"LLM": "Deepseek"` | 理解电话内容，并判断何时调用 Codex 工具。它不是 Codex 执行任务时使用的模型。 |
| `CodexAssistant.cs` 中的常量 | 指定 Codex CLI 真正执行任务时使用的模型和思考强度。 |

电话助手会在每次执行任务时明确传入模型和思考强度，不依赖用户电脑中的 Codex 全局配置文件。这样电话服务使用的模型只由当前项目代码决定。

### 5.1 在项目代码中填写模型

打开 [CodexAssistant.cs](../demo/Agent.Telephone.Sample.Server/FunctionTools/Codex/CodexAssistant.cs)，在 `CODEX_PATH` 下方找到下面两个常量：

```csharp
private const string CODEX_MODEL = "gpt-5.6";
private const string CODEX_REASONING_EFFORT = "medium";
```

建议将 `CODEX_MODEL` 改为 `gpt-5.6-terra`。它是 GPT-5.6 系列中适合日常任务的均衡型号，也是本示例推荐的默认选择：

```csharp
private const string CODEX_MODEL = "gpt-5.6-terra";
```

`gpt-5.6` 是 OpenAI 官方文档支持的通用名称，但它不指定使用 Sol、Terra 还是 Luna。电话服务需要长期、稳定地按预期执行任务，因此建议填写明确的型号：

| 模型名称 | 适合的电话任务 |
| --- | --- |
| `gpt-5.6-terra` | 日常问答、代码阅读、常规排查和大多数任务。推荐作为默认值。 |
| `gpt-5.6-sol` | 复杂改动、难以界定的问题、需要更多分析或润色的任务。通常更慢、消耗更多额度。 |
| `gpt-5.6-luna` | 目标明确、重复性高的查询、提取、分类和整理任务。速度更快。 |

实际可用模型取决于登录账户、工作区权限和 Codex CLI 版本。如果不确定模型名称，可以在 PowerShell 中启动 `codex`，输入 `/model`，从列表中查看账户可用的模型，再把名称原样复制到 `CODEX_MODEL`。不需要修改 Codex 的 `config.toml`。

### 5.2 在项目代码中填写思考强度

修改 `CODEX_REASONING_EFFORT` 的值：

```csharp
private const string CODEX_REASONING_EFFORT = "medium";
```

思考强度可以简单理解为“Codex 在回答前愿意花多少精力分析”：

| 值 | 适合的任务 | 特点 |
| --- | --- | --- |
| `minimal` / `low` | 简单查询、整理和格式转换。 | 速度较快。 |
| `medium` | 大多数日常任务。 | 速度和分析深度较平衡，建议第一次配置使用。 |
| `high` | 多步骤分析、复杂排查和需要仔细检查的任务。 | 更慢，通常会使用更多额度。 |
| `xhigh` | 极复杂任务。 | 只在部分模型中可用，耗时也可能更长。 |

不是所有模型都支持全部强度。如果出现不支持的提示，先改回 `medium`，或者通过 `/model` 查看该模型可选择的强度。OpenAI 官方建议从较低或默认强度开始，只在任务确实需要更多分析时再提高。

一份适合首次使用的完整项目配置如下：

```csharp
private const string CODEX_MODEL = "gpt-5.6-terra";
private const string CODEX_REASONING_EFFORT = "medium";
```

程序会为每一次新任务和续接任务传入 `--model` 与 `model_reasoning_effort`，因此这些值会覆盖 Codex 的用户级默认设置。参数格式与可选项见 [OpenAI 官方命令行选项文档](https://learn.chatgpt.com/docs/developer-commands.md?surface=cli) 和 [模型文档](https://learn.chatgpt.com/docs/models.md)。

### 5.3 确认配置是否生效

保存 `CodexAssistant.cs`、重新编译并启动服务后，拨打 `10088` 执行一个简单任务即可验证。更改模型或思考强度后，第一次打电话时建议明确说“开始一个新任务”，避免旧任务上下文带来混淆。

当前这两个常量会影响所有电话用户的 Codex 任务。若未来需要不同 Assistant 使用不同模型，应再扩展为按 Assistant 配置；本示例目前不这样做。

## 🧵 6. Thread 如何工作

Thread 用来记住同一项任务的上下文。用户不需要配置 Thread ID，只需要在电话中说明自己是要“继续当前任务”还是“开始新任务”。

- 第一次拨打 `10088` 并交办任务时，程序自动创建一条新 Thread。
- 同一位用户再次拨打 `10088`，说“继续刚才的任务”或“把刚才的结果再精简一下”，程序会续接原来的 Thread。
- 用户明确说“开始一个新任务”时，程序会新建 Thread，并把以后默认续接的目标改成这条新 Thread。
- 旧 Thread 不会被删除，只是不再作为该用户的默认 Thread。
- 不同电话用户，或者同一用户拨打不同 Assistant 号码，不会共用 Thread。

电话服务把当前 Thread 的关联保存在默认数据库：

```text
./data/messages-db/messages.db
```

一般不需要打开或修改这个数据库。如果删除数据库、换了 Windows 账户，或者让服务使用了不同的 `CODEX_HOME`，电话助手可能无法继续以前的 Thread。初次部署时，最简单的做法是不要修改数据库位置或 `CODEX_HOME`。

## ⏱️ 7. 设置等待时间

Codex 任务可能需要几分钟。示例已经为号码 `10088` 配置了 30 分钟等待时间：

```json
"LLMResponseTimeoutSeconds": 1800
```

该配置位于 [config.json](../demo/Agent.Telephone.Sample.Server/configs/config.json) 的 `10088` Assistant 条目中。多数情况下保留 `1800` 即可。时间太短会让尚未完成的任务被取消；设置为 `0` 表示不使用本项目的回复超时，但任务仍可能因服务停止、用户打断或 Codex 自身限制而结束。

用户可以在等待期间挂断。任务完成后，系统会按照现有规则尝试回拨；没有接通时，结果会保存为未读留言。

## ✅ 8. 第一次运行与验证

保存 `CodexAssistant.cs` 后，在仓库根目录打开 PowerShell，执行：

```powershell
dotnet restore Agent.Telephone.sln
dotnet build Agent.Telephone.sln --no-restore
Set-Location demo/Agent.Telephone.Sample.Server
dotnet run
```

服务启动后，按以下顺序验证：

1. 拨打 `10088`，说一个明确、只读且不需要中途确认的任务。
2. 等待电话播报结果，或挂断后等待回拨。
3. 再次拨打 `10088`，说“继续刚才的任务，并把结果缩短一半”，确认 Codex 能记住上一项任务。
4. 再说“开始一个新任务”，确认之后的内容不再沿用旧任务上下文。

如果以上步骤都成功，说明执行路径、Project 和 Thread 已经正常工作。

## 🩺 9. 常见问题

| 现象 | 处理方法 |
| --- | --- |
| `codex` 不是可识别的命令 | 关闭并重新打开 PowerShell；仍无效时重新安装 Codex CLI。 |
| 提示找不到文件 | 重新运行 `where.exe codex`，确认 `CODEX_PATH` 使用完整的 `.cmd` 或 `.exe` 路径，并检查代码中的反斜杠是否写成 `\\`。 |
| 提示未登录或认证失败 | 在运行电话服务的同一 Windows 账户下执行 `codex login`，再用 `codex login status` 检查。 |
| 第一次可以运行，重启后不能续接 | 确认服务仍使用同一 Windows 账户、同一个数据库和同一个 `CODEX_HOME`。 |
| 提示模型不存在或不支持当前思考强度 | 在 Codex 中输入 `/model` 查看账户可用选项；把模型名称写入 `CODEX_MODEL`，并先将 `CODEX_REASONING_EFFORT` 改为 `medium`。 |
| 电话执行的模型与预期不同 | 确认修改的是 `CodexAssistant.cs` 中的 `CODEX_MODEL`，而不是把 `config.json` 中的 `LLM` 当作 Codex 模型。 |
| 总是接着旧任务回答 | 明确告诉电话助手“开始一个全新的任务”。 |
| 任务经常提前结束 | 检查 `10088` 的 `LLMResponseTimeoutSeconds`，可在确认安全后适当增大。 |
| Desktop 中看不到电话任务 | 手动把 Project 文件夹添加到 Desktop；当前示例不会自动打开或同步 Desktop 界面。 |

如果任务失败，系统不会自动重试或偷偷新建另一条 Thread。应先检查路径、登录状态和当前任务，再决定继续旧任务还是明确开始新任务。

上一篇：[09-项目目录、外部资源与内置提示音.md](09-项目目录、外部资源与内置提示音.md)<br />
下一篇：[文档索引](README.md)

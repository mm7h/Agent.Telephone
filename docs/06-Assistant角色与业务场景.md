# Assistant 角色与业务场景

本文说明示例宿主中 `AssistantConfigs` 的角色设计，以及角色切换、回拨、离线留言和按键交互在电话链路中的实际行为。配置字段的完整说明见 [02-配置文件参考](02-配置文件参考.md)，Function Tool 的编写方式见 [04-FunctionTool与按键交互扩展](04-FunctionTool与按键交互扩展.md)。

> 本文以当前工作区的 `demo/Agent.Telephone.Sample.Server/configs/config.json` 和源码为准。`10088` 是可选的 Codex 非交互任务助理；它不提供电话审批、追问或主机写入能力。

## 1. Assistant 的绑定方式

一个 Assistant 由一个可拨打号码和一组运行时能力共同定义：

```text
用户拨打的号码
    │
    ▼
AssistantConfig（Prompt、VAD、ASR、Intent、LLM、TTS、AllowedTools）
    │
    ▼
本通电话的 AIAgentContext（Provider、Handler、Private Function Tool）
```

号码不是展示名称：呼入 INVITE 的被叫号码必须匹配某个 `AssistantConfig.DialingNumber`。接通后，当前通话会持有该配置及对应的 `AIAgentContext`；同一个设备同一时刻只允许一个活动通话。`HelloMessageTempletes` 中会随机选取一条作为首次问候的文本来源。

`AllowedTools` 是角色权限边界，而不是提示词中的建议。工具已注册但名称不在当前 Assistant 的 `AllowedTools` 中时，不会作为该角色的可用工具构建。提示词仍应只描述被允许的能力，避免模型尝试调用未授权工具。

## 2. 示例角色矩阵

|号码|名称与定位|当前意图模式|当前可用工具|适合的场景|
|---|---|---|---|---|
|`10000`|智能服务前台|`FunctionCall`|`SwitchAssistantAsync`、`HangupCurrentCall`|识别需求并在当前电话内转给专业角色。|
|`10086`|通用问答助理|`FunctionCall`|`GetWeatherInfo`、`HangupCurrentCall`|日常咨询、知识问答、思路梳理、建议和轻松闲聊。|
|`10085`|写作与语言助理|`IntentLlm`|`HangupCurrentCall`|起草、润色、改写、翻译与表达优化。当前提示词强调短句、适合电话收听。|
|`10088`|Codex 任务助理|`FunctionCall`|`RunCodexTaskAsync`、`HangupCurrentCall`|在受控主机上执行明确、只读且非交互的 Codex 任务；可能耗时较长。|

前台的 `AssistantPrompts/10000.md` 只允许将通话转往 `10086` 或 `10085`。这是一份产品路由规则，不会替代运行时校验；`AssistantRoleControl` 仍会核对目标号码存在、不是当前角色、通话未结束且没有并发切换。

### 2.1 直接拨打与前台路由

用户可直接拨打专业号码，例如 `10086`；此时跳过前台，直接创建通用问答角色。用户也可拨打 `10000`，由前台根据需求调用工具路由：

```text
拨打 10000
  └─ 前台澄清需求
       ├─ 日常咨询 / 分析 / 闲聊  ──► SwitchAssistantAsync("10086")
       └─ 写作 / 润色 / 翻译      ──► SwitchAssistantAsync("10085")
```

前台应在意图不清时只追问一个关键问题；确认后立即转接，不应代替专业 Assistant 处理后续内容。专业角色的 Prompt 同样应限制一次回复的长度和结构，避免把适合屏幕阅读的长段落、表格或代码直接播报到电话中。

## 3. 当前通话内的角色切换

“转接”在本项目中不是新建一通 SIP 电话。它保留现有的 SIP dialogue、`SIPUserAgent`、`VoIPMediaSession`、协商得到的音频格式和 RTP 会话；变化的只有 Assistant 配置及其 `AIAgentContext`（Provider、Handler、Private Function Tool）。因此用户不会收到第二次来电，也不需要重新接听。

### 3.1 切换时序

```text
前台工具调用 SwitchAssistantAsync(target)
  │
  ├─ 校验目标：非空 / 存在 / 不是当前号码 / 通话仍在
  ├─ 取得本通话的独占切换权；暂停 Agent 媒体并取消当前 Turn
  ├─ 启动 180.mp3 循环回铃
  ├─ 释放旧 AIAgentContext，替换为目标号码的新 AIAgentContext
  ├─ 构建：Function Tool → Provider → Handler
  ├─ 停止回铃
  └─ 恢复 Agent 媒体，播放目标角色首次问候并标记通话已连接
```

工具立刻获得的 `Accepted` 仅表示后台切换请求已被接受，不等同于目标 Pipeline 已经构建成功。`AssistantSwitchResult` 的其他可观察状态包括：

|状态|含义|工具或提示词应如何处理|
|---|---|---|
|`InvalidTarget`|号码为空或无效。|请模型补充明确目标，不要猜测。|
|`UnknownAssistant`|号码未出现在当前 `AssistantConfigs`。|说明没有该服务，不要继续切换。|
|`CurrentAssistant`|请求目标就是当前角色。|继续当前会话即可。|
|`CallEnded`|通话已结束或无法取得通话使用权。|不再输出通话内回复。|
|`Failed`|请求被取消、已有切换正在进行或通话控制不可用。|简短告知暂不能切换；避免重试风暴。|

### 3.2 回铃、失败与资源释放

开始切换时会暂停 Agent 媒体并重启当前 Turn，避免旧角色的流式文本或 TTS 音频和新角色输出交错。系统在构建阶段循环播放嵌入式 `error_feedback/180.mp3`；无论成功、异常还是通话挂断，都会取消并等待该播放任务结束。

切换失败有两条不同路径：

- 如果旧 Agent 尚未替换（例如换前校验或替换动作失败），系统恢复旧会话的 Agent 媒体，用户可继续与原角色通话。
- 如果旧会话已替换但新 Pipeline 构建失败，旧资源已经释放，无法安全恢复。系统停止回铃、播放 `error_feedback/480.mp3`，然后挂断当前电话，避免留下半初始化角色。

因此，新增角色所依赖的模型、工具或外部服务应在上线前单独验证。不要在 Function Tool 内直接操作 SIP/RTP 对象；工具通过 `IAssistantControl` 和 `AssistantControlAdapter` 请求切换，底层控制仍由 Provider 负责。

## 4. 在线对话、挂断后的回拨与离线留言

每次用户语句对应一个 Turn。在线时，LLM 的文本片段继续送往 TTS/RTP；已完成的在线 Turn 先由当前 Agent 会话保留，并在通话关闭时以 `Read` 状态持久化。若用户在回复生成期间挂断，生成不必立即停止：系统将此 Turn 标为离线投递，立即持久化已生成和后续生成的文本片段，并在完成后尝试主动回拨。

```text
用户说话 → LLM 流式生成
               │
               ├─ 通话仍在线：实时 TTS 播放，完成后保存为 Read
               │
               └─ 已挂断：保存为 Generating → Unread
                                  │
                                  ├─ 设备仍有有效 SIP 注册：主动回拨并播放
                                  └─ 未接听/无法回拨：保留为未读留言
```

回拨使用设备最近一次有效 SIP `Contact`；拨号超时使用 `SIPConfig.CallbackTimeoutSeconds`。设备进入回拨占用状态后，新的呼入不会与该回拨争用同一设备。回拨已接通时，系统重新构建当前 Assistant 的 Function Tool、Provider 和回拨 Handler，再按片段播放留言；消息播放完成后才标记为已读。

下次该用户拨打**同一个 Assistant 号码**时，系统按 `UserAor + AssistantNumber` 查找未读助手消息：

1. 暂停正常用户语音输入并播放留言菜单。
2. 有 1–9 条时播报数量；10 条及以上播放通用提示。
3. 按 `1` 播放未读留言，逐条播放成功后各自标为已读；按 `2` 全部标为已读。
4. 按键等待最多 15 秒；未选择、异常或结束后恢复正常首次问候/通话处理。

留言与普通会话属于敏感数据。数据库和缓存位置、保留期及替换持久化实现见 [05-持久化、回拨与离线留言](05-持久化、回拨与离线留言.md)；生产保护要求见 [08-安全、隐私与生产运行边界](08-安全、隐私与生产运行边界.md)。

## 5. DTMF 菜单与电话任务交互

DTMF 输入有两类用途：

- **系统菜单**：未读留言菜单使用 `1`、`2`，不受普通工具可用按键集合限制。
- **工具交互**：`IAssistantControl.RequestDtmfInputAsync` 用于确认或选择。普通工具请求的按键必须同时在支持范围内，并且映射到当前角色允许的功能。

一次 DTMF 窗口最多等待 15 秒，同一时刻只能有一个窗口。新 Turn、切换角色、挂断或取消会关闭窗口；调用方必须处理 `Accepted`、`TimedOut`、`Cancelled`、`CallEnded` 与 `Unavailable`，不得把“未按键”当成确认。

当前示例配置的注释已明确：需要按键反馈来触发自定义 Function Tool 时，应使用 `Intent: "FunctionCall"`。`10085` 使用 `IntentLlm`，不应把它当作 DTMF 工具交互入口。

## 6. Codex 任务助理

`10088` 将 `RunCodexTaskAsync` 暴露给电话中的模型。工具会：

1. 使用 `CODEX_CLI_PATH` 或 `PATH` 找到已登录的本机 Codex CLI。
2. 以 `codex exec --ephemeral <task>` 启动单次非交互任务，并只取 stdout 的最终结果。
3. 用户仍在线时正常播报结果；用户已经挂断时，沿用离线 Turn 的回拨/未读留言投递。
4. 当前任务不跨来电保存 Codex Thread，也不能在电话中批准命令、文件变更、权限、App 操作或回答 Codex 追问。

这项能力的风险仍高于普通问答。官方 `codex exec` 默认使用只读沙箱，示例不覆盖该默认值；服务必须在受控 Git 工作目录和低权限账户下运行，且只将工具授权给专用号码。具体安全要求见 [08-安全、隐私与生产运行边界](08-安全、隐私与生产运行边界.md)。

## 7. 场景验收清单

完成角色配置或业务功能改动后，至少人工验证以下闭环：

|场景|期望结果|
|---|---|
|直拨 `10086`|随机首次问候后，可完成一轮普通问答。|
|拨打 `10000` 并要求写作帮助|只保持一通电话；听到短暂回铃后进入 `10085` 的首次问候。|
|未知、空或当前号码的切换|不释放当前正常会话，不产生第二通 SIP 呼叫。|
|新角色模型配置故障|停止回铃，播放不可用提示并结束电话。|
|用户在回复中挂断|回复完成后仅在有效注册且设备空闲时尝试一次回拨；失败后保留未读消息。|
|直拨 `10088`|已登录 CLI 收到一个明确的只读任务；用户挂断后完成结果会回拨或成为未读留言。|
|带未读留言的再次呼入|先出现 `1`/`2` 菜单；播放完才标已读，跳过则按 `2` 批量标已读。|
|DTMF 超时、挂机、抢话或切换|交互任务返回非成功状态，不把动作当作已确认。|

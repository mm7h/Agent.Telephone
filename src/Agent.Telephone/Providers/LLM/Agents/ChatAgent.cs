using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Agent.Telephone.Common.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.Exceptions;
using Agent.Telephone.Helpers;
using Agent.Telephone.Providers.LLM.AIContextProviders;
using Agent.Telephone.Providers.LLM.Contexts;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Providers.LLM.Agents
{
    internal class ChatAgent : BaseAgent<ChatAgent>
    {
        private const string FUNCTION_CALL_INTENT_TYPE = "FunctionCall";
        private const string INTENT_LLM_INTENT_TYPE = "IntentLlm";
        private const string HANGUP_FUNCTION_NAME = "HangupCurrentCall";
        private const string ENHANCED_CHAT_PROMPT = """
请在遵守上方角色设定的前提下，额外严格遵守以下回复规则：
1. 回复要像真实语音聊天，语气自然、简短、直接，第一句先回答核心内容，不要先寒暄，不要自我解释。
2. 用户输入可能来自 ASR 转写，允许存在同音字、错别字、断句不准，你要优先理解真实意图，不要纠正用户的识别结果。
3. 除非用户明确要求切换语言，否则始终沿用当前对话语言回复。
4. 输出内容必须适合 TTS 朗读：不要使用 Markdown、代码块、XML/HTML 标签、项目符号或解释性括号动作。
""";

        private ChatClientAgent? _chatClientAgent;

        private AgentSession? _agentSession;
        private SessionChatHistoryProvider? _chatHistoryProvider;
        private bool _allowFunctionCall;

        public ChatAgent(IServiceProvider serviceProvider, ILogger<ChatAgent> logger) : base(SubAgentNames.ChatAgent, serviceProvider, logger)
        {

        }

        public override int Order => 10;

        public override bool Build(LLMAgentBuildConfig agentBuildConfig)
        {
            try
            {
                this.Prompt = agentBuildConfig.AgentSetting.Config.GetConfigValueOrDefault("Prompt")!;
                this._chatHistoryProvider = agentBuildConfig.ChatHistoryProvider;
                string? summaryMemory = agentBuildConfig.AgentSetting.Config.GetValueOrDefault("SummaryMemory");
                string intentType = agentBuildConfig.AgentSetting.Config.GetConfigValueOrDefault("IntentType", "None");
                this._allowFunctionCall = string.Compare(FUNCTION_CALL_INTENT_TYPE, intentType, StringComparison.OrdinalIgnoreCase) == 0;
                bool exposeToolCapabilities = string.Compare(INTENT_LLM_INTENT_TYPE, intentType, StringComparison.OrdinalIgnoreCase) == 0;

                string instructions = this.BuildInstructions(summaryMemory);
                if (this._allowFunctionCall)
                {
                    instructions += "\n需要调用工具时直接调用，不要先生成受理、等待或告别语；执行前提示由系统负责。收到工具结果后直接播报结果或告别，不要重复等待提示，也不要说任务才刚开始。";
                }
                if (exposeToolCapabilities)
                {
                    string toolDescriptions = FunctionToolHelper.BuildToolDescriptions(agentBuildConfig.SessionPrivateProvider.FunctionTools);
                    if (!string.IsNullOrWhiteSpace(toolDescriptions))
                    {
                        instructions += $"""

当前已注册以下能力。仅当用户询问你能做什么或相关能力时，才自然概括这些能力；不要播报内部函数名、参数或实现细节，也不要因为此说明主动调用工具。
{toolDescriptions}
""";
                    }
                }

                ChatOptions chatOptions = new ChatOptions
                {
                    Instructions = instructions,
                    Temperature = 0.5f,
                    MaxOutputTokens = 40,
                    Reasoning = new ReasoningOptions
                    {
                        Effort = ReasoningEffort.None,
                        Output = ReasoningOutput.None
                    }
                };

                if (this._allowFunctionCall && agentBuildConfig.SessionPrivateProvider.FunctionTools.Count > 0)
                {
                    chatOptions.ToolMode = ChatToolMode.Auto;
                    chatOptions.Tools = agentBuildConfig.SessionPrivateProvider.FunctionTools;

                    this.Logger.LogDebug("在 ChatAgent 中可用的 tool 数量：{count}", agentBuildConfig.SessionPrivateProvider.FunctionTools.Count);
                }
                else
                {
                    chatOptions.ResponseFormat = ChatResponseFormat.Text;
                }

                ChatClientAgentOptions chatClientAgentOptions = new ChatClientAgentOptions
                {
                    Name = SubAgentNames.ChatAgent,
                    Description = $"the agent of {SubAgentNames.ChatAgent}",
                    ChatOptions = chatOptions,
                    ChatHistoryProvider = agentBuildConfig.ChatHistoryProvider,
                    RequirePerServiceCallChatHistoryPersistence = true
                };

                IChatClient chatClient = this.ServiceProvider.GetRequiredKeyedService<IChatClient>($"LLM_{agentBuildConfig.AgentSetting.ModelName}");

                this._chatClientAgent = new ChatClientAgent(
                    chatClient: chatClient,
                    options: chatClientAgentOptions,
                    services: this.ServiceProvider
                );

                this._agentSession = this._chatClientAgent.CreateSessionAsync().GetAwaiter().GetResult();

                return true;
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "构建 ChatAgent 失败，ModelName: {modelName}, AgentName: {agentName}", agentBuildConfig.AgentSetting.ModelName, this.AgentName);
                return false;
            }
        }

        private string BuildInstructions(string? summaryMemory)
        {
            StringBuilder instructionsBuilder = new StringBuilder();
            instructionsBuilder.Append(this.Prompt);
            instructionsBuilder.Append("\n\n");
            instructionsBuilder.Append(ENHANCED_CHAT_PROMPT);

            if (!string.IsNullOrWhiteSpace(summaryMemory))
            {
                instructionsBuilder.Append("\n\n");
                instructionsBuilder.Append(summaryMemory);
            }

            return instructionsBuilder.ToString();
        }


        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
        {
            return protocolBuilder.ConfigureRoutes(routeBuilder =>
            {
                routeBuilder
                .AddHandler<WorkflowPreInputs>(this.GenerateChatResponseAsync)
                .AddHandler<IntentDetectionResult>(this.GenerateChatFromDetectionResultAsync);
            })
            .SendsMessage<string>();
        }


        [MessageHandler]
        public async ValueTask GenerateChatResponseAsync(WorkflowPreInputs preInput, IWorkflowContext workflowContext, CancellationToken token)
        {
            if (!this.CheckDeviceRegistered(this.DeviceId))
            {
                throw new SessionNotInitializedException();
            }
            if (this._chatClientAgent is null || this._agentSession is null)
            {
                throw new InvalidOperationException("Chat agent is not built.");
            }

            await foreach (string sentence in this.StreamLLMResponseAsync(preInput.UserMessage, token))
            {
                await workflowContext.SendMessageAsync(sentence, token);
            }
        }

        /// <summary>接收 IntentDetectionResult（未检测到意图），将用户消息转发给 LLM 正常对话</summary>
        [MessageHandler]
        public async ValueTask GenerateChatFromDetectionResultAsync(IntentDetectionResult detectionResult, IWorkflowContext workflowContext, CancellationToken token)
        {
            if (!this.CheckDeviceRegistered(this.DeviceId))
            {
                throw new SessionNotInitializedException();
            }
            if (this._chatClientAgent is null || this._agentSession is null)
            {
                throw new InvalidOperationException("Chat agent is not built.");
            }

            await foreach (string sentence in this.StreamLLMResponseAsync(detectionResult.UserMessage, token))
            {
                await workflowContext.SendMessageAsync(sentence, token);
            }
        }

        /// <summary>流式调用LLM，按标点符号分句逐句返回</summary>
        private async IAsyncEnumerable<string> StreamLLMResponseAsync(string userMessage, [EnumeratorCancellation] CancellationToken token)
        {
            if (!this.CheckDeviceRegistered(this.DeviceId))
            {
                throw new SessionNotInitializedException();
            }
            if (this._chatClientAgent is null || this._agentSession is null)
            {
                throw new InvalidOperationException("Chat agent is not built.");
            }

            this._chatHistoryProvider?.RemoveIncompleteFunctionCalls();

            StringBuilder segmentResponse = new StringBuilder();

            await foreach (string content in this.StreamReplyTextAsync(userMessage, token))
            {
                string text = MarkdownCleaner.CleanMarkdown(Regex.Unescape(content));
                segmentResponse.Append(text);
                string currentSegment = segmentResponse.ToString();
                Match match = DialogueHelper.SENTENCE_SPLIT_REGEX.Match(currentSegment);
                while (match.Success)
                {
                    int splitPosition = match.Index + match.Length;
                    string sentence = currentSegment.Substring(0, splitPosition);

                    yield return sentence;

                    string remaining = currentSegment.Substring(splitPosition);
                    segmentResponse.Clear();
                    segmentResponse.Append(remaining);
                    currentSegment = remaining;
                    match = DialogueHelper.SENTENCE_SPLIT_REGEX.Match(currentSegment);
                }
            }

            // 处理 LLM 回复内容无法被句子分隔的情况
            if (segmentResponse.Length > 0)
            {
                string sentence = segmentResponse.ToString();
                yield return sentence;
            }
        }

        private async IAsyncEnumerable<string> StreamReplyTextAsync(string userMessage, [EnumeratorCancellation] CancellationToken token)
        {
            StringBuilder reply = new();
            bool calledHangup = false;
            await foreach (AgentResponseUpdate update in this._chatClientAgent!.RunStreamingAsync(userMessage, this._agentSession, cancellationToken: token))
            {
                if (!this._allowFunctionCall)
                {
                    yield return update.Text ?? string.Empty;
                    continue;
                }

                calledHangup |= update.Contents.OfType<FunctionCallContent>()
                    .Any(call => string.Equals(call.Name, HANGUP_FUNCTION_NAME, StringComparison.OrdinalIgnoreCase));

                // 自动调用会混入调用前文本；等工具边界确定后只播报最后一次结果之后的回复。
                if (update.Contents.Any(static content => content is FunctionCallContent or FunctionResultContent))
                {
                    reply.Clear();
                    continue;
                }

                if (update.Role != ChatRole.Tool)
                {
                    reply.Append(update.Text);
                }
            }

            if (this._allowFunctionCall && reply.Length > 0)
            {
                string finalReply = reply.ToString();
                yield return calledHangup ? RemoveRepeatedFarewell(finalReply) : finalReply;
            }
        }

        private static string RemoveRepeatedFarewell(string reply)
        {
            if (reply.Length % 2 != 0)
            {
                return reply;
            }

            int halfLength = reply.Length / 2;
            ReadOnlySpan<char> firstHalf = reply.AsSpan(0, halfLength);
            return firstHalf.SequenceEqual(reply.AsSpan(halfLength)) ? firstHalf.ToString() : reply;
        }

        public override void Dispose()
        {
        }
    }
}

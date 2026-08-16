using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Agent.Telephone.Common.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.Exceptions;
using Agent.Telephone.Helpers;
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
        private const string ENHANCED_CHAT_PROMPT = """
请在遵守上方角色设定的前提下，额外严格遵守以下回复规则：
1. 回复要像真实语音聊天，语气自然、简短、直接，第一句先回答核心内容，不要先寒暄，不要自我解释。
2. 用户输入可能来自 ASR 转写，允许存在同音字、错别字、断句不准，你要优先理解真实意图，不要纠正用户的识别结果。
3. 除非用户明确要求切换语言，否则始终沿用当前对话语言回复。
4. 输出内容必须适合 TTS 朗读：不要使用 Markdown、代码块、XML/HTML 标签、项目符号或解释性括号动作。
""";

        private ChatClientAgent? _chatClientAgent;

        private AgentSession? _agentSession;
        private PrivateProvider? _sessionPrivateProvider;
        private bool _allowFunctionCall;

        public ChatAgent(IServiceProvider serviceProvider, ILogger<ChatAgent> logger) : base(SubAgentNames.ChatAgent, serviceProvider, logger)
        {

        }

        public override int Order => 10;

        public void SetChatHistory(List<ChatMessage> chatHistory)
        {
            if (this._agentSession is null)
            {
                throw new InvalidOperationException("Chat agent is not built.");
            }

            this._agentSession.SetInMemoryChatHistory(chatHistory, jsonSerializerOptions: JsonHelper.OPTIONS);
        }

        public override bool Build(LLMAgentBuildConfig agentBuildConfig)
        {
            try
            {
                this.Prompt = agentBuildConfig.AgentSetting.Config.GetConfigValueOrDefault("Prompt")!;
                string? summaryMemory = agentBuildConfig.AgentSetting.Config.GetValueOrDefault("SummaryMemory");
                string intentType = agentBuildConfig.AgentSetting.Config.GetConfigValueOrDefault("IntentType", "None");
                this._allowFunctionCall = string.Compare(FUNCTION_CALL_INTENT_TYPE, intentType, StringComparison.OrdinalIgnoreCase) == 0;
                
                this._sessionPrivateProvider = agentBuildConfig.SessionPrivateProvider;

                string instructions = this.BuildInstructions(summaryMemory);
                IChatClient chatClient = this.ServiceProvider.GetRequiredKeyedService<IChatClient>($"LLM_{agentBuildConfig.AgentSetting.ModelName}");


                ChatOptions chatOptions = new ChatOptions
                {
                    Instructions = instructions,
                    Temperature = 0.5f,
                    MaxOutputTokens = 40,
                };
                if (!this._allowFunctionCall)
                {
                    chatOptions.ResponseFormat = ChatResponseFormat.Text;
                }

                ChatClientAgentOptions chatClientAgentOptions = new ChatClientAgentOptions
                {
                    Name = SubAgentNames.ChatAgent,
                    Description = $"the agent of {SubAgentNames.ChatAgent}",
                    ChatOptions = chatOptions
                };

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

            StringBuilder allResponse = new StringBuilder();
            StringBuilder segmentResponse = new StringBuilder();

            ChatClientAgentRunOptions runOptions = new ChatClientAgentRunOptions(new ChatOptions
            {
                Reasoning = new ReasoningOptions
                {
                    Effort = ReasoningEffort.None,
                    Output = ReasoningOutput.None
                },
                ToolMode = this._allowFunctionCall ? ChatToolMode.Auto : ChatToolMode.None,
                Tools = (this._allowFunctionCall && this._sessionPrivateProvider?.FunctionTools.Count > 0)
                    ? this._sessionPrivateProvider.FunctionTools
                    : null
            });
            await foreach (AgentResponseUpdate update in this._chatClientAgent.RunStreamingAsync(userMessage, this._agentSession, runOptions, token))
            {
                string content = update.Text ?? string.Empty;
                string text = MarkdownCleaner.CleanMarkdown(Regex.Unescape(content));
                segmentResponse.Append(text);
                string currentSegment = segmentResponse.ToString();
                Match match = DialogueHelper.SENTENCE_SPLIT_REGEX.Match(currentSegment);
                while (match.Success)
                {
                    int splitPosition = match.Index + match.Length;
                    string sentence = currentSegment.Substring(0, splitPosition);

                    allResponse.Append(sentence);
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
                allResponse.Append(sentence);
                yield return sentence;
            }
        }

        public override void Dispose()
        {
        }
    }
}

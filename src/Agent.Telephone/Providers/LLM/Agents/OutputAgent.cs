using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Helpers;
using Agent.Telephone.Providers.LLM.Contexts;
using Agent.Telephone.Common.Configs;

namespace Agent.Telephone.Providers.LLM.Agents
{
    internal class OutputAgent : BaseAgent<OutputAgent>
    {
        public OutputAgent(IServiceProvider serviceProvider, ILogger<OutputAgent> logger) : base(SubAgentNames.OutputAgent, serviceProvider, logger)
        {
        }

        public override int Order => 99;

        public override bool Build(LLMAgentBuildConfig buildConfig)
        {
            return true;
        }

        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
        {
            return protocolBuilder.ConfigureRoutes(routeBuilder =>
            {
                routeBuilder
                    .AddHandler<string>(this.HandleChatSentenceAsync);
            })
            .YieldsOutput<WorkflowOutputs>();
        }

        /// <summary>对话路径：解析ChatAgent发来的单句文本（含Emotion标识），yield WorkflowOutputs</summary>
        [MessageHandler]
        public async ValueTask HandleChatSentenceAsync(string sentence, IWorkflowContext context, CancellationToken token)
        {
            string cleanContent = DialogueHelper.GetStringNoPunctuationOrEmoji(sentence);
            if (string.IsNullOrWhiteSpace(cleanContent)) return;

            await context.YieldOutputAsync(new WorkflowOutputs(false, cleanContent), token);
        }

        public override void Dispose()
        {

        }
    }
}

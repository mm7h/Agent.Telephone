using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Sample.Server.FunctionTools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class HangupCallFunctionToolTests
{
    [Fact]
    public void HangupCurrentCall_DelegatesToCallControl()
    {
        var callControl = new RecordingAssistantControl();
        var tool = new HangupCall
        {
            Logger = NullLogger.Instance,
            CallControl = callControl,
        };

        FunctionReturn<string> result = tool.HangupCurrentCall();

        Assert.True(callControl.HangupRequested);
        Assert.Equal(ToolAction.Silent, result.Next);
    }

    private sealed class RecordingAssistantControl : IAssistantControl
    {
        public string? CallerNumber => null;
        public string? AssistantNumber => "10000";
        public bool IsCallActive => true;
        public bool HangupRequested { get; private set; }

        public Task<AssistantSwitchResult> SwitchAssistantAsync(
            string targetAssistantNumber,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void HangupCurrentCall()
        {
            this.HangupRequested = true;
        }

        public Task<DtmfInputResult> RequestDtmfInputAsync(
            DtmfKey keys,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

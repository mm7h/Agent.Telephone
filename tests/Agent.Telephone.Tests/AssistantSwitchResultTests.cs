using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.FunctionTools;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class AssistantSwitchResultTests
{
    [Theory]
    [InlineData(AssistantSwitchStatus.Accepted, true)]
    [InlineData(AssistantSwitchStatus.InvalidTarget, false)]
    [InlineData(AssistantSwitchStatus.UnknownAssistant, false)]
    [InlineData(AssistantSwitchStatus.CurrentAssistant, false)]
    [InlineData(AssistantSwitchStatus.CallEnded, false)]
    [InlineData(AssistantSwitchStatus.Failed, false)]
    public void Succeeded_ReflectsWhetherRequestWasAccepted(
        AssistantSwitchStatus status,
        bool expected)
    {
        AssistantSwitchResult result = new(status, "10086");

        Assert.Equal(expected, result.Succeeded);
    }

    [Fact]
    public async Task SwitchAssistantAsync_DelegatesToCallControlAndReturnsSilentResultAsync()
    {
        StubCallControl callControl = new();
        AssistantSwitchFunctionTool tool = new()
        {
            CallControl = callControl,
        };

        FunctionReturn<AssistantSwitchResult> response = await tool.SwitchAssistantAsync("10010");

        Assert.Equal("10010", callControl.TargetAssistantNumber);
        Assert.Equal(AssistantSwitchStatus.Accepted, response.Result?.Status);
        Assert.Equal(ToolAction.Silent, response.Next);
    }

    [Fact]
    public async Task RequestDtmfInputAsync_DelegatesToCallControlAsync()
    {
        StubCallControl callControl = new();
        DtmfInputFunctionTool tool = new()
        {
            CallControl = callControl,
        };

        FunctionReturn<DtmfInputResult> response = await tool.RequestDtmfInputAsync(
            "请按一",
            DtmfKey.One | DtmfKey.Pound);

        Assert.Equal(DtmfKey.One | DtmfKey.Pound, callControl.RequestedDtmfKeys);
        Assert.True(response.Result?.Succeeded);
        Assert.Equal("请按一", response.Response);
    }

    private sealed class StubCallControl : IAssistantControl
    {
        public string? CallerNumber => "10001";

        public string? AssistantNumber => "10086";

        public bool IsCallActive => true;

        public string? TargetAssistantNumber { get; private set; }

        public DtmfKey RequestedDtmfKeys { get; private set; }

        public Task<AssistantSwitchResult> SwitchAssistantAsync(
            string targetAssistantNumber,
            CancellationToken cancellationToken = default)
        {
            this.TargetAssistantNumber = targetAssistantNumber;
            return Task.FromResult(
                new AssistantSwitchResult(
                    AssistantSwitchStatus.Accepted,
                    targetAssistantNumber));
        }

        public Task<DtmfInputResult> RequestDtmfInputAsync(
            DtmfKey keys,
            CancellationToken cancellationToken = default)
        {
            this.RequestedDtmfKeys = keys;
            return Task.FromResult(new DtmfInputResult(DtmfInputStatus.Accepted));
        }
    }
}

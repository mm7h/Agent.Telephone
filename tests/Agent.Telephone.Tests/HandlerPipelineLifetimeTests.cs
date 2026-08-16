using System.Threading.Channels;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class HandlerPipelineLifetimeTests
{
    [Fact]
    public void Dispose_CompletesChannelsAndWaitsForHandlerTasksBeforeDisposingHandlers()
    {
        Channel<int> channel = Channel.CreateUnbounded<int>();
        var handler = new TrackingHandler();
        Task consumerTask = Task.Run(async () =>
        {
            await foreach (int _ in channel.Reader.ReadAllAsync())
            {
            }

            handler.ConsumerCompleted = true;
        });
        var pipeline = new HandlerPipeline();
        pipeline.InitHandlerPipeline(
            [handler],
            [() => channel.Writer.TryComplete()],
            [consumerTask],
            NullLogger.Instance);

        pipeline.Dispose();
        pipeline.Dispose();

        Assert.True(handler.ConsumerCompleted);
        Assert.Equal(1, handler.DisposeCount);
    }

    private sealed class TrackingHandler : IHandler
    {
        public string HandlerName => nameof(TrackingHandler);
        public ActiveCallContext ActiveCallContext { get; set; } = null!;
        public bool ConsumerCompleted { get; set; }
        public int DisposeCount { get; private set; }
        public bool Build() => true;

        public void Dispose()
        {
            Assert.True(this.ConsumerCompleted);
            this.DisposeCount++;
        }
    }
}

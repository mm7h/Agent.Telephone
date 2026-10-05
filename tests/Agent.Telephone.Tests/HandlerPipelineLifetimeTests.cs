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

    [Fact]
    public async Task DisposeAsync_YieldsUntilTheCurrentHandlerTaskCompletesAsync()
    {
        var handler = new TrackingHandler();
        var handlerCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new HandlerPipeline();
        pipeline.InitHandlerPipeline(
            [handler],
            [],
            [handlerCompletion.Task],
            NullLogger.Instance);

        Task disposal = pipeline.DisposeAsync();

        Assert.False(disposal.IsCompleted);
        handler.ConsumerCompleted = true;
        handlerCompletion.SetResult();
        await disposal;

        Assert.Equal(1, handler.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_UnblocksBackpressuredWriterAndWaitsForConsumerAsync()
    {
        Channel<int> channel = Channel.CreateBounded<int>(1);
        await channel.Writer.WriteAsync(1);
        Task pendingWrite = channel.Writer.WriteAsync(2).AsTask();
        Assert.False(pendingWrite.IsCompleted);
        TaskCompletionSource continueReading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TrackingHandler handler = new();
        Task consumer = ConsumeAsync();
        HandlerPipeline pipeline = new();
        pipeline.InitHandlerPipeline(
            [handler],
            [() => channel.Writer.TryComplete()],
            [consumer],
            NullLogger.Instance);

        Task disposal = pipeline.DisposeAsync();
        Assert.Same(disposal, pipeline.DisposeAsync());
        await Assert.ThrowsAsync<ChannelClosedException>(() => pendingWrite);
        Assert.False(disposal.IsCompleted);
        continueReading.SetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.DisposeCount);

        async Task ConsumeAsync()
        {
            await continueReading.Task;
            await foreach (int _ in channel.Reader.ReadAllAsync())
            {
            }
            handler.ConsumerCompleted = true;
        }
    }

    [Fact]
    public async Task DisposeAsync_ContinuesCleanupWhenAnotherHandlerThrowsAsync()
    {
        TrackingHandler handler = new() { ConsumerCompleted = true };
        HandlerPipeline pipeline = new();
        pipeline.InitHandlerPipeline(
            [handler, new ThrowingHandler()],
            [],
            [],
            NullLogger.Instance);

        await pipeline.DisposeAsync();
        Assert.Equal(1, handler.DisposeCount);
    }

    private sealed class ThrowingHandler : IHandler
    {
        public string HandlerName => nameof(ThrowingHandler);
        public ActiveCallContext ActiveCallContext { get; set; } = null!;
        public bool Build() => true;
        public void Dispose() => throw new InvalidOperationException("Cleanup failed.");
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

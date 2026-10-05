using Agent.Telephone.Protocol.WebSocket;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class WebSocketClientTests
{
    [Fact]
    public async Task ConnectAsync_CancelledBeforeAcquiringLock_DoesNotReleaseUnacquiredSemaphoreAsync()
    {
        using var client = new WebSocketClient(null);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await client.ConnectAsync("wss://localhost", cancellationSource.Token);
    }
}

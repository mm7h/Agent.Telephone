using Agent.Telephone.Providers.TTS.Huoshan;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class HuoshanBidirectionTtsTests
{
    [Fact]
    public void Dispose_WithoutOpeningWebSocket_DoesNotWaitForConnectionFinished()
    {
        var tts = new HuoshanBidirectionTTS(null!, NullLogger<HuoshanBidirectionTTS>.Instance);
        Stopwatch stopwatch = Stopwatch.StartNew();

        tts.Dispose();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("WebSocketClient_OnClose")]
    [InlineData("WebSocketClient_OnError")]
    public void WebSocketCallbacks_DoNotThrowWhenCreatingFailureException(string callbackName)
    {
        var tts = new HuoshanBidirectionTTS(null!, NullLogger<HuoshanBidirectionTTS>.Instance);
        Type baseType = typeof(HuoshanStreamTTS<HuoshanBidirectionTTS>);
        MethodInfo callback = baseType.GetMethod(callbackName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments = callbackName == "WebSocketClient_OnClose"
            ? new object?[] { WebSocketCloseStatus.NormalClosure, "connection disconnected normally" }
            : new object?[] { WebSocketError.ConnectionClosedPrematurely, "无法优雅地关闭 WebSocket 连接。" };

        Exception? exception = Record.Exception(() => callback.Invoke(tts, arguments));

        Assert.Null(exception);
    }
}

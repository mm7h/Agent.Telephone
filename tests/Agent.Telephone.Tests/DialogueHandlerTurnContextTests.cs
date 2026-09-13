using System.Collections.Concurrent;
using System.Reflection;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Handlers.AIAdapterHandlers;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class DialogueHandlerTurnContextTests
{
    [Fact]
    public void DialogueHandler_CachesTurnContextsByTurnId()
    {
        FieldInfo? contextsField = typeof(DialogueHandler).GetField("_turnContexts", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(contextsField);
        Assert.Equal(typeof(ConcurrentDictionary<,>), contextsField.FieldType.GetGenericTypeDefinition());
        Assert.Equal(typeof(long), contextsField.FieldType.GetGenericArguments()[0]);
        Assert.NotNull(typeof(DialogueHandler).GetNestedType("DialogueTurnContext", BindingFlags.NonPublic));
        Assert.Null(typeof(DialogueHandler).GetField("_turn", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void DialogueTurnContext_KeepsTheEntireDeferredFarewell()
    {
        Type contextType = typeof(DialogueHandler).GetNestedType("DialogueTurnContext", BindingFlags.NonPublic)!;
        ConstructorInfo constructor = contextType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
        var turn = new OfflineDialogueTurn("1", "sip:1001@device.test", "10000", "user", "assistant", "再见");
        object context = constructor.Invoke([turn]);
        MethodInfo capture = contextType.GetMethod("CaptureDeferredHangupSegment", BindingFlags.Instance | BindingFlags.Public)!;
        PropertyInfo deferredSegment = contextType.GetProperty("DeferredHangupSegment", BindingFlags.Instance | BindingFlags.Public)!;
        var empty = new OutSegment();
        empty.Initialize(" ", isFirst: false, isLast: false);
        var first = new OutSegment();
        first.Initialize("好的", isFirst: false, isLast: false, paragraphId: "p1", sentenceId: "s1");
        var second = new OutSegment();
        second.Initialize("再见", isFirst: false, isLast: true, paragraphId: "p2", sentenceId: "s2");

        capture.Invoke(context, [empty]);
        capture.Invoke(context, [first]);
        capture.Invoke(context, [second]);

        object snapshot = deferredSegment.GetValue(context)
            ?? throw new InvalidOperationException("Expected a deferred hangup segment.");
        Type snapshotType = snapshot.GetType();
        Assert.Equal("好的。再见", snapshotType.GetProperty("Content")!.GetValue(snapshot));
        Assert.Equal("p1", snapshotType.GetProperty("ParagraphId")!.GetValue(snapshot));
        Assert.Equal("s1", snapshotType.GetProperty("SentenceId")!.GetValue(snapshot));
    }
}

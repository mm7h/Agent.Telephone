using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.ObjectPoolPolicies;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class OutAudioSegmentTests
{
    [Fact]
    public void Return_ResetsAudioFrameMarkers()
    {
        var policy = new OutAudioSegmentPolicy();
        OutAudioSegment segment = policy.Create();
        segment.Initialize(
            audioData: [0.25f],
            isFirstSegment: true,
            isLastSegment: true,
            isFirstFrame: true,
            isLastFrame: true);

        bool returned = policy.Return(segment);

        Assert.True(returned);
        Assert.Empty(segment.AudioData);
        Assert.False(segment.IsFirstSegment);
        Assert.False(segment.IsLastSegment);
        Assert.False(segment.IsFirstFrame);
        Assert.False(segment.IsLastFrame);
    }
}

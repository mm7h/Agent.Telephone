using Agent.Telephone.Providers.AudioProcessor;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class DefaultAudioProcessorTests
{
    [Fact]
    public void ApplyInboundAudioGain_AmplifiesAndClampsSamples()
    {
        float[] audio = [-1f, -0.1f, 0f, 0.01f, 0.1f, 1f];

        DefaultAudioProcessor.ApplyInboundAudioGain(audio);

        Assert.Equal(-1f, audio[0]);
        Assert.Equal(-0.19952623f, audio[1], 6);
        Assert.Equal(0f, audio[2]);
        Assert.Equal(0.019952623f, audio[3], 6);
        Assert.Equal(0.19952623f, audio[4], 6);
        Assert.Equal(1f, audio[5]);
    }
}

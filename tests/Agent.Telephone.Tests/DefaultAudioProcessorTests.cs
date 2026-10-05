using Agent.Telephone.Providers.AudioProcessor;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class DefaultAudioProcessorTests
{
    [Fact]
    public void ApplyInboundAudioGain_AmplifiesAndClampsSamples()
    {
        float[] audio = [-0.1f, 0.01f, 0.1f];

        DefaultAudioProcessor.ApplyInboundAudioGain(audio);

        Assert.Equal(-1f, audio[0]);
        Assert.Equal(0.15848932f, audio[1], 6);
        Assert.Equal(1f, audio[2]);
    }
}

using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Media.Abstractions.Common.Dtos;
using Agent.Telephone.Media.Mixers;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class AudioStreamProcessorTests
{
    [Fact]
    public void AddDataAfterStop_ReopensStreamWithoutDiscardingBufferedAudio()
    {
        using var processor = new AudioStreamProcessor(
            AudioType.TTS,
            8000,
            1,
            20,
            new AudioMixerConfig());
        float[] firstSegment = [0.1f];
        float[] secondSegment = [0.2f];

        processor.AddData(firstSegment);
        processor.Stop();
        processor.AddData(secondSegment);

        float[] mixedFrame = processor.GetFrameDataWithPartialSupport(2, out _, out _)!;

        Assert.False(processor.IsStopping);
        Assert.Equal([0.1f, 0.2f], mixedFrame);
    }
}

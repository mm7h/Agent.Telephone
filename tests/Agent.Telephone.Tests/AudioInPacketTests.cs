using Agent.Telephone.Common.Contexts;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class AudioInPacketTests
{
    [Fact]
    public void TrimOldAudio_PreservesFullVadDetectionWindowByDefault()
    {
        var audioInPacket = new AudioInPacket();
        float[] audio = Enumerable.Range(1, 10 * 512).Select(static value => (float)value).ToArray();

        audioInPacket.PushAudio(audio);
        audioInPacket.TrimOldAudio();

        Assert.Equal(audio[^4096..], audioInPacket.GetAllAudio());
    }
}

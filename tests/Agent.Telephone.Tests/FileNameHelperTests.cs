using Agent.Telephone.Helpers;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class FileNameHelperTests
{
    [Fact]
    public void CreateAudioFileName_UsesReadableCallMetadata()
    {
        string result = FileNameHelper.CreateAudioFileName("tts", "1000", "10000", "1_1", ".pcm");

        Assert.Equal("tts_1000_10000_1_1.pcm", result);
    }

    [Fact]
    public void GetIndex_RemovesDeviceIdPrefix()
    {
        string result = FileNameHelper.GetIndex(
            "sip-device:v1:default:1000@192.168.3.9_1_2",
            "sip-device:v1:default:1000@192.168.3.9");

        Assert.Equal("1_2", result);
    }
}

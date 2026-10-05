using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Helpers;
using Agent.Telephone.Management;
using Agent.Telephone.Media.Abstractions;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.ASR.Aliyun;
using Agent.Telephone.Providers.TTS;
using Agent.Telephone.Providers.TTS.Aliyun;
using Agent.Telephone.Providers.TTS.Huoshan;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class AliyunProviderTests
{
    [Fact]
    public void RealtimeAsr_BuildsSupportedStreamingConfiguration()
    {
        using var asr = new AliyunRealtimeASR(NullLogger<AliyunRealtimeASR>.Instance);

        bool built = asr.Build(new ModelSetting
        {
            ModelName = "AliyunRealtime",
            Config = new Dictionary<string, string>
            {
                ["ApiKey"] = "test-key",
                ["ModelName"] = "qwen-audio-3.0-asr-flash-streaming",
                ["SegmentDurationMs"] = "100"
            }
        });

        Assert.True(built);
        Assert.True(asr.IsStreaming);
    }

    [Fact]
    public void RealtimeAsr_RejectsUnsupportedPacketDuration()
    {
        using var asr = new AliyunRealtimeASR(NullLogger<AliyunRealtimeASR>.Instance);

        bool built = asr.Build(new ModelSetting
        {
            ModelName = "AliyunRealtime",
            Config = new Dictionary<string, string>
            {
                ["ApiKey"] = "test-key",
                ["SegmentDurationMs"] = "10"
            }
        });

        Assert.False(built);
    }

    [Fact]
    public void RealtimeTts_RunTaskUsesFixed24KhzPcm()
    {
        var options = new AliyunRealtimeTtsOptions(
            "wss://dashscope.aliyuncs.com/api-ws/v1/inference",
            "cosyvoice-v3-flash",
            "longanfengyue",
            50,
            1.0f,
            1.0f,
            [],
            null,
            5,
            30);
        using var session = new AliyunRealtimeTtsTurnSession(null!, options, null, static (_, _) => { });
        MethodInfo buildRequest = typeof(AliyunRealtimeTtsTurnSession).GetMethod(
            "BuildRunTaskRequest",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        JsonObject request = JsonNode.Parse(JsonHelper.Serialize(buildRequest.Invoke(session, null)!))!.AsObject();

        Assert.Equal("pcm", request["payload"]!["parameters"]!["format"]!.GetValue<string>());
        Assert.Equal(24000, request["payload"]!["parameters"]!["sample_rate"]!.GetValue<int>());
    }

    [Fact]
    public void HttpTts_RequestUsesFixed24KhzPcm()
    {
        var options = new AliyunHttpTtsOptions(
            "https://workspace.cn-beijing.maas.aliyuncs.com/api/v1/services/audio/tts/SpeechSynthesizer",
            "test-key",
            "cosyvoice-v3-flash",
            "longanfengyue",
            false,
            50,
            1.0f,
            1.0f,
            [],
            null,
            120);
        MethodInfo createRequest = typeof(AliyunHttpTTS).GetMethod(
            "CreateRequest",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        JsonObject request = JsonNode.Parse(JsonHelper.Serialize(createRequest.Invoke(null, [options, "你好"])!))!.AsObject();

        Assert.Equal("pcm", request["input"]!["format"]!.GetValue<string>());
        Assert.Equal(24000, request["input"]!["sample_rate"]!.GetValue<int>());
    }

    [Fact]
    public void HttpTts_BuildRejectsNon24KhzOutput()
    {
        using var tts = new AliyunHttpTTS(null!, null!, NullLogger<AliyunHttpTTS>.Instance);

        bool built = tts.Build(new ModelSetting
        {
            ModelName = "AliyunHttp",
            Config = new Dictionary<string, string>
            {
                ["ApiKey"] = "test-key",
                ["WorkspaceId"] = "workspace",
                ["Voice"] = "longanfengyue",
                ["SampleRate"] = "16000"
            }
        });

        Assert.False(built);
    }

    [Fact]
    public void HttpTts_SseEventStreamsPcmAudio()
    {
        using var tts = new AliyunHttpTTS(null!, null!, NullLogger<AliyunHttpTTS>.Instance);
        var callback = new RecordingTtsCallback();
        var segment = new OutSegment();
        segment.Initialize("你好", true, true, "paragraph", "sentence");
        Type stateType = typeof(AliyunHttpTTS).GetNestedType("SynthesisState", BindingFlags.NonPublic)!;
        object state = Activator.CreateInstance(
            stateType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [callback, segment],
            culture: null)!;
        MethodInfo tryProcess = typeof(AliyunHttpTTS).GetMethod(
            "TryProcessSseEvent",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            new StringBuilder("{\"output\":{\"type\":\"sentence-begin\",\"audio\":{\"data\":\"AEA=\"},\"finish_reason\":\"stop\"}}"),
            state,
            false
        ];

        bool processed = (bool)tryProcess.Invoke(tts, arguments)!;

        Assert.True(processed);
        Assert.True((bool)arguments[2]!);
        Assert.Equal(["你好"], callback.StartedSentences);
        Assert.Single(callback.AudioFrames);
        Assert.Single(callback.AudioFrames[0]);
    }

    [Fact]
    public void ProviderManager_RegistersAliyunAndHttpTtsClientCachesByProviderKey()
    {
        TelephoneConfig config = new()
        {
            ModelConfig = new ModelConfig
            {
                ConfiguredSettings = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>
                {
                    ["VAD"] = [],
                    ["ASR"] = new()
                    {
                        ["AliyunRealtime"] = []
                    },
                    ["LLM"] = new()
                    {
                        ["Test"] = new()
                        {
                            ["BaseUrl"] = "https://example.test",
                            ["ApiKey"] = "test-key",
                            ["ModelName"] = "test-model"
                        }
                    },
                    ["TTS"] = new()
                    {
                        ["AliyunRealtimeTTS"] = [],
                        ["AliyunHttp"] = [],
                        ["HuoshanHttp"] = [],
                        ["HuoshanHttpV3"] = []
                    }
                }
            }
        };
        IHostBuilder hostBuilder = ProviderManager.RegisterServices(new HostBuilder(), config)
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<IAudioEditor>(_ => null!);
            });
        using IHost host = hostBuilder.Build();

        Assert.IsType<AliyunRealtimeASR>(host.Services.GetRequiredKeyedService<IAsr>("aliyun-realtime"));
        Assert.IsType<AliyunRealtimeTTS>(host.Services.GetRequiredKeyedService<ITts>("aliyun-realtime-t-t-s"));
        Assert.IsType<AliyunHttpTTS>(host.Services.GetRequiredKeyedService<ITts>("aliyun-http"));
        Assert.IsType<HuoshanHttpTTS>(host.Services.GetRequiredKeyedService<ITts>("huoshan-http"));
        Assert.IsType<HuoshanHttpV3TTS>(host.Services.GetRequiredKeyedService<ITts>("huoshan-http-v3"));
    }

    private sealed class RecordingTtsCallback : ITtsEventCallback
    {
        public List<string> StartedSentences { get; } = [];

        public List<float[]> AudioFrames { get; } = [];

        public void OnBeforeProcessing(string sentence, bool isFirstSegment, bool isLastSegment)
        {
        }

        public void OnProcessing(float[] audioData, bool isFirstFrame, bool isLastFrame)
        {
            this.AudioFrames.Add(audioData);
        }

        public void OnProcessed(string sentence, bool isFirstSegment, bool isLastSegment, Agent.Telephone.Common.Enums.TtsGenerateResult ttsGenerateResult)
        {
        }

        public void OnSentenceStart(string sentence, string sentenceId)
        {
            this.StartedSentences.Add(sentence);
        }

        public void OnSentenceEnd(string sentence, string sentenceId)
        {
        }
    }
}

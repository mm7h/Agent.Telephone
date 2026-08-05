using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.OfflineDialogue;
using Agent.Telephone.Providers.TTS;
using Agent.Telephone.Resources;
using Agent.Telephone.Sample.Server.MessageStore;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class OfflineDialogueProviderTests : IDisposable
{
    private const string DeviceNumber = "1001";
    private const string AssistantNumber = "10086";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UnfinishedTurnKeepsTtsSavedFileForNextInboundCallAsync()
    {
        string audioPath = Path.Combine(this._root, "reply.mp3");
        Directory.CreateDirectory(this._root);
        await File.WriteAllBytesAsync(audioPath, [1, 2, 3]);
        SqliteMessageStore store = new(new SqliteMessageStoreOptions
        {
            DatabasePath = Path.Combine(this._root, "messages.db")
        });
        using TestCallSession session = CreateActiveCall();
        session.Call.AIAgentContext.PrivateProvider.SetTts(new SavedFileTts(audioPath));
        using OfflineDialogueProvider provider = new(
            store,
            null!,
            NullLogger<OfflineDialogueProvider>.Instance);

        await provider.TrackGeneratedAudioAsync(
            session.Call,
            turnId: 1,
            sentenceId: "sentence-1",
            isLastSegment: true,
            CancellationToken.None);
        await provider.MarkTurnPlaybackCompletedAsync(
            session.Call,
            turnId: 1,
            fullyPlayed: false,
            CancellationToken.None);

        MessageRecord message = Assert.Single(await store.GetUnreadAsync(
            session.Call.UserAor,
            AssistantNumber));
        Assert.Equal(audioPath, message.AudioPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(this._root))
        {
            Directory.Delete(this._root, true);
        }
    }

    private static TestCallSession CreateActiveCall()
    {
        SIPTransport transport = new();
        SIPRequest register = CreateRegisterRequest();
        DeviceContext device = new(
            transport,
            register,
            SIPURI.ParseSIPURI($"sip:{DeviceNumber}@192.0.2.10:5060"),
            300,
            [new AssistantConfig { DialingNumber = AssistantNumber }]);
        Assert.True(device.TryBeginCallback());
        var source = new AudioExtrasSource(
            new AudioEncoder(SupportedAudioFormats.SupportedSDPAudioFormat),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        var mediaSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = source });
        var userAgent = new SIPUserAgent(transport, SIPEndPoint.Empty, false);
        Assert.True(device.TryAttachCallbackCallSession(
            $"sip:{DeviceNumber}@device.test",
            AssistantNumber,
            userAgent,
            mediaSession,
            out ActiveCallContext? activeCall));
        Assert.NotNull(activeCall);
        device.EndCallback();
        return new TestCallSession(transport, device, activeCall);
    }

    private static SIPRequest CreateRegisterRequest()
    {
        SIPRequest request = SIPRequest.GetRequest(
            SIPMethodsEnum.REGISTER,
            SIPURI.ParseSIPURI("sip:registrar@127.0.0.1"));
        request.Header.From = new SIPFromHeader(
            null,
            SIPURI.ParseSIPURI($"sip:{DeviceNumber}@device.test"),
            CallProperties.CreateNewTag());
        request.Header.To = new SIPToHeader(
            null,
            SIPURI.ParseSIPURI("sip:registrar@server.test"),
            null);
        request.Header.Contact = [new SIPContactHeader(
            null,
            SIPURI.ParseSIPURI($"sip:{DeviceNumber}@192.0.2.10:5060")) { Expires = 300 }];
        request.Header.Expires = 300;
        return request;
    }

    private sealed class SavedFileTts : BaseProvider<SavedFileTts, ModelSetting>, ITts
    {
        private readonly string _audioPath;

        public SavedFileTts(string audioPath)
            : base(NullLogger<SavedFileTts>.Instance)
        {
            this._audioPath = audioPath;
        }

        public override string ProviderType => "tts";

        public override string ModelName => nameof(SavedFileTts);

        public override bool Build(ModelSetting settings) => true;

        public string? GetSavedAudioFilePath(string sentenceId) => this._audioPath;

        public void RegisterDevice(string deviceId, ITtsEventCallback callback)
        {
        }

        public Task SynthesisAsync(Workflow<OutSegment> workflow, CancellationToken token) => Task.CompletedTask;

        public override void Dispose()
        {
        }
    }

    private sealed class TestCallSession : IDisposable
    {
        private readonly SIPTransport _transport;
        private readonly DeviceContext _device;
        private ActiveCallContext? _call;

        public TestCallSession(SIPTransport transport, DeviceContext device, ActiveCallContext call)
        {
            this._transport = transport;
            this._device = device;
            this._call = call;
        }

        public ActiveCallContext Call => this._call
            ?? throw new ObjectDisposedException(nameof(TestCallSession));

        public void Dispose()
        {
            ActiveCallContext? call = Interlocked.Exchange(ref this._call, null);
            if (call is not null)
            {
                this._device.CloseCallSession(call);
            }

            this._device.Dispose();
            this._transport.Dispose();
        }
    }
}

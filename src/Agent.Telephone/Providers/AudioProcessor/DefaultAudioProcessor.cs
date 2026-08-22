using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Helpers;
using Agent.Telephone.Media.Abstractions;
using Agent.Telephone.Media.Abstractions.Dtos;
using Agent.Telephone.Resources;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using ISIPSorceryAudioCodec = SIPSorcery.Media.AudioEncoder;

namespace Agent.Telephone.Providers.AudioProcessor
{
    internal sealed class DefaultAudioProcessor : BaseProvider<DefaultAudioProcessor, ModelSetting>, IAudioProcessor
    {
        private readonly ISIPSorceryAudioCodec _audioCodec;
        private readonly IAudioMixer _audioMixer;
        private readonly IAudioSubtitleRegister _audioSubtitleRegister;
        private readonly IAudioPromptPlayer _audioPromptPlayer;
        private int _mixerSampleRate;

        /* 音频输入增益倍数
         +0 dB	1.000000	
        +1 dB	1.122018
        +2 dB	1.258925
        +3 dB	1.412538
        +4 dB	1.584893
        +5 dB	1.778279
        +6 dB	1.995262
        +7 dB	2.238721
        +8 dB	2.511886
        +9 dB	2.818383
        +10 dB	3.162278
        +12 dB	3.981072
        +11 dB	3.548134
        +13 dB	4.466836
        +14 dB	5.011872
        +15 dB	5.623413
        +16 dB	6.309573
        +17 dB	7.079458
        +18 dB	7.943282
        +19 dB	8.912509
        +20 dB	10.000000
        +21 dB	11.220185
        +22 dB	12.589254
        +23 dB	14.125375
		+24 dB	15.848932
         */

        private const float InboundAudioGain = 1.9952623f; // +6 dB

        public DefaultAudioProcessor(
            ISIPSorceryAudioCodec audioCodec,
            IAudioMixer audioMixer,
            IAudioSubtitleRegister audioSubtitleRegister,
            IAudioPromptPlayer audioPromptPlayer,
            ILogger<DefaultAudioProcessor> logger)
            : base(logger)
        {
            this._audioCodec = audioCodec;
            this._audioMixer = audioMixer;
            this._audioSubtitleRegister = audioSubtitleRegister;
            this._audioPromptPlayer = audioPromptPlayer;
            this._audioMixer.OnMixedAudioDataAvailable += this.FireOnMixedAudioData;
        }

        public event Action<float[], bool, bool, string?>? OnMixedAudioDataAvailable;

        public override string ModelName => nameof(DefaultAudioProcessor);
        public override string ProviderType => "audio processor";

        public override bool Build(ModelSetting modelSetting)
        {
            return true;
        }

        public bool InitializeMixer(int outputSampleRate, int outputChannels, int frameDuration)
        {
            if (this._audioMixer.IsInitialized)
            {
                return this._audioMixer.OutputSampleRate == outputSampleRate
                    && this._audioMixer.OutputChannels == outputChannels
                    && this._audioMixer.FrameDuration == frameDuration;
            }

            if (!this._audioMixer.Initialize(outputSampleRate, outputChannels, frameDuration))
            {
                return false;
            }

            this._mixerSampleRate = outputSampleRate;
            return true;
        }

        public bool TryBeginInitialGreeting(ActiveCallContext activeCall)
        {
            if (!HasInitialGreeting(activeCall.AssistantConfig))
            {
                this.Logger.LogWarning(
                    "Agent {AssistantNumber} 的首呼问候配置无效，设备 {DeviceId} 将直接进入正常通话。",
                    activeCall.AssistantConfig.DialingNumber,
                    activeCall.DeviceId);
                return false;
            }

            activeCall.PauseUserAudioInput();
            return true;
        }

        public void StartInitialGreeting(
            ActiveCallContext activeCall,
            Func<string, string, string, CancellationToken, Task<bool>> synthesizePrompt)
        {
            if (!activeCall.TryAcquireUse(out IDisposable? lease) || lease is null)
            {
                return;
            }

            _ = this.PlayInitialGreetingAsync(
                activeCall,
                synthesizePrompt,
                lease);
        }

        public Task<bool> PlayCachedPromptAsync(
            ActiveCallContext activeCall,
            IReadOnlyList<string> relativeFilePaths,
            CancellationToken cancellationToken)
        {
            return this.PlayPromptAsync(
                activeCall,
                onAudioData => this._audioPromptPlayer.PlayCachedAudioFilesAsync(
                    relativeFilePaths,
                    activeCall.NegotiatedAudioFormat.ClockRate,
                    onAudioData,
                    cancellationToken),
                cancellationToken);
        }

        public Task<bool> PlayFilePromptAsync(
            ActiveCallContext activeCall,
            string filePath,
            CancellationToken cancellationToken)
        {
            return this.PlayPromptAsync(
                activeCall,
                onAudioData => this._audioPromptPlayer.PlayFileAsync(
                    filePath,
                    activeCall.NegotiatedAudioFormat.ClockRate,
                    activeCall.PacketTimeMs,
                    onAudioData,
                    cancellationToken),
                cancellationToken);
        }

        public Task<float[]> DecodeAsync(byte[] encodedData, AudioFormat format, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            this.EnsureTelephoneCodec(format);

            short[] pcm16 = this._audioCodec.DecodeAudio(encodedData, format);
            short[] resampled = PcmResampler.Resample(
                pcm16,
                format.ClockRate,
                AudioProcessSettings.OutputToModelSampleRate);
            float[] pcmData = resampled.PcmShortToFloat();
            ApplyInboundAudioGain(pcmData);
            return Task.FromResult(pcmData);
        }

        internal static void ApplyInboundAudioGain(float[] audioData)
        {
            for (int index = 0; index < audioData.Length; index++)
            {
                audioData[index] = Math.Clamp(audioData[index] * InboundAudioGain, -1f, 1f);
            }
        }

        public Task<byte[]> EncodeAsync(float[] pcmData, AudioFormat format, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            this.EnsureTelephoneCodec(format);

            short[] pcm16 = pcmData.PcmFloatToShort();
            return Task.FromResult(this._audioCodec.EncodeAudio(pcm16, format));
        }

        public void ProcessAudio(AudioType audioType, float[] audioData, string? sentenceId)
        {
            if (audioType == AudioType.TTS && audioData.Length > 0 && this._mixerSampleRate > 0)
            {
                short[] source = audioData.PcmFloatToShort();
                short[] resampled = PcmResampler.Resample(
                    source,
                    AudioProcessSettings.ModelToInputSampleRate,
                    this._mixerSampleRate);
                this._audioMixer.AddAudioData(audioType, resampled.PcmShortToFloat(), sentenceId);
                return;
            }

            this._audioMixer.AddAudioData(audioType, audioData, sentenceId);
        }

        public void CompleteStream(AudioType audioType)
        {
            this._audioMixer.StopAudioStream(audioType);
        }

        public void ClearAllBuffers()
        {
            this._audioSubtitleRegister.ClearAll();
            this._audioMixer.ClearAllBuffers();
        }

        public void RegisterSubtitle(string sentenceId, AudioType audioType, TtsStatus ttsStatus, string text)
        {
            this._audioSubtitleRegister.Register(sentenceId, audioType, ttsStatus, text);
        }

        public bool GetSubtitle(string sentenceId, out AudioSubtitle subtitle)
        {
            return this._audioSubtitleRegister.GetSubtitle(sentenceId, out subtitle);
        }

        public override void Dispose()
        {
            this.ClearAllBuffers();
            this._audioMixer.OnMixedAudioDataAvailable -= this.FireOnMixedAudioData;
            this._audioMixer.Dispose();
            this._audioSubtitleRegister.Dispose();
        }

        private void FireOnMixedAudioData(float[] audioPcmData, bool isFirst, bool isLast, string? sentenceId)
        {
            try
            {
                this.OnMixedAudioDataAvailable?.Invoke(audioPcmData, isFirst, isLast, sentenceId);
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "转发混音音频时失败。");
            }
        }

        private void EnsureTelephoneCodec(AudioFormat format)
        {
            if (format.Codec is not (AudioCodecsEnum.PCMU or AudioCodecsEnum.PCMA))
            {
                throw new NotSupportedException($"不支持的电话音频编码：{format.Codec}。");
            }
        }

        private async Task PlayInitialGreetingAsync(
            ActiveCallContext activeCall,
            Func<string, string, string, CancellationToken, Task<bool>> synthesizePrompt,
            IDisposable lease)
        {
            using (lease)
            {
                try
                {
                    string? helloMessage = GetHelloMessage(activeCall.AssistantConfig);
                    if (helloMessage is null)
                    {
                        this.Logger.LogWarning("客服首呼问候文本为空，设备 {DeviceId} 将直接进入正常通话。", activeCall.DeviceId);
                        return;
                    }

                    IReadOnlyList<string> audioFiles = GetGreetingAudioFiles(activeCall.AssistantConfig.DialingNumber);
                    if (audioFiles.Count == 0)
                    {
                        this.Logger.LogWarning("客服号码 {AssistantNumber} 不是纯数字，无法播放首呼问候。", activeCall.AssistantConfig.DialingNumber);
                        return;
                    }

                    activeCall.MarkPlayingPrompt();
                    bool fixedAudioPlayed = await this.PlayCachedPromptAsync(
                        activeCall,
                        audioFiles,
                        activeCall.CallToken);
                    if (!fixedAudioPlayed)
                    {
                        this.Logger.LogWarning("客服首呼固定音频播放失败，设备 {DeviceId} 将直接进入正常通话。", activeCall.DeviceId);
                        return;
                    }

                    Task<bool> playbackCompleted = activeCall.BeginPromptPlayback();
                    if (playbackCompleted.IsCompleted)
                    {
                        this.Logger.LogWarning("客服首呼 TTS 播放已被占用，设备 {DeviceId} 将直接进入正常通话。", activeCall.DeviceId);
                        return;
                    }

                    bool synthesisStarted = await synthesizePrompt(
                        helloMessage,
                        $"greeting-{activeCall.CallId}",
                        $"greeting-{Guid.NewGuid():N}",
                        activeCall.CallToken);
                    if (!synthesisStarted)
                    {
                        this.Logger.LogWarning("客服首呼 TTS 合成失败，设备 {DeviceId} 将直接进入正常通话。", activeCall.DeviceId);
                        return;
                    }

                    if (!await playbackCompleted.WaitAsync(activeCall.CallToken))
                    {
                        this.Logger.LogWarning("客服首呼 TTS 未完整播放，设备 {DeviceId} 将直接进入正常通话。", activeCall.DeviceId);
                    }
                }
                catch (OperationCanceledException) when (activeCall.CallToken.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    this.Logger.LogWarning(exception, "播放客服首呼问候失败，设备 {DeviceId} 将直接进入正常通话。", activeCall.DeviceId);
                }
                finally
                {
                    activeCall.CompletePromptPlayback(fullyPlayed: false);
                    if (!activeCall.CallToken.IsCancellationRequested)
                    {
                        activeCall.ResumeUserAudioInput();
                        activeCall.DeviceContext.MarkCallConnected(activeCall);
                    }
                }
            }
        }

        private async Task<bool> PlayPromptAsync(
            ActiveCallContext activeCall,
            Func<Action<float[]>, Task<bool>> supplyAudio,
            CancellationToken cancellationToken)
        {
            AudioFormat format = activeCall.NegotiatedAudioFormat;
            if (format.IsEmpty() || !this.InitializeMixer(format.ClockRate, outputChannels: 1, activeCall.PacketTimeMs))
            {
                return false;
            }

            Task<bool> playbackCompleted = activeCall.BeginPromptPlayback();
            if (playbackCompleted.IsCompleted)
            {
                return false;
            }

            bool streamCompleted = false;
            try
            {
                bool supplied = await supplyAudio(audioData =>
                    this.ProcessAudio(AudioType.SystemNotification, audioData, sentenceId: null));
                if (!supplied)
                {
                    return false;
                }

                this.CompleteStream(AudioType.SystemNotification);
                streamCompleted = true;
                return await playbackCompleted.WaitAsync(cancellationToken);
            }
            finally
            {
                if (!streamCompleted)
                {
                    this.CompleteStream(AudioType.SystemNotification);
                }

                activeCall.CompletePromptPlayback(fullyPlayed: false);
            }
        }

        internal static bool HasInitialGreeting(AssistantConfig assistant)
        {
            return !string.IsNullOrWhiteSpace(assistant.DialingNumber) &&
                assistant.DialingNumber.All(static character => character is >= '0' and <= '9') &&
                assistant.HelloMessageTempletes?.Any(static template => !string.IsNullOrWhiteSpace(template)) == true;
        }

        private static string? GetHelloMessage(AssistantConfig assistant)
        {
            string[] templates = (assistant.HelloMessageTempletes ?? [])
                .Where(static template => !string.IsNullOrWhiteSpace(template))
                .ToArray();
            return templates.Length == 0
                ? null
                : templates[Random.Shared.Next(templates.Length)];
        }

        internal static IReadOnlyList<string> GetGreetingAudioFiles(string assistantNumber)
        {
            if (string.IsNullOrWhiteSpace(assistantNumber) || assistantNumber.Any(static character => character < '0' || character > '9'))
            {
                return [];
            }

            var paths = new List<string>(assistantNumber.Length + 1);
            foreach (char character in assistantNumber)
            {
                paths.Add($"numbers/{character}.mp3");
            }

            paths.Add(PromptAudioSettings.GreetingServiceAgentAudio);
            return paths;
        }
    }
}

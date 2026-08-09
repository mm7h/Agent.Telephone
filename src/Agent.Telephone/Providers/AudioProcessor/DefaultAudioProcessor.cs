using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Helpers;
using Agent.Telephone.Media.Abstractions;
using Agent.Telephone.Media.Abstractions.Dtos;
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
        private int _mixerSampleRate;

        public DefaultAudioProcessor(
            ISIPSorceryAudioCodec audioCodec,
            IAudioMixer audioMixer,
            IAudioSubtitleRegister audioSubtitleRegister,
            ILogger<DefaultAudioProcessor> logger)
            : base(logger)
        {
            this._audioCodec = audioCodec;
            this._audioMixer = audioMixer;
            this._audioSubtitleRegister = audioSubtitleRegister;
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

        public Task<float[]> DecodeAsync(byte[] encodedData, AudioFormat format, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            this.EnsureTelephoneCodec(format);

            short[] pcm16 = this._audioCodec.DecodeAudio(encodedData, format);
            short[] resampled = PcmResampler.Resample(
                pcm16,
                format.ClockRate,
                AudioProcessSettings.OutputToModelSampleRate);
            return Task.FromResult(resampled.PcmShortToFloat());
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
    }
}

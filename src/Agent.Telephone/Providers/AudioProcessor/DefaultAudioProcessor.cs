using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Helpers;
using Agent.Telephone.Resources.Audio;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace Agent.Telephone.Providers.AudioProcessor
{
    internal class DefaultAudioProcessor : BaseProvider<DefaultAudioProcessor, ModelSetting>, IAudioProcessor
    {
        private readonly AudioEncoder _audioCodec;
        private readonly FfmpegAudioPlayer _filePlayer;

        public DefaultAudioProcessor(AudioEncoder audioCodec,FfmpegAudioPlayer filePlayer,
            ILogger<DefaultAudioProcessor> logger) : base(logger)
        {
            this._audioCodec = audioCodec;
            this._filePlayer = filePlayer;
        }

        public override string ModelName => nameof(DefaultAudioProcessor);
        public override string ProviderType => "audio processor";



        public override bool Build(ModelSetting modelSetting)
        {
            return true;
        }

        public Task<float[]> DecodeAsync(byte[] rtpAudioDate, AudioFormat format, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (format.Codec != AudioCodecsEnum.PCMU && format.Codec != AudioCodecsEnum.PCMA)
            {
                throw new NotSupportedException($"不支持的音频编码： {format.Codec}.");
            }

            short[] pcm16 = this._audioCodec.DecodeAudio(rtpAudioDate, format);
            short[] resampled = PcmResampler.Resample(pcm16, format.ClockRate, AudioProcessSettings.OutputToModelSampleRate);

            return Task.FromResult(resampled.PcmShortToFloat());
        }

        public Task<byte[]> EncodeAsync(float[] pcmData, AudioFormat format, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (format.Codec != AudioCodecsEnum.PCMU && format.Codec != AudioCodecsEnum.PCMA)
            {
                throw new NotSupportedException($"Unsupported telephone codec {format.Codec}.");
            }

            short[] pcmBytes = pcmData.PcmFloatToShort();
            short[] resampled = PcmResampler.Resample(pcmBytes, AudioProcessSettings.ModelToInputSampleRate, format.ClockRate);
            byte[] rtpPacket = this._audioCodec.EncodeAudio(resampled, format);

            return Task.FromResult(rtpPacket);
        }

        public Task<byte[]> DecodeFileToPcmWaveAsync(string? path, CancellationToken token = default)
        {
            return this._filePlayer.DecodeFileToPcmWaveAsync(path, token);
        }

        public Task<bool> PlayFileAsync(string? path, VoIPMediaSession mediaSession, AudioFormat audioFormat, CancellationToken token)
        {
            return this._filePlayer.PlayFileAsync(path, mediaSession, audioFormat, token);
        }

        public override void Dispose()
        {
            this._audioCodec.Dispose();
        }


    }

}

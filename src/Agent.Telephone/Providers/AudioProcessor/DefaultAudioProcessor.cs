using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Helpers;
using Agent.Telephone.Resources;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using ISIPSorceryAudioCodec = SIPSorcery.Media.AudioEncoder;

namespace Agent.Telephone.Providers.AudioProcessor
{
    internal class DefaultAudioProcessor : BaseProvider<DefaultAudioProcessor, ModelSetting>, IAudioProcessor
    {
        private readonly ISIPSorceryAudioCodec _audioCodec;
        private readonly IAudioEditor _audioEditor;

        public DefaultAudioProcessor(ISIPSorceryAudioCodec audioCodec, IAudioEditor audioEditor,
            ILogger<DefaultAudioProcessor> logger) : base(logger)
        {
            this._audioCodec = audioCodec;
            this._audioEditor = audioEditor;
        }

        public event Action<uint, byte[], bool, bool>? OnAudioDataAvailable;

        public override string ModelName => nameof(DefaultAudioProcessor);
        public override string ProviderType => "audio processor";



        public override bool Build(ModelSetting modelSetting)
        {
            this._audioEditor.OnAudioDataAvailable += this.FireOnAudioDataAvailable;
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

        public Task<bool> PlayFileAsync(string? path, VoIPMediaSession mediaSession, AudioFormat audioFormat, CancellationToken token)
        {
            return this._audioEditor.PlayFileAsync(path, mediaSession, audioFormat, token);
        }

        private void FireOnAudioDataAvailable(
            uint sampleRate,
            byte[] data,
            bool isFirst,
            bool isLast)
        {
            this.OnAudioDataAvailable?.Invoke(sampleRate, data, isFirst, isLast);
        }

        public override void Dispose()
        {
            this._audioEditor.OnAudioDataAvailable -= this.FireOnAudioDataAvailable;
        }


    }

}

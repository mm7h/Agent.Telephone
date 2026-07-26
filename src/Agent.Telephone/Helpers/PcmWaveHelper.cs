using System.Buffers.Binary;

namespace Agent.Telephone.Helpers
{
    internal static class PcmWaveHelper
    {
        public static byte[] CreateMono16BitWave(IReadOnlyList<float> samples, int sampleRate)
        {
            ArgumentNullException.ThrowIfNull(samples);
            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }

            const short channels = 1;
            const short bitsPerSample = 16;
            const int headerLength = 44;
            int dataLength = checked(samples.Count * sizeof(short));
            byte[] wave = new byte[checked(headerLength + dataLength)];
            Span<byte> header = wave.AsSpan(0, headerLength);

            "RIFF"u8.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + dataLength);
            "WAVE"u8.CopyTo(header[8..]);
            "fmt "u8.CopyTo(header[12..]);
            BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
            BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
            BinaryPrimitives.WriteInt16LittleEndian(header[22..], channels);
            BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * channels * bitsPerSample / 8);
            BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)(channels * bitsPerSample / 8));
            BinaryPrimitives.WriteInt16LittleEndian(header[34..], bitsPerSample);
            "data"u8.CopyTo(header[36..]);
            BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataLength);

            Span<byte> pcm = wave.AsSpan(headerLength);
            for (int index = 0; index < samples.Count; index++)
            {
                float clamped = Math.Clamp(samples[index], -1f, 1f);
                short value = (short)Math.Round(clamped * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm[(index * sizeof(short))..], value);
            }

            return wave;
        }

        public static byte[] CreateMono16BitWave(IReadOnlyList<short> samples, int sampleRate)
        {
            ArgumentNullException.ThrowIfNull(samples);
            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }

            const int headerLength = 44;
            int dataLength = checked(samples.Count * sizeof(short));
            byte[] wave = new byte[checked(headerLength + dataLength)];
            Span<byte> header = wave.AsSpan(0, headerLength);

            "RIFF"u8.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + dataLength);
            "WAVE"u8.CopyTo(header[8..]);
            "fmt "u8.CopyTo(header[12..]);
            BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
            BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
            BinaryPrimitives.WriteInt16LittleEndian(header[22..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * sizeof(short));
            BinaryPrimitives.WriteInt16LittleEndian(header[32..], sizeof(short));
            BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
            "data"u8.CopyTo(header[36..]);
            BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataLength);

            Span<byte> pcm = wave.AsSpan(headerLength);
            for (int index = 0; index < samples.Count; index++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm[(index * sizeof(short))..],
                    samples[index]);
            }

            return wave;
        }
    }
}

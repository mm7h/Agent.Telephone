using SherpaOnnx;

namespace Agent.Telephone.Common.Contexts
{
    internal sealed class AudioOutputPacket : IDisposable
    {
        private const int DefaultBufferCapacity = 960 * 100;
        private readonly CircularBuffer _audioBuffer = new(DefaultBufferCapacity);
        private bool _disposed;

        public void PushAudio(float[] audioData)
        {
            if (!this._disposed && audioData.Length > 0)
            {
                this._audioBuffer.Push(audioData);
            }
        }

        public float[] GetAllAudio()
        {
            return this._disposed || this._audioBuffer.Size == 0
                ? []
                : this._audioBuffer.Get(this._audioBuffer.Head, this._audioBuffer.Size);
        }

        public void PopFrames(int count)
        {
            if (!this._disposed && count > 0 && this._audioBuffer.Size >= count)
            {
                this._audioBuffer.Pop(count);
            }
        }

        public void Reset()
        {
            if (!this._disposed)
            {
                this._audioBuffer.Reset();
            }
        }

        public void Dispose()
        {
            if (!this._disposed)
            {
                this._disposed = true;
                this._audioBuffer.Dispose();
            }
        }
    }
}

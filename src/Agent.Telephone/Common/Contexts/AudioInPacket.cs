using SherpaOnnx;

namespace Agent.Telephone.Common.Contexts
{
    internal class AudioInPacket
    {
        private bool _released;
        private const int DEFAULT_BUFFER_CAPACITY = 960 * 100;
        // Preserve the entire Silero VAD voice-confirmation window before trimming silence.
        private const int PRE_SPEECH_BUFFER_SAMPLES = 512 * 8;
        private readonly CircularBuffer _audioBuffer;

        public AudioInPacket()
        {
            this._audioBuffer = new CircularBuffer(DEFAULT_BUFFER_CAPACITY);
        }

        public long LastHaveVoiceTime { get; set; }

        public long HaveVoiceLatestTime { get; set; }

        public bool HaveVoice { get; set; }

        public bool VoiceStop { get; set; }

        public int BufferHead => this._audioBuffer.Head;

        public int BufferSize => this._audioBuffer.Size;

        public void PushAudio(float[] audioData)
        {
            if (!this._released && audioData.Length > 0)
            {
                this._audioBuffer.Push(audioData);
            }
        }

        public float[] GetFrames(int startIndex, int frameSize)
        {
            if (this._released || this._audioBuffer.Size < startIndex + frameSize)
            {
                return [];
            }
            return this._audioBuffer.Get(startIndex, frameSize);
        }

        public void PopFrames(int count)
        {
            if (!this._released && count > 0 && this._audioBuffer.Size >= count)
            {
                this._audioBuffer.Pop(count);
            }
        }

        public float[] GetAllAudio()
        {
            if (this._released || this._audioBuffer.Size == 0)
            {
                return [];
            }
            return this._audioBuffer.Get(this._audioBuffer.Head, this._audioBuffer.Size);
        }

        public float[] GetLatestAudio(int maxSamples)
        {
            if (this._released || this._audioBuffer.Size == 0 || maxSamples <= 0)
            {
                return [];
            }

            int sampleCount = Math.Min(maxSamples, this._audioBuffer.Size);
            int startIndex = this._audioBuffer.Head + this._audioBuffer.Size - sampleCount;
            return this._audioBuffer.Get(startIndex, sampleCount);
        }

        public void ResetAudioBuffer()
        {
            if (!this._released)
            {
                this._audioBuffer.Reset();
            }
        }

        public void Reset()
        {
            this.VoiceStop = false;
            this.HaveVoice = false;
            this.HaveVoiceLatestTime = 0;
            this.LastHaveVoiceTime = 0;
            this.ResetAudioBuffer();
        }

        public void Release()
        {
            this._released = true;
            this._audioBuffer.Dispose();
        }

        public void TrimOldAudio(int keepFrames = Agent.Telephone.Common.Constants.AudioProcessSettings.OutputToModelSampleRate * Agent.Telephone.Common.Constants.AudioProcessSettings.StreamingAsrPreRollMilliseconds / 1000)
        {
            if (!this._released && this._audioBuffer.Size > keepFrames)
            {
                this._audioBuffer.Pop(this._audioBuffer.Size - keepFrames);
            }
        }
    }
}

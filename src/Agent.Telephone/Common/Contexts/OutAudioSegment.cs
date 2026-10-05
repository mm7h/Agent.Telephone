using Agent.Telephone.Abstractions.Common.Enums;

namespace Agent.Telephone.Common.Contexts
{
    internal class OutAudioSegment : OutSegment
    {
        private float[] _audioData = Array.Empty<float>();

        public float[] AudioData => this._audioData;

        public AudioType AudioType { get; private set; }

        public bool IsFirstFrame { get; set; }

        public bool IsLastFrame { get; set; }

        public void Initialize(
            float[]? audioData = null,
            AudioType audioType = AudioType.None,
            string? content = null,
            bool isFirstSegment = false,
            bool isLastSegment = false,
            bool isFirstFrame = false,
            bool isLastFrame = false,
            string? sentenceId = null)
        {
            this._audioData = audioData ?? Array.Empty<float>();
            this.AudioType = audioType;
            this.IsFirstFrame = isFirstFrame;
            this.IsLastFrame = isLastFrame;
            base.Initialize(content ?? string.Empty, isFirstSegment, isLastSegment, sentenceId: sentenceId);
        }

        public override void Reset()
        {
            this._audioData = Array.Empty<float>();
            this.AudioType = AudioType.None;
            this.IsFirstFrame = false;
            this.IsLastFrame = false;
            base.Reset();
        }
    }
}

namespace Agent.Telephone.Common.Contexts
{
    internal class OutAudioSegment : OutSegment
    {
        private float[] _audioData = Array.Empty<float>();

        public OutAudioSegment() : base()
        {
        }

        public float[] AudioData => this._audioData;
        public bool IsFirstFrame { get; set; }
        public bool IsLastFrame { get; set; }

        public void Initialize(float[]? audioData = null,
            string? content = null,
            bool isFirstSegment = false,
            bool isLastSegment = false,
            bool isFirstFrame = false,
            bool isLastFrame = false,
            string? sentenceId = null)
        {
            this._audioData = audioData ?? Array.Empty<float>();
            this.IsFirstFrame = isFirstFrame;
            this.IsLastFrame = isLastFrame;
            base.Initialize(content ?? string.Empty, isFirstSegment, isLastSegment, sentenceId: sentenceId);
        }

        public override void Reset()
        {
            this._audioData = Array.Empty<float>();
            this.IsFirstFrame = false;
            this.IsLastFrame = false;
            base.Reset();
        }


    }
}

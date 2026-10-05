using Agent.Telephone.Abstractions.Common.Enums;

namespace Agent.Telephone.Media.Abstractions.Dtos
{
    public struct AudioSubtitle
    {
        public AudioSubtitle(string sentenceId, AudioType audioType, string subtitleText, TtsStatus ttsStatus, DateTime registerTime)
        {
            this.SentenceId = sentenceId;
            this.AudioType = audioType;
            this.SubtitleText = subtitleText;
            this.TtsStatus = ttsStatus;
            this.RegisterTime = registerTime;
        }

        public string SentenceId { get; } = string.Empty;
        public AudioType AudioType { get; }
        public string SubtitleText { get; } = string.Empty;
        public DateTime RegisterTime { get; }
        public TtsStatus TtsStatus { get; }
    }
}



using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Media.Abstractions.Dtos;

namespace Agent.Telephone.Media.Abstractions
{
    public interface IAudioSubtitleRegister : IDisposable
    {
        void Register(string sentenceId, AudioType audioType, TtsStatus ttsStatus, string subtitleText);
        bool GetSubtitle(string sentenceId, out AudioSubtitle subtitle);
        void ClearAll();
    }

}

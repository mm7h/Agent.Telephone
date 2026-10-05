using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Common.Enums;

namespace Agent.Telephone.Providers.TTS
{
    internal interface ITtsEventCallback
    {
        void OnBeforeProcessing(string sentence, bool isFirstSegment, bool isLastSegment);
        void OnProcessing(float[] audioData, bool isFirstFrame, bool isLastFrame);
        void OnProcessed(string sentence, bool isFirstSegment, bool isLastSegment, TtsGenerateResult ttsGenerateResult);
        void OnSentenceStart(string sentence, string sentenceId);
        void OnSentenceEnd(string sentence, string sentenceId);
    }
}

namespace Agent.Telephone.Providers.VAD
{
    internal interface IVadEventCallback
    {
        bool IsWaitingForReply => false;
        void OnVoiceStarted();
        void OnVoiceDetected(float[] audioData);
        void OnVoiceSilence();
        void OnLongTermSilence();
    }
}

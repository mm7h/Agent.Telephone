namespace Agent.Telephone.Providers.VAD
{
    internal interface IVadEventCallback
    {
        void OnVoiceStarted();
        void OnVoiceDetected(float[] audioData);
        void OnVoiceSilence();
        void OnLongTermSilence();
    }
}

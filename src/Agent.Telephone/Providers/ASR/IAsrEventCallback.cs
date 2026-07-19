namespace Agent.Telephone.Providers.ASR
{
    internal interface IAsrEventCallback
    {
        void OnSpeechTextConverted(bool success, string text);
    }
}

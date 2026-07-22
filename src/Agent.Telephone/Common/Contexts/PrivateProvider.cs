using Agent.Telephone.Providers;
using Agent.Telephone.Providers.LLM.Contexts;
using Microsoft.Extensions.AI;

namespace Agent.Telephone.Common.Contexts
{
    internal sealed class PrivateProvider : IDisposable
    {

        public PrivateProvider(string deviceId)
        {
            this.DeviceId = deviceId;
            this.FunctionTools = [];
        }

        public string DeviceId { get; }
        public IList<AITool> FunctionTools { get; }
        public IAudioProcessor? AudioProcessor { get; private set; }
        public IVad? Vad { get; private set; }
        public IAsr? Asr { get; private set; }
        public ILlm? Llm { get; private set; }
        public ITts? Tts { get; private set; }

        public void SetAudioProcessor(IAudioProcessor value) => this.AudioProcessor = value;
        public void SetVad(IVad value) => this.Vad = value;
        public void SetAsr(IAsr value) => this.Asr = value;
        public void SetLlm(ILlm value) => this.Llm = value;
        public void SetTts(ITts value) => this.Tts = value;

        public bool TryGetFunctionToolRegistration(string functionName, out FunctionToolRegistration? registration)
        {
            registration = null;
            return false;
        }

        public void Dispose()
        {
            this.Vad?.UnregisterDevice(this.DeviceId);
            this.Asr?.UnregisterDevice(this.DeviceId);
            this.Tts?.UnregisterDevice(this.DeviceId);
            this.AudioProcessor?.Dispose();
            this.Llm?.Dispose();
            this.FunctionTools.Clear();
        }
    }
}

using Agent.Telephone.FunctionTools;
using Agent.Telephone.Providers;
using Agent.Telephone.Providers.LLM.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Microsoft.Extensions.AI;

namespace Agent.Telephone.Common.Contexts
{
    internal sealed class PrivateProvider : IDisposable
    {

        private readonly Dictionary<string, FunctionToolRegistration> _functionToolRegistrations;
        private readonly List<PrivateFunctionTool> _privateFunctionTools;

        public PrivateProvider(string deviceId)
        {
            this.DeviceId = deviceId;
            this.FunctionTools = [];
            this._functionToolRegistrations = new Dictionary<string, FunctionToolRegistration>(StringComparer.OrdinalIgnoreCase);
            this._privateFunctionTools = [];
        }

        public string DeviceId { get; }
        public IList<AITool> FunctionTools { get; }
        public IAudioProcessor? AudioProcessor { get; private set; }
        public IVad? Vad { get; private set; }
        public IAsr? Asr { get; private set; }
        public ILlm? Llm { get; private set; }
        public ITts? Tts { get; private set; }
        public ICallControl? CallControl { get; private set; }
        public IDtmfInput? DtmfInput { get; private set; }

        public void SetAudioProcessor(IAudioProcessor value) => this.AudioProcessor = value;
        public void SetVad(IVad value) => this.Vad = value;
        public void SetAsr(IAsr value) => this.Asr = value;
        public void SetLlm(ILlm value) => this.Llm = value;
        public void SetTts(ITts value) => this.Tts = value;
        public void SetCallControl(ICallControl value) => this.CallControl = value;
        public void SetDtmfInput(IDtmfInput value) => this.DtmfInput = value;

        public DtmfKey GetAvailableDtmfKeys()
        {
            return this._functionToolRegistrations.Values.Aggregate(
                DtmfKey.None,
                static (keys, registration) => keys | registration.DtmfKeys);
        }

        public void ClearCallControl(ICallControl value)
        {
            if (ReferenceEquals(this.CallControl, value))
            {
                this.CallControl = null;
            }
        }
        public List<PrivateFunctionTool> PrivateFunctionTools => this._privateFunctionTools;

        public void AddFunctionToolRegistration(PrivateFunctionTool privateFunctionTool, FunctionToolRegistration registration)
        {
            if (!this._privateFunctionTools.Contains(privateFunctionTool))
            {
                this._privateFunctionTools.Add(privateFunctionTool);
            }
            this.AddFunctionToolRegistration(registration);
        }

        public void AddFunctionToolRegistration(FunctionToolRegistration registration)
        {
            this.FunctionTools.Add(registration.Function);
            this._functionToolRegistrations[registration.Function.Name] = registration;
        }

        public bool TryGetFunctionToolRegistration(string functionName, out FunctionToolRegistration? registration)
        {
            return this._functionToolRegistrations.TryGetValue(functionName, out registration);
        }

        public void Dispose()
        {
            this.Vad?.UnregisterDevice(this.DeviceId);
            this.Asr?.UnregisterDevice(this.DeviceId);
            this.Tts?.UnregisterDevice(this.DeviceId);
            if (this.Vad is { IsSherpaModel: false })
            {
                this.Vad.Dispose();
            }
            if (this.Asr is { IsSherpaModel: false })
            {
                this.Asr.Dispose();
            }
            if (this.Tts is { IsSherpaModel: false })
            {
                this.Tts.Dispose();
            }
            this.AudioProcessor?.Dispose();
            this.Llm?.Dispose();
            this.CallControl?.Dispose();
            this.DtmfInput?.Dispose();
            this.FunctionTools.Clear();
            this._functionToolRegistrations.Clear();
            this._privateFunctionTools.Clear();
        }
    }
}

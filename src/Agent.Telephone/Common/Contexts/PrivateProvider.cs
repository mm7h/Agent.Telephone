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
        private readonly object _lifetimeLock = new();
        private ActiveCallContext? _activeCall;
        private IDisposable? _privateFunctionToolLifetime;
        private bool _disposed;

        public PrivateProvider(string deviceId)
        {
            this.DeviceId = deviceId;
            this.FunctionTools = [];
            this._functionToolRegistrations = new Dictionary<string, FunctionToolRegistration>(StringComparer.OrdinalIgnoreCase);
            this._privateFunctionTools = [];
        }

        public PrivateProvider(ActiveCallContext activeCall)
            : this(activeCall.DeviceId)
        {
            this._activeCall = activeCall;
        }

        public string DeviceId { get; }
        public IList<AITool> FunctionTools { get; }
        public IAudioProcessor? AudioProcessor { get; private set; }
        public IVad? Vad { get; private set; }
        public IAsr? Asr { get; private set; }
        public ILlm? Llm { get; private set; }
        public ITts? Tts { get; private set; }
        public IOfflineDialogue? OfflineDialogue { get; private set; }
        public ICallControl? CallControl { get; private set; }
        public IDtmfInput? DtmfInput { get; private set; }

        public void SetAudioProcessor(IAudioProcessor value) => this.SetProvider(value, static (instance, provider) => instance.AudioProcessor = provider);
        public void SetVad(IVad value) => this.SetProvider(value, static (instance, provider) => instance.Vad = provider);
        public void SetAsr(IAsr value) => this.SetProvider(value, static (instance, provider) => instance.Asr = provider);
        public void SetLlm(ILlm value) => this.SetProvider(value, static (instance, provider) => instance.Llm = provider);
        public void SetTts(ITts value) => this.SetProvider(value, static (instance, provider) => instance.Tts = provider);
        public void SetOfflineDialogue(IOfflineDialogue value) => this.SetProvider(value, static (instance, provider) => instance.OfflineDialogue = provider);
        public void SetCallControl(ICallControl value) => this.SetProvider(value, static (instance, provider) => instance.CallControl = provider);
        public void SetDtmfInput(IDtmfInput value) => this.SetProvider(value, static (instance, provider) => instance.DtmfInput = provider);

        public DtmfKey GetAvailableDtmfKeys()
        {
            return this._functionToolRegistrations.Values.Aggregate(
                DtmfKey.None,
                static (keys, registration) => keys | registration.DtmfKeys);
        }

        public List<PrivateFunctionTool> PrivateFunctionTools => this._privateFunctionTools;

        public void AddFunctionToolRegistration(PrivateFunctionTool privateFunctionTool, FunctionToolRegistration registration)
        {
            lock (this._lifetimeLock)
            {
                this.ThrowIfDisposed();
                if (!this._privateFunctionTools.Contains(privateFunctionTool))
                {
                    this._privateFunctionTools.Add(privateFunctionTool);
                }
                this.AddFunctionToolRegistrationCore(registration);
            }
        }

        public void SetPrivateFunctionToolLifetime(IDisposable lifetime)
        {
            ArgumentNullException.ThrowIfNull(lifetime);

            lock (this._lifetimeLock)
            {
                try
                {
                    this.ThrowIfDisposed();
                    if (this._privateFunctionToolLifetime is not null)
                    {
                        throw new InvalidOperationException("The private function tool lifetime has already been registered.");
                    }

                    this._privateFunctionToolLifetime = lifetime;
                }
                catch
                {
                    lifetime.Dispose();
                    throw;
                }
            }
        }

        public void AddFunctionToolRegistration(FunctionToolRegistration registration)
        {
            lock (this._lifetimeLock)
            {
                this.ThrowIfDisposed();
                this.AddFunctionToolRegistrationCore(registration);
            }
        }

        public bool TryGetFunctionToolRegistration(string functionName, out FunctionToolRegistration? registration)
        {
            return this._functionToolRegistrations.TryGetValue(functionName, out registration);
        }

        public void Dispose()
        {
            ActiveCallContext? activeCall;
            IDisposable? privateFunctionToolLifetime;
            IAudioProcessor? audioProcessor;
            IVad? vad;
            IAsr? asr;
            ILlm? llm;
            ITts? tts;
            IOfflineDialogue? offlineDialogue;
            ICallControl? callControl;
            IDtmfInput? dtmfInput;

            lock (this._lifetimeLock)
            {
                if (this._disposed)
                {
                    return;
                }

                this._disposed = true;
                activeCall = this._activeCall;
                this._activeCall = null;
                privateFunctionToolLifetime = this._privateFunctionToolLifetime;
                this._privateFunctionToolLifetime = null;
                audioProcessor = this.AudioProcessor;
                vad = this.Vad;
                asr = this.Asr;
                llm = this.Llm;
                tts = this.Tts;
                offlineDialogue = this.OfflineDialogue;
                callControl = this.CallControl;
                dtmfInput = this.DtmfInput;

                this.AudioProcessor = null;
                this.Vad = null;
                this.Asr = null;
                this.Llm = null;
                this.Tts = null;
                this.OfflineDialogue = null;
                this.CallControl = null;
                this.DtmfInput = null;
                this.FunctionTools.Clear();
                this._functionToolRegistrations.Clear();
                this._privateFunctionTools.Clear();
            }

            privateFunctionToolLifetime?.Dispose();

            if (activeCall is not null)
            {
                UnregisterProvider(audioProcessor, activeCall);
                UnregisterProvider(vad, activeCall);
                UnregisterProvider(asr, activeCall);
                llm?.UnregisterDevice(activeCall);
                UnregisterProvider(tts, activeCall);
                UnregisterProvider(offlineDialogue, activeCall);
                UnregisterProvider(callControl, activeCall);
                UnregisterProvider(dtmfInput, activeCall);
            }

            if (vad is { IsSherpaModel: false })
            {
                vad.Dispose();
            }
            if (asr is { IsSherpaModel: false })
            {
                asr.Dispose();
            }
            if (tts is { IsSherpaModel: false })
            {
                tts.Dispose();
            }
            audioProcessor?.Dispose();
            llm?.Dispose();
            offlineDialogue?.Dispose();
            callControl?.Dispose();
            dtmfInput?.Dispose();
        }

        private void SetProvider<TProvider>(TProvider provider, Action<PrivateProvider, TProvider> assign)
            where TProvider : class
        {
            ArgumentNullException.ThrowIfNull(provider);

            lock (this._lifetimeLock)
            {
                this.ThrowIfDisposed();
                assign(this, provider);
            }
        }

        private void AddFunctionToolRegistrationCore(FunctionToolRegistration registration)
        {
            if (!string.IsNullOrWhiteSpace(registration.PreExecutionPrompt))
            {
                registration.WithFunction(new PromptedAIFunction(registration.Function, this.DeviceId, this._activeCall));
            }
            this.FunctionTools.Add(registration.Function);
            this._functionToolRegistrations[registration.Function.Name] = registration;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);
        }

        private static void UnregisterProvider<TSettings>(
            IProvider<TSettings>? provider,
            ActiveCallContext activeCall)
            where TSettings : class
        {
            provider?.UnregisterDevice(activeCall);
        }
    }
}

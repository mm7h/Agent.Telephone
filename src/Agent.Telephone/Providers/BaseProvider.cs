using Agent.Telephone.Common.Constants;
using Microsoft.Extensions.Logging;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Helpers;
using Agent.Telephone.Media.Abstractions;
using System.Text.RegularExpressions;
using SIPSorcery.Media;

namespace Agent.Telephone.Providers
{
    internal abstract class BaseProvider<TLogger, TSettings> : IProvider<TSettings> where TSettings : class
    {
        public BaseProvider(ILogger<TLogger> logger)
        {
            this.Logger = logger;
        }
        public abstract string ProviderType { get; }
        public abstract string ModelName { get; }
        public bool IsSherpaModel => this.CheckIsSherpaModel();
        protected string ModelFileFoler => Path.Combine(Environment.CurrentDirectory, "models", this.ProviderType, this.ConvertToKebabCase(this.ModelName));
        protected ActiveCallContext CurrentCall { get; private set; } = null!;
        protected ILogger<TLogger> Logger { get; }
        public abstract bool Build(TSettings settings);

        public abstract void Dispose();

        public virtual void RegisterDevice(ActiveCallContext activeCall)
        {
            ArgumentNullException.ThrowIfNull(activeCall);
            this.CurrentCall = activeCall;
            this.Logger.LogInformation("device注册, device id: {deviceId}, provider type: {providerType}", activeCall.DeviceId, this.ProviderType);
        }

        public virtual void UnregisterDevice(ActiveCallContext activeCall)
        {
            ArgumentNullException.ThrowIfNull(activeCall);
            if (ReferenceEquals(this.CurrentCall, activeCall))
            {
                this.CurrentCall = null!;
            }
            this.Logger.LogInformation("解除device注册, device id: {deviceId}, provider type: {providerType}", activeCall.DeviceId, this.ProviderType);
        }

        public virtual bool CheckDeviceRegistered(string deviceId)
        {
            if (this.CurrentCall is null || !string.Equals(this.CurrentCall.DeviceId, deviceId, StringComparison.Ordinal))
            {
                this.Logger.LogError("在{providerType}中注册了无效的device id", this.ProviderType);
                return false;
            }
            return true;
        }

        protected bool CheckModelExist()
        {
            string modelFilePath = Path.Combine(this.ModelFileFoler, "model.onnx");
            bool exist = File.Exists(modelFilePath);
            if (!exist)
            {
                this.Logger.LogError("在{providerType}中未找到模型文件: {modelFilePath}", this.ProviderType, modelFilePath);
            }
            return exist;
        }

        protected virtual string GenerateId()
        {
            return Guid.NewGuid().ToString("N");
        }

        protected int GetNegotiatedAudioSavingSampleRate()
        {
            int clockRate = this.CurrentCall.NegotiatedAudioFormat.ClockRate;
            return clockRate > 0 ? clockRate : 8000;
        }

        protected static float[] ResampleForAudioSaving(float[] audioData, int sourceSampleRate, int targetSampleRate)
        {
            if (sourceSampleRate == targetSampleRate)
            {
                return audioData;
            }

            short[] source = audioData.PcmFloatToShort();
            return PcmResampler.Resample(source, sourceSampleRate, targetSampleRate).PcmShortToFloat();
        }

        private bool CheckIsSherpaModel()
        {
            switch (this.ProviderType.ToLower())
            {
                case "vad" when SherpaModels.VadModels.Contains(this.ModelName):
                case "asr" when SherpaModels.AsrModels.Contains(this.ModelName):
                case "tts" when SherpaModels.TtsModels.Contains(this.ModelName):
                    return true;
            }
            return false;
        }

        private string ConvertToKebabCase(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return input;
            }

            return Regex.Replace(input, "(?<!^)([A-Z])", "-$1").ToLower();
        }
    }
}

using Agent.Telephone.Common.Constants;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

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
        protected string DeviceId { get; set; } = string.Empty;
        protected ILogger<TLogger> Logger { get; }
        public abstract bool Build(TSettings settings);

        public abstract void Dispose();

        public virtual void RegisterDevice(string deviceId)
        {
            this.DeviceId = deviceId;
            this.Logger.LogInformation("device注册, device id: {deviceId}", deviceId);
        }

        public virtual void UnregisterDevice(string deviceId)
        {
            this.DeviceId = string.Empty;
            this.Logger.LogInformation("解除device注册, device id: {deviceId}", deviceId);
        }

        public virtual bool CheckDeviceRegistered(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(this.DeviceId))
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
                return input;

            return Regex.Replace(input, "(?<!^)([A-Z])", "-$1").ToLower();
        }
    }
}

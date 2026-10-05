using Agent.Telephone.Abstractions.Configs;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Agent.Telephone.Resources.OnnxModels
{
    internal abstract class BaseOnnxModel<TLogger> : BaseResource<TLogger, ModelSetting>
    {
        public BaseOnnxModel(ILogger<TLogger> logger) : base(logger)
        {

        }
        public override string ResourceName => "onnx model";
        public abstract string ModelType { get; }
        public abstract string ModelName { get; }
        protected string ModelFileFoler => Path.Combine(Environment.CurrentDirectory, "models", this.ModelType, this.ConvertToKebabCase(this.ModelName));

        protected bool CheckModelExist()
        {
            string modelFilePath = Path.Combine(this.ModelFileFoler, "model.onnx");
            bool exist = File.Exists(modelFilePath);
            if (!exist)
            {
                this.Logger.LogError("模型文件未找到：{modelFilePath}", modelFilePath);
            }
            return exist;
        }
        private string ConvertToKebabCase(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;

            return Regex.Replace(input, "(?<!^)([A-Z])", "-$1").ToLower();
        }
    }
}

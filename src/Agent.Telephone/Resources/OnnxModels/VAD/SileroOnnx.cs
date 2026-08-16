using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Providers.VAD.Native;
using Agent.Telephone.Resources.OnnxModels.VAD.Models;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Agent.Telephone.Resources.OnnxModels.VAD
{
    /// <summary>
    /// Silero VAD v4 ONNX model wrapper.
    /// This class holds the ONNX InferenceSession and should be registered as a Singleton.
    /// The Infer() method is thread-safe when each caller provides their own SileroModelState.
    /// 
    /// Model inputs (v4):
    /// - input: [batch_size, chunk_samples] - 16kHz audio samples (512 samples per frame)
    /// - sr: [1] - Sample rate as int64
    /// - h: [2, batch_size, 64] - Hidden state
    /// - c: [2, batch_size, 64] - Cell state
    /// 
    /// Model outputs (v4):
    /// - output: [batch_size, 1] - Speech probability
    /// - hn: [2, batch_size, 64] - New hidden state
    /// - cn: [2, batch_size, 64] - New cell state
    /// </summary>
    internal sealed class SileroOnnx : BaseOnnxModel<SileroOnnx>, IVadOnnxModel
    {
        private readonly object _sessionLock = new object();
        
        private InferenceSession? _session;
        private bool _disposed;

        private const int SupportedSampleRate = 16000;
        private const int FrameSize = 512;
        private const int StateSize = 64;

        public SileroOnnx(ILogger<SileroOnnx> logger) : base(logger)
        {
        }

        public override string ModelType => "vad";
        public override string ModelName => nameof(SileroNative);

        public override bool Load(ModelSetting settings)
        {
            if (this.CheckModelExist())
            {
                SessionOptions sessionOptions = new SessionOptions
                {
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = 1,
                    EnableCpuMemArena = true,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                };
                string modelPath = Path.Combine(this.ModelFileFoler, "model.onnx");
                this._session = new InferenceSession(modelPath, sessionOptions);
                this.Logger.LogInformation("Silero VAD v4 ONNX 模型已加载：{modelName}", this.ModelName);
                return true;
            }
            else
            {
                this.Logger.LogError("无效的模型：{modelName}", this.ModelName);
                return false;
            }
        }

        /// <summary>
        /// Creates a new model state for a client session.
        /// Each client should have its own state instance for lock-free concurrent access.
        /// </summary>
        /// <param name="sampleRate">Sample rate (16000)</param>
        public static SileroModelState CreateModelState(int sampleRate)
        {
            return new SileroModelState(sampleRate);
        }

        /// <summary>
        /// Run inference on the model with the given audio samples.
        /// Multiple callers can use this method concurrently with their own SileroModelState.
        /// </summary>
        /// <param name="audioSamples">Audio samples as float array (normalized to [-1, 1])</param>
        /// <param name="sampleRate">Sample rate (16000)</param>
        /// <param name="modelState">Per-client model state for lock-free concurrent access</param>
        /// <returns>Speech probability (0.0 to 1.0)</returns>
        public float Infer(float[] audioSamples, int sampleRate, SileroModelState modelState)
        {
            if (this._disposed)
            {
                throw new ObjectDisposedException(nameof(SileroOnnx), string.Format("对象 {0} 已被释放。", nameof(SileroOnnx)));
            }

            if (this._session is null)
            {
                throw new InvalidOperationException("模型会话未初始化。");
            }

            this.ValidateInput(audioSamples, sampleRate, modelState);

            if (audioSamples.Length != FrameSize)
            {
                throw new ArgumentException(string.Format("样本数量不匹配。期望 {0} 个样本，实际 {1} 个样本，采样率 {2}。", FrameSize, audioSamples.Length, sampleRate));
            }

            const int BatchSize = 1;

            // Prepare input tensors
            var inputTensor = new DenseTensor<float>(audioSamples, new int[] { BatchSize, audioSamples.Length });
            var srTensor = new DenseTensor<long>(new long[] { sampleRate }, new int[] { 1 });
            var hTensor = new DenseTensor<float>(modelState.HiddenState, new int[] { 2, BatchSize, StateSize });
            var cTensor = new DenseTensor<float>(modelState.CellState, new int[] { 2, BatchSize, StateSize });

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", inputTensor),
                NamedOnnxValue.CreateFromTensor("sr", srTensor),
                NamedOnnxValue.CreateFromTensor("h", hTensor),
                NamedOnnxValue.CreateFromTensor("c", cTensor)
            };

            // Run inference (session.Run is thread-safe for reading, but we lock to be extra safe)
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs;
            lock (this._sessionLock)
            {
                outputs = this._session.Run(inputs);
            }

            using (outputs)
            {
                // Extract output probability
                var outputTensor = outputs.First(o => o.Name == "output").AsTensor<float>();
                float speechProb = outputTensor[0];

                // Extract and update hidden state
                var hnTensor = outputs.First(o => o.Name == "hn").AsTensor<float>();
                float[] newHiddenState = new float[2 * BatchSize * StateSize];
                for (int i = 0; i < 2; i++)
                {
                    for (int j = 0; j < BatchSize; j++)
                    {
                        for (int k = 0; k < StateSize; k++)
                        {
                            newHiddenState[i * BatchSize * StateSize + j * StateSize + k] = hnTensor[i, j, k];
                        }
                    }
                }
                modelState.UpdateHiddenState(newHiddenState);

                // Extract and update cell state
                var cnTensor = outputs.First(o => o.Name == "cn").AsTensor<float>();
                float[] newCellState = new float[2 * BatchSize * StateSize];
                for (int i = 0; i < 2; i++)
                {
                    for (int j = 0; j < BatchSize; j++)
                    {
                        for (int k = 0; k < StateSize; k++)
                        {
                            newCellState[i * BatchSize * StateSize + j * StateSize + k] = cnTensor[i, j, k];
                        }
                    }
                }
                modelState.UpdateCellState(newCellState);

                return speechProb;
            }
        }

        private void ValidateInput(float[] audioSamples, int sampleRate, SileroModelState modelState)
        {
            if (audioSamples == null || audioSamples.Length == 0)
            {
                throw new ArgumentException("音频样本为空。");
            }

            if (sampleRate != SupportedSampleRate)
            {
                throw new ArgumentException(string.Format("不支持的采样率：{sampleRate}。仅支持 16000。", sampleRate));
            }

            if (modelState.LastSampleRate != sampleRate)
            {
                this.Logger.LogDebug("采样率从 {lastSr} 更改为 {newSr}，重置模型状态。", modelState.LastSampleRate, sampleRate);
                modelState.Reset();
                modelState.LastSampleRate = sampleRate;
            }
        }

        public override void Dispose()
        {
            if (!this._disposed)
            {
                this._session?.Dispose();
                this._disposed = true;
                this.Logger.LogInformation("Silero VAD v4 ONNX 模型已释放。");
            }
            GC.SuppressFinalize(this);
        }
    }
}

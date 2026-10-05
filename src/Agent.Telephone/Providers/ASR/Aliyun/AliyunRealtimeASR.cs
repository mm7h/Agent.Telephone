using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Constants;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Helpers;
using Agent.Telephone.Protocol.WebSocket;
using Agent.Telephone.Providers.ASR.Contexts;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Providers.ASR.Aliyun
{
    internal sealed class AliyunRealtimeASR : BaseProvider<AliyunRealtimeASR, ModelSetting>, IAsr
    {
        private readonly SemaphoreSlim _streamLock = new(1, 1);
        private IAsrEventCallback? _asrEventCallback;
        private WebSocketClient? _webSocketClient;
        private AliyunRealtimeAsrOptions? _options;
        private AliyunRealtimeAsrUtteranceSession? _activeSession;
        private int _disposed;

        public AliyunRealtimeASR(ILogger<AliyunRealtimeASR> logger)
            : base(logger)
        {
        }

        public override string ProviderType => "asr";

        public override string ModelName => nameof(AliyunRealtimeASR);

        public bool IsStreaming => true;

        public override bool Build(ModelSetting modelSetting)
        {
            this._options = null;
            try
            {
                string apiKey = modelSetting.Config.GetConfigValueOrDefault("ApiKey", string.Empty);
                string modelName = modelSetting.Config.GetConfigValueOrDefault(
                    "ModelName",
                    "qwen-audio-3.0-asr-flash-streaming");
                if (string.IsNullOrWhiteSpace(apiKey) || !IsSupportedModel(modelName))
                {
                    this.Logger.LogWarning("阿里云实时 ASR 配置不完整或模型不受支持。");
                    return false;
                }

                string endpoint = this.ResolveEndpoint(
                    modelSetting.Config.GetConfigValueOrDefault<string?>("Endpoint"),
                    modelSetting.Config.GetConfigValueOrDefault("Region", "cn-beijing"),
                    modelSetting.Config.GetConfigValueOrDefault<string?>("WorkspaceId"));
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    return false;
                }

                int segmentDurationMs = modelSetting.Config.GetConfigValueOrDefault("SegmentDurationMs", 100);
                if (segmentDurationMs is < 20 or > 1000)
                {
                    this.Logger.LogWarning("阿里云实时 ASR 的 SegmentDurationMs 必须介于 20 和 1000。");
                    return false;
                }

                int packetSizeBytes = AudioProcessSettings.OutputToModelSampleRate
                    * AudioProcessSettings.ModelAudioChannels
                    * (AudioProcessSettings.ModelAudioBitsPerSample / 8)
                    * segmentDurationMs / 1000;
                string[] languageHints = (modelSetting.Config.GetConfigValueOrDefault<string?>("LanguageHints") ?? string.Empty)
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                this._options = new AliyunRealtimeAsrOptions(
                    endpoint,
                    modelName,
                    languageHints,
                    packetSizeBytes,
                    Math.Clamp(modelSetting.Config.GetConfigValueOrDefault("ConnectionTimeoutSeconds", 5), 1, 60),
                    Math.Clamp(modelSetting.Config.GetConfigValueOrDefault("ResponseTimeoutSeconds", 15), 1, 60));

                this._webSocketClient?.Dispose();
                this._webSocketClient = new WebSocketClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = $"Bearer {apiKey}"
                });
                this.Logger.LogInformation("已构建阿里云实时 ASR：{ModelName}。", modelName);
                return true;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "构建阿里云实时 ASR 失败。");
                return false;
            }
        }

        public void RegisterDevice(ActiveCallContext activeCall, IAsrEventCallback callback)
        {
            this._asrEventCallback = callback;
            this.RegisterDevice(activeCall);
        }

        public override void UnregisterDevice(ActiveCallContext activeCall)
        {
            this.AbortSynchronously();
            this._asrEventCallback = null;
            base.UnregisterDevice(activeCall);
        }

        public async Task ConvertSpeechTextAsync(Workflow<float[]> workflow, int sampleRate, CancellationToken token)
        {
            await this.ConvertSpeechTextStreamingAsync(workflow, sampleRate, StreamingAsrOperation.Start, token);
            await this.ConvertSpeechTextStreamingAsync(workflow, sampleRate, StreamingAsrOperation.Finish, token);
        }

        public Task ConvertSpeechTextStreamingAsync(
            Workflow<float[]> workflow,
            int sampleRate,
            StreamingAsrOperation operation,
            CancellationToken token)
        {
            if (!this.CheckDeviceRegistered(workflow.DeviceId))
            {
                throw new InvalidOperationException("阿里云 ASR 未注册到当前通话。");
            }

            return operation switch
            {
                StreamingAsrOperation.Start => this.StartUtteranceAsync(workflow.Data, sampleRate, workflow.TurnId, token),
                StreamingAsrOperation.Audio => this.AppendAudioAsync(workflow.Data, sampleRate, token),
                StreamingAsrOperation.Finish => this.FinishUtteranceAsync(token),
                StreamingAsrOperation.Abort => this.AbortUtteranceAsync(),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            };
        }

        private async Task StartUtteranceAsync(float[] initialAudio, int sampleRate, long turnId, CancellationToken token)
        {
            await this._streamLock.WaitAsync(token);
            try
            {
                if (this._activeSession is not null)
                {
                    return;
                }

                AliyunRealtimeAsrOptions options = this._options
                    ?? throw new InvalidOperationException("阿里云 ASR 尚未构建。");
                WebSocketClient webSocketClient = this._webSocketClient
                    ?? throw new InvalidOperationException("阿里云 ASR WebSocket 尚未初始化。");
                var session = new AliyunRealtimeAsrUtteranceSession(webSocketClient, options, turnId);
                this._activeSession = session;
                try
                {
                    await session.StartAsync(initialAudio, sampleRate, token);
                }
                catch
                {
                    if (ReferenceEquals(this._activeSession, session))
                    {
                        this._activeSession = null;
                    }

                    session.Dispose();
                    throw;
                }
            }
            finally
            {
                this._streamLock.Release();
            }
        }

        private async Task AppendAudioAsync(float[] audioData, int sampleRate, CancellationToken token)
        {
            await this._streamLock.WaitAsync(token);
            try
            {
                this._activeSession?.AppendAudio(audioData, sampleRate);
            }
            finally
            {
                this._streamLock.Release();
            }
        }

        private async Task FinishUtteranceAsync(CancellationToken token)
        {
            AliyunRealtimeAsrUtteranceSession? session;
            Task<string?>? finishTask;
            await this._streamLock.WaitAsync(token);
            try
            {
                session = this._activeSession;
                if (session is null || session.IsAborted || session.IsFinishing)
                {
                    return;
                }

                finishTask = session.FinishAsync(token);
            }
            finally
            {
                this._streamLock.Release();
            }

            string? finalText = null;
            bool failed = false;
            bool shouldNotify = false;
            try
            {
                finalText = await finishTask;
            }
            catch (Exception exception)
            {
                failed = !session.IsAborted;
                if (failed)
                {
                    this.Logger.LogError(exception, "等待阿里云实时 ASR 最终结果失败。");
                }
            }
            finally
            {
                await this._streamLock.WaitAsync();
                try
                {
                    if (ReferenceEquals(this._activeSession, session))
                    {
                        this._activeSession = null;
                        shouldNotify = !session.IsAborted;
                    }
                }
                finally
                {
                    this._streamLock.Release();
                    session.Dispose();
                }
            }

            if (shouldNotify)
            {
                this._asrEventCallback?.OnSpeechTextConverted(
                    session.TurnId,
                    !failed,
                    failed ? string.Empty : finalText ?? string.Empty);
            }
        }

        private async Task AbortUtteranceAsync()
        {
            await this._streamLock.WaitAsync();
            try
            {
                AliyunRealtimeAsrUtteranceSession? session = this._activeSession;
                this._activeSession = null;
                if (session is not null)
                {
                    await session.AbortAsync();
                    session.Dispose();
                }
            }
            finally
            {
                this._streamLock.Release();
            }
        }

        private string ResolveEndpoint(string? endpoint, string region, string? workspaceId)
        {
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                if (Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeWss)
                {
                    return uri.ToString();
                }

                this.Logger.LogWarning("阿里云实时 ASR Endpoint 必须是绝对 wss 地址。");
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(workspaceId))
            {
                return region.ToLowerInvariant() switch
                {
                    "cn-beijing" => $"wss://{workspaceId}.cn-beijing.maas.aliyuncs.com/api-ws/v1/inference",
                    "ap-southeast-1" => $"wss://{workspaceId}.ap-southeast-1.maas.aliyuncs.com/api-ws/v1/inference",
                    _ => this.LogUnsupportedRegion(region)
                };
            }

            return region.ToLowerInvariant() switch
            {
                "cn-beijing" => "wss://dashscope.aliyuncs.com/api-ws/v1/inference",
                "ap-southeast-1" => "wss://dashscope-intl.aliyuncs.com/api-ws/v1/inference",
                _ => this.LogUnsupportedRegion(region)
            };
        }

        private string LogUnsupportedRegion(string region)
        {
            this.Logger.LogWarning("阿里云实时 ASR 不支持地域 {Region}。", region);
            return string.Empty;
        }

        private void AbortSynchronously()
        {
            try
            {
                this.AbortUtteranceAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                this.Logger.LogDebug(exception, "同步中止阿里云实时 ASR 失败。");
            }
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref this._disposed, 1) != 0)
            {
                return;
            }

            this.AbortSynchronously();
            this._webSocketClient?.Dispose();
            this._streamLock.Dispose();
        }

        private static bool IsSupportedModel(string modelName) =>
            modelName.Equals("qwen-audio-3.0-asr-flash-streaming", StringComparison.OrdinalIgnoreCase)
            || modelName.StartsWith("fun-asr-realtime", StringComparison.OrdinalIgnoreCase);
    }
}

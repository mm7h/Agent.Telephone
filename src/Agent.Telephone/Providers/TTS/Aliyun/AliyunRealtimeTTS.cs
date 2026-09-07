using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.Enums;
using Agent.Telephone.Helpers;
using Agent.Telephone.Media.Abstractions;
using Agent.Telephone.Protocol.WebSocket;
using Agent.Telephone.Providers.TTS;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Providers.TTS.Aliyun
{
    internal sealed class AliyunRealtimeTTS : BaseAliyunTts<AliyunRealtimeTTS>, ITts
    {
        private readonly SemaphoreSlim _turnLock = new(1, 1);
        private ITtsEventCallback? _ttsEventCallback;
        private WebSocketClient? _webSocketClient;
        private AliyunRealtimeTtsOptions? _options;
        private AliyunRealtimeTtsTurnSession? _activeTurn;
        private int _disposed;

        public AliyunRealtimeTTS(IAudioEditor audioEditor, ILogger<AliyunRealtimeTTS> logger)
            : base(audioEditor, logger)
        {
        }

        public override string ModelName => nameof(AliyunRealtimeTTS);

        public override bool Build(ModelSetting modelSetting)
        {
            this._options = null;
            try
            {
                string apiKey = modelSetting.Config.GetConfigValueOrDefault("ApiKey", string.Empty);
                string modelName = modelSetting.Config.GetConfigValueOrDefault("ModelName", "cosyvoice-v3-flash");
                string voice = modelSetting.Config.GetConfigValueOrDefault("Voice", string.Empty);
                string format = modelSetting.Config.GetConfigValueOrDefault("Format", "pcm");
                int sampleRate = modelSetting.Config.GetConfigValueOrDefault("SampleRate", 24000);
                if (string.IsNullOrWhiteSpace(apiKey) ||
                    string.IsNullOrWhiteSpace(voice) ||
                    !IsSupportedModel(modelName) ||
                    !format.Equals("pcm", StringComparison.OrdinalIgnoreCase) ||
                    sampleRate != 24000)
                {
                    this.Logger.LogWarning("阿里云实时 TTS 需要 ApiKey、Voice、支持的模型及 24 kHz PCM 输出。");
                    return false;
                }

                int volume = modelSetting.Config.GetConfigValueOrDefault("Volume", 50);
                float rate = modelSetting.Config.GetConfigValueOrDefault("Rate", 1.0f);
                float pitch = modelSetting.Config.GetConfigValueOrDefault("Pitch", 1.0f);
                if (volume is < 0 or > 100 || rate is < 0.5f or > 2.0f || pitch is < 0.5f or > 2.0f)
                {
                    this.Logger.LogWarning("阿里云实时 TTS 的 Volume、Rate 或 Pitch 配置无效。");
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

                string[] languageHints = (modelSetting.Config.GetConfigValueOrDefault<string?>("LanguageHints") ?? string.Empty)
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                this._options = new AliyunRealtimeTtsOptions(
                    endpoint,
                    modelName,
                    voice,
                    volume,
                    rate,
                    pitch,
                    languageHints,
                    modelSetting.Config.GetConfigValueOrDefault<string?>("Instruction"),
                    Math.Clamp(modelSetting.Config.GetConfigValueOrDefault("ConnectionTimeoutSeconds", 5), 1, 60),
                    Math.Clamp(modelSetting.Config.GetConfigValueOrDefault("ResponseTimeoutSeconds", 30), 1, 120));
                this.BuildAudioSavingConfig(modelSetting);

                this._webSocketClient?.Dispose();
                this._webSocketClient = new WebSocketClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = $"Bearer {apiKey}"
                });
                this.Logger.LogInformation("已构建阿里云实时 TTS：{ModelName}。", modelName);
                return true;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "构建阿里云实时 TTS 失败。");
                return false;
            }
        }

        public void RegisterDevice(ActiveCallContext activeCall, ITtsEventCallback callback)
        {
            this._ttsEventCallback = callback;
            this.RegisterDevice(activeCall);
        }

        public override void UnregisterDevice(ActiveCallContext activeCall)
        {
            this.AbortSynchronously();
            this._ttsEventCallback = null;
            base.UnregisterDevice(activeCall);
        }

        public async Task SynthesisAsync(Workflow<OutSegment> workflow, CancellationToken token)
        {
            if (!this.CheckDeviceRegistered(workflow.DeviceId))
            {
                throw new InvalidOperationException("阿里云实时 TTS 未注册到当前通话。");
            }

            AliyunRealtimeTtsSegment segment = AliyunRealtimeTtsSegment.From(workflow.Data);
            if (string.IsNullOrWhiteSpace(segment.Content))
            {
                return;
            }

            AliyunRealtimeTtsTurnSession? turn = null;
            Task? finishTask = null;
            await this._turnLock.WaitAsync(token);
            try
            {
                AliyunRealtimeTtsOptions options = this._options
                    ?? throw new InvalidOperationException("阿里云实时 TTS 尚未构建。");
                WebSocketClient webSocketClient = this._webSocketClient
                    ?? throw new InvalidOperationException("阿里云实时 TTS WebSocket 尚未初始化。");

                turn = this._activeTurn;
                if (segment.IsFirstSegment)
                {
                    if (turn is not null)
                    {
                        await this.StopActiveTurnAsync(turn);
                    }

                    turn = new AliyunRealtimeTtsTurnSession(webSocketClient, options, this._ttsEventCallback, this.OnTurnFaulted);
                    this._activeTurn = turn;
                    await turn.StartAsync(token);
                    this._ttsEventCallback?.OnBeforeProcessing(segment.Content, true, segment.IsLastSegment);
                }

                if (turn is null)
                {
                    throw new InvalidOperationException("阿里云实时 TTS 缺少首个文本片段。");
                }

                await turn.AppendTextAsync(segment, token);
                if (segment.IsLastSegment)
                {
                    finishTask = turn.FinishAsync(token);
                }
            }
            catch (OperationCanceledException)
            {
                if (turn is not null)
                {
                    await this.StopActiveTurnAsync(turn);
                }

                throw;
            }
            catch (Exception exception)
            {
                if (turn is not null)
                {
                    this.NotifyTurnFailed(turn, exception);
                    await this.StopActiveTurnAsync(turn);
                }

                throw;
            }
            finally
            {
                this._turnLock.Release();
            }

            if (finishTask is null || turn is null)
            {
                return;
            }

            try
            {
                await finishTask;
                foreach (KeyValuePair<string, float[]> audio in turn.GetCompletedAudio())
                {
                    await this.SaveAudioFileAsync(audio.Key, audio.Value);
                }

                this._ttsEventCallback?.OnProcessed(segment.Content, segment.IsFirstSegment, true, TtsGenerateResult.Success);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                this.NotifyTurnFailed(turn, exception);
                throw;
            }
            finally
            {
                await this.StopTurnAfterCompletionAsync(turn);
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
            this._turnLock.Dispose();
        }

        private async Task StopTurnAfterCompletionAsync(AliyunRealtimeTtsTurnSession turn)
        {
            await this._turnLock.WaitAsync();
            try
            {
                if (ReferenceEquals(this._activeTurn, turn))
                {
                    this._activeTurn = null;
                }
            }
            finally
            {
                this._turnLock.Release();
                turn.Dispose();
            }
        }

        private async Task StopActiveTurnAsync(AliyunRealtimeTtsTurnSession turn)
        {
            if (ReferenceEquals(this._activeTurn, turn))
            {
                this._activeTurn = null;
            }

            await turn.AbortAsync();
            turn.Dispose();
        }

        private void OnTurnFaulted(AliyunRealtimeTtsTurnSession turn, Exception exception) => this.NotifyTurnFailed(turn, exception);

        private void NotifyTurnFailed(AliyunRealtimeTtsTurnSession turn, Exception exception)
        {
            if (!turn.TryMarkFailureReported())
            {
                return;
            }

            this.Logger.LogError(exception, "阿里云实时 TTS 任务 {TaskId} 失败。", turn.TaskId);
            this._ttsEventCallback?.OnProcessed(string.Empty, false, false, TtsGenerateResult.Failed);
        }

        private string ResolveEndpoint(string? endpoint, string region, string? workspaceId)
        {
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                if (Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeWss)
                {
                    return uri.ToString();
                }

                this.Logger.LogWarning("阿里云实时 TTS Endpoint 必须是绝对 wss 地址。");
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
            this.Logger.LogWarning("阿里云实时 TTS 不支持地域 {Region}。", region);
            return string.Empty;
        }

        private void AbortSynchronously()
        {
            bool lockTaken = false;
            try
            {
                this._turnLock.Wait();
                lockTaken = true;
                AliyunRealtimeTtsTurnSession? turn = this._activeTurn;
                this._activeTurn = null;
                if (turn is not null)
                {
                    turn.AbortAsync().GetAwaiter().GetResult();
                    turn.Dispose();
                }
            }
            catch (Exception exception)
            {
                this.Logger.LogDebug(exception, "同步中止阿里云实时 TTS 失败。");
            }
            finally
            {
                if (lockTaken)
                {
                    this._turnLock.Release();
                }
            }
        }

        private static bool IsSupportedModel(string modelName) =>
            modelName.StartsWith("cosyvoice-", StringComparison.OrdinalIgnoreCase) ||
            (modelName.StartsWith("qwen-audio-", StringComparison.OrdinalIgnoreCase) &&
                modelName.Contains("tts", StringComparison.OrdinalIgnoreCase));
    }
}

using System.Net.Http;
using System.Text;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.Enums;
using Agent.Telephone.Helpers;
using Agent.Telephone.Media.Abstractions;
using Agent.Telephone.Providers.TTS;
using Flurl.Http;
using Flurl.Http.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Providers.TTS.Aliyun
{
    internal sealed class AliyunHttpTTS : BaseAliyunTts<AliyunHttpTTS>, ITts
    {
        private const string FlurlClientName = nameof(AliyunHttpTTS);
        private const string SpeechSynthesizerPath = "/api/v1/services/audio/tts/SpeechSynthesizer";

        private readonly IFlurlClientCache _flurlClientCache;
        private ITtsEventCallback? _ttsEventCallback;
        private AliyunHttpTtsOptions? _options;

        public AliyunHttpTTS(
            IAudioEditor audioEditor,
            [FromKeyedServices(FlurlClientName)] IFlurlClientCache flurlClientCache,
            ILogger<AliyunHttpTTS> logger)
            : base(audioEditor, logger)
        {
            this._flurlClientCache = flurlClientCache;
        }

        public override string ModelName => nameof(AliyunHttpTTS);

        public override bool Build(ModelSetting modelSetting)
        {
            this._options = null;
            try
            {
                string apiKey = modelSetting.Config.GetConfigValueOrDefault("ApiKey", string.Empty);
                string workspaceId = modelSetting.Config.GetConfigValueOrDefault("WorkspaceId", string.Empty);
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
                    this.Logger.LogWarning("阿里云 HTTP TTS 需要 ApiKey、Voice、支持的模型及 24 kHz PCM 输出。");
                    return false;
                }

                string? endpoint = this.ResolveEndpoint(
                    modelSetting.Config.GetConfigValueOrDefault<string?>("Endpoint"),
                    workspaceId);
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    return false;
                }

                int volume = modelSetting.Config.GetConfigValueOrDefault("Volume", 50);
                float rate = modelSetting.Config.GetConfigValueOrDefault("Rate", 1.0f);
                float pitch = modelSetting.Config.GetConfigValueOrDefault("Pitch", 1.0f);
                if (volume is < 0 or > 100 || rate is < 0.5f or > 2.0f || pitch is < 0.5f or > 2.0f)
                {
                    this.Logger.LogWarning("阿里云 HTTP TTS 的 Volume、Rate 或 Pitch 配置无效。");
                    return false;
                }

                string[] languageHints = (modelSetting.Config.GetConfigValueOrDefault<string?>("LanguageHints") ?? string.Empty)
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                this._options = new AliyunHttpTtsOptions(
                    endpoint,
                    apiKey,
                    modelName,
                    voice,
                    modelSetting.Config.GetConfigValueOrDefault("Streaming", false),
                    volume,
                    rate,
                    pitch,
                    languageHints,
                    modelSetting.Config.GetConfigValueOrDefault<string?>("Instruction"),
                    Math.Clamp(modelSetting.Config.GetConfigValueOrDefault("ResponseTimeoutSeconds", 120), 1, 600));
                this.BuildAudioSavingConfig(modelSetting);
                this.Logger.LogInformation("已构建阿里云 HTTP TTS：{ModelName}。", modelName);
                return true;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "构建阿里云 HTTP TTS 失败。");
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
            this._ttsEventCallback = null;
            base.UnregisterDevice(activeCall);
        }

        public async Task SynthesisAsync(Workflow<OutSegment> workflow, CancellationToken token)
        {
            if (!this.CheckDeviceRegistered(workflow.DeviceId))
            {
                throw new InvalidOperationException("阿里云 HTTP TTS 未注册到当前通话。");
            }

            AliyunHttpTtsOptions options = this._options
                ?? throw new InvalidOperationException("阿里云 HTTP TTS 尚未构建。");
            OutSegment segment = workflow.Data;
            if (string.IsNullOrWhiteSpace(segment.SentenceId))
            {
                this.Logger.LogWarning("阿里云 HTTP TTS 缺少句子 ID。");
                return;
            }

            var state = new SynthesisState(this._ttsEventCallback, segment);
            try
            {
                state.OnBeforeProcessing();
                Dictionary<string, object?> request = CreateRequest(options, segment.Content);
                bool synthesized = options.Streaming
                    ? await this.SynthesizeStreamingAsync(options, request, state, token)
                    : await this.SynthesizeNonStreamingAsync(options, request, state, token);
                if (!synthesized || !state.HasAudio)
                {
                    if (synthesized)
                    {
                        this.Logger.LogError("阿里云 HTTP TTS 未返回音频数据。");
                    }

                    state.ReportProcessed(TtsGenerateResult.Failed);
                    return;
                }

                await this.SaveAudioFileAsync(segment.SentenceId, state.PcmData);
                state.OnSentenceEnd();
                state.ReportProcessed(TtsGenerateResult.Success);
            }
            catch (OperationCanceledException)
            {
                state.ReportProcessed(TtsGenerateResult.Aborted);
                throw;
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "阿里云 HTTP TTS 合成失败。");
                state.ReportProcessed(TtsGenerateResult.Failed);
                throw;
            }
        }

        public override void Dispose()
        {
            this._ttsEventCallback = null;
        }

        private async Task<bool> SynthesizeNonStreamingAsync(
            AliyunHttpTtsOptions options,
            Dictionary<string, object?> request,
            SynthesisState state,
            CancellationToken token)
        {
            IFlurlClient client = this._flurlClientCache.Get(FlurlClientName);
            using IFlurlResponse response = await client.Request(options.Endpoint)
                .WithHeader("Authorization", $"Bearer {options.ApiKey}")
                .WithTimeout(options.ResponseTimeoutSeconds)
                .AllowAnyHttpStatus()
                .PostJsonAsync(request, cancellationToken: token);
            if (!response.ResponseMessage.IsSuccessStatusCode)
            {
                this.Logger.LogError("阿里云 HTTP TTS 请求失败：状态={Status}，正文={Body}。", response.StatusCode, await response.GetStringAsync());
                return false;
            }

            AliyunHttpTtsResponse? synthesisResponse = await response.GetJsonAsync<AliyunHttpTtsResponse>();
            if (IsApiError(synthesisResponse))
            {
                this.LogApiError(synthesisResponse!);
                return false;
            }

            string? audioUrl = synthesisResponse?.Output?.Audio?.Url;
            if (string.IsNullOrWhiteSpace(audioUrl))
            {
                this.Logger.LogError("阿里云 HTTP TTS 响应中缺少音频 URL。");
                return false;
            }

            using IFlurlResponse audioResponse = await client.Request(audioUrl)
                .WithTimeout(options.ResponseTimeoutSeconds)
                .AllowAnyHttpStatus()
                .GetAsync(cancellationToken: token);
            if (!audioResponse.ResponseMessage.IsSuccessStatusCode)
            {
                this.Logger.LogError("下载阿里云 HTTP TTS 音频失败：状态={Status}，正文={Body}。", audioResponse.StatusCode, await audioResponse.GetStringAsync());
                return false;
            }

            state.AppendAudio(await audioResponse.GetBytesAsync());
            return true;
        }

        private async Task<bool> SynthesizeStreamingAsync(
            AliyunHttpTtsOptions options,
            Dictionary<string, object?> request,
            SynthesisState state,
            CancellationToken token)
        {
            IFlurlClient client = this._flurlClientCache.Get(FlurlClientName);
            using IFlurlResponse response = await client.Request(options.Endpoint)
                .WithHeader("Authorization", $"Bearer {options.ApiKey}")
                .WithHeader("X-DashScope-SSE", "enable")
                .WithTimeout(options.ResponseTimeoutSeconds)
                .AllowAnyHttpStatus()
                .PostJsonAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.ResponseMessage.IsSuccessStatusCode)
            {
                this.Logger.LogError("阿里云 HTTP TTS 流式请求失败：状态={Status}，正文={Body}。", response.StatusCode, await response.GetStringAsync());
                return false;
            }

            bool completed = false;
            var eventData = new StringBuilder();
            await using Stream stream = await response.GetStreamAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(token);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    if (!this.TryProcessSseEvent(eventData, state, out bool eventCompleted))
                    {
                        return false;
                    }

                    completed |= eventCompleted;
                    eventData.Clear();
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (eventData.Length > 0)
                    {
                        eventData.AppendLine();
                    }

                    eventData.Append(line.AsSpan(5).TrimStart());
                }
            }

            return this.TryProcessSseEvent(eventData, state, out bool finalEventCompleted) &&
                (completed || finalEventCompleted);
        }

        private bool TryProcessSseEvent(StringBuilder eventData, SynthesisState state, out bool completed)
        {
            completed = false;
            if (eventData.Length == 0 || eventData.ToString().Equals("[DONE]", StringComparison.Ordinal))
            {
                return true;
            }

            AliyunHttpTtsResponse? message = JsonHelper.Deserialize<AliyunHttpTtsResponse>(eventData.ToString());
            if (message is null || message.Output is null)
            {
                this.Logger.LogError("阿里云 HTTP TTS 返回了无效的 SSE 数据。");
                return false;
            }

            if (IsApiError(message))
            {
                this.LogApiError(message);
                return false;
            }

            if (message.Output.Type?.Equals("sentence-begin", StringComparison.OrdinalIgnoreCase) == true)
            {
                state.OnSentenceStart();
            }

            string? encodedAudio = message.Output.Audio?.Data;
            if (!string.IsNullOrWhiteSpace(encodedAudio))
            {
                try
                {
                    state.AppendAudio(Convert.FromBase64String(encodedAudio));
                }
                catch (FormatException exception)
                {
                    this.Logger.LogError(exception, "阿里云 HTTP TTS SSE 音频不是有效的 Base64 数据。");
                    return false;
                }
            }

            completed = message.Output.FinishReason?.Equals("stop", StringComparison.OrdinalIgnoreCase) == true;
            return true;
        }

        private string? ResolveEndpoint(string? endpoint, string workspaceId)
        {
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                if (Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps)
                {
                    return uri.ToString();
                }

                this.Logger.LogWarning("阿里云 HTTP TTS Endpoint 必须是绝对 https 地址。");
                return null;
            }

            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                this.Logger.LogWarning("阿里云 HTTP TTS 需要 WorkspaceId 或 Endpoint。");
                return null;
            }

            return $"https://{workspaceId}.cn-beijing.maas.aliyuncs.com{SpeechSynthesizerPath}";
        }

        private static Dictionary<string, object?> CreateRequest(AliyunHttpTtsOptions options, string text)
        {
            var input = new Dictionary<string, object?>
            {
                ["text"] = text,
                ["voice"] = options.Voice,
                ["format"] = "pcm",
                ["sample_rate"] = 24000,
                ["volume"] = options.Volume,
                ["rate"] = options.Rate,
                ["pitch"] = options.Pitch
            };
            if (options.LanguageHints.Length > 0)
            {
                input["language_hints"] = options.LanguageHints;
            }
            if (!string.IsNullOrWhiteSpace(options.Instruction))
            {
                input["instruction"] = options.Instruction;
            }

            return new Dictionary<string, object?>
            {
                ["model"] = options.ModelName,
                ["input"] = input
            };
        }

        private static bool IsApiError(AliyunHttpTtsResponse? response) =>
            !string.IsNullOrWhiteSpace(response?.Code) &&
            !response.Code.Equals("0", StringComparison.OrdinalIgnoreCase) &&
            !response.Code.Equals("200", StringComparison.OrdinalIgnoreCase);

        private void LogApiError(AliyunHttpTtsResponse response)
        {
            this.Logger.LogError(
                "阿里云 HTTP TTS API 错误：RequestId={RequestId}，Code={Code}，Message={Message}。",
                response.RequestId,
                response.Code,
                response.Message);
        }

        private static bool IsSupportedModel(string modelName) =>
            modelName.StartsWith("cosyvoice-", StringComparison.OrdinalIgnoreCase) ||
            (modelName.StartsWith("qwen-audio-", StringComparison.OrdinalIgnoreCase) &&
                modelName.Contains("tts", StringComparison.OrdinalIgnoreCase));

        private sealed class SynthesisState
        {
            private readonly ITtsEventCallback? _callback;
            private readonly OutSegment _segment;
            private readonly List<float> _pcm = [];
            private bool _processed;
            private bool _sentenceStarted;

            public SynthesisState(ITtsEventCallback? callback, OutSegment segment)
            {
                this._callback = callback;
                this._segment = segment;
            }

            public bool HasAudio => this._pcm.Count > 0;

            public float[] PcmData => this._pcm.ToArray();

            public void OnBeforeProcessing() =>
                this._callback?.OnBeforeProcessing(this._segment.Content, this._segment.IsFirstSegment, this._segment.IsLastSegment);

            public void OnSentenceStart()
            {
                if (this._sentenceStarted)
                {
                    return;
                }

                this._sentenceStarted = true;
                this._callback?.OnSentenceStart(this._segment.Content, this._segment.SentenceId!);
            }

            public void AppendAudio(byte[] audioData)
            {
                float[] pcm = audioData.PcmBytesToFloat(16);
                if (pcm.Length == 0)
                {
                    return;
                }

                this.OnSentenceStart();
                this._pcm.AddRange(pcm);
                this._callback?.OnProcessing(pcm, false, false);
            }

            public void OnSentenceEnd()
            {
                if (this._sentenceStarted)
                {
                    this._callback?.OnSentenceEnd(this._segment.Content, this._segment.SentenceId!);
                }
            }

            public void ReportProcessed(TtsGenerateResult result)
            {
                if (this._processed)
                {
                    return;
                }

                this._processed = true;
                this._callback?.OnProcessed(this._segment.Content, this._segment.IsFirstSegment, this._segment.IsLastSegment, result);
            }
        }
    }
}

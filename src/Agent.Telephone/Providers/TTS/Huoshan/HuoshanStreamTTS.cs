using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.Enums;
using Agent.Telephone.Helpers;
using Agent.Telephone.Media.Abstractions;
using Agent.Telephone.Protocol.WebSocket;
using Agent.Telephone.Providers.TTS.Huoshan.Protocols.Enums;
using Agent.Telephone.Providers.TTS.Huoshan.Protocols.Models;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Providers.TTS.Huoshan
{
    internal abstract class HuoshanStreamTTS<TLogger> : BaseHuoshanTTS<TLogger>
    {

        private readonly object _audioBufferLock = new();
        private readonly Dictionary<string, List<float>> _sessionAudioBuffers = new();
        private readonly List<PendingWait> _waits = new();
        private readonly object _waitsLock = new();
        private static readonly TimeSpan s_defaultWaitTimeout = TimeSpan.FromSeconds(15);

        public HuoshanStreamTTS(IAudioEditor audioEditor, ILogger<TLogger> logger) : base(audioEditor, logger)
        {
            this.ProcessingSegments = new ConcurrentDictionary<string, OutSegment>();
        }

        protected WebSocketClient? WebSocketClient { get; set; }
        protected IDictionary<string, OutSegment> ProcessingSegments { get; }
        protected bool StreamingActive { get; set; } = false;

        public override bool Build(ModelSetting modelSetting)
        {
            try
            {
                string? appId = modelSetting.Config.GetConfigValueOrDefault("AppId");
                string? accessToken = modelSetting.Config.GetConfigValueOrDefault("AccessToken");
                string? resourceId = modelSetting.Config.GetConfigValueOrDefault("ResourceId");
                string? speaker = modelSetting.Config.GetConfigValueOrDefault("Speaker");

                if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(resourceId) || string.IsNullOrWhiteSpace(speaker))
                {
                    this.Logger.LogWarning("火山双向 TTS 配置不完整，请检查 AppId、AccessToken、ResourceId 和 speaker。");
                    return false;
                }
                this.SpeakerId = speaker;
                this.SpeechRate = modelSetting.Config.GetConfigValueOrDefault("SpeechRate", 0);
                this.LoudnessRate = modelSetting.Config.GetConfigValueOrDefault("LoudnessRate", 0);
                this.BuildAudioSavingConfig(modelSetting);

                IDictionary<string, string> headers = new Dictionary<string, string>
                {
                    { "X-Api-App-Key", appId },
                    { "X-Api-Access-Key", accessToken },
                    { "X-Api-Resource-Id", resourceId },
                    { "X-Api-Connect-Id", Guid.NewGuid().ToString() }
                };
                this.WebSocketClient = new WebSocketClient(headers);
                this.WebSocketClient.OnOpen += this.WebSocketClient_OnOpen;
                this.WebSocketClient.OnBinaryMessage += this.WebSocketClient_OnBinaryMessage;
                this.WebSocketClient.OnClose += this.WebSocketClient_OnClose;
                this.WebSocketClient.OnError += this.WebSocketClient_OnError;

                this.Logger.LogInformation("已构建 {providerType} 模型：{modelName}", this.ProviderType, this.ModelName);

                return true;
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "构建 {modelName} 失败。", this.ModelName);
                return false;
            }
        }

        #region Huoshan TTS services API
        protected async Task ConnectAsync(string endPoint, CancellationToken token)
        {
            if (this.WebSocketClient is null)
            {
                this.Logger.LogError("火山 TTS 的 WebSocket 客户端未初始化。");
                throw new InvalidOperationException("WebSocket 客户端未初始化。");
            }
            await this.WebSocketClient.ConnectAsync(endPoint, token);
        }
        protected async Task TaskRequestAsync(object ttsReq)
        {
            var message = Message.Create(MsgType.FullClientRequest, MsgTypeFlagBits.NoSeq);
            message.Payload = JsonHelper.SerializeToUtf8Bytes(ttsReq);
            await this.SendMessageAsync(message);
        }

        protected async Task TaskRequestAsync(string sessionId, byte[] payload)
        {
            var message = Message.Create(MsgType.FullClientRequest, MsgTypeFlagBits.WithEvent);
            message.EventType = EventType.TaskRequest;
            message.SessionId = sessionId;
            message.Payload = payload;
            await this.SendMessageAsync(message);
        }

        protected async Task SendMessageAsync(Message message)
        {
            if (this.WebSocketClient is null)
            {
                return;
            }
            var data = message.Marshal();
            await this.WebSocketClient.SendAsync(data);
        }

        protected void StartNewAudioBuffer(string sessionId)
        {
            if (this.AudioSavingConfig is null || !this.AudioSavingConfig.SaveFile || string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            lock (this._audioBufferLock)
            {
                if (!this._sessionAudioBuffers.ContainsKey(sessionId))
                {
                    this._sessionAudioBuffers[sessionId] = new List<float>();
                }
            }
        }

        protected void AppendAudioPayloadChunk(string sessionId, byte[] audioData)
        {
            if (this.AudioSavingConfig is null || !this.AudioSavingConfig.SaveFile || string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            float[] pcmData = audioData.PcmBytesToFloat(16);
            if (pcmData.Length == 0)
            {
                return;
            }

            lock (this._audioBufferLock)
            {
                if (!this._sessionAudioBuffers.TryGetValue(sessionId, out var buffer))
                {
                    buffer = new List<float>();
                    this._sessionAudioBuffers[sessionId] = buffer;
                }

                buffer.AddRange(pcmData);
            }
        }
        protected async Task<Message> StartConnectionAsync(CancellationToken cancellationToken)
        {
            var message = Message.Create(MsgType.FullClientRequest, MsgTypeFlagBits.WithEvent);
            message.EventType = EventType.StartConnection;
            message.Payload = JsonHelper.SerializeToUtf8Bytes(new { });

            var waitTask = this.WaitForEventAsync(MsgType.FullServerResponse, EventType.ConnectionStarted, cancellationToken, null);
            await this.SendMessageAsync(message);
            return await waitTask;
        }



        protected async Task<Message> StartSessionAsync(string sessionId, byte[] payload, CancellationToken cancellationToken)
        {
            var message = Message.Create(MsgType.FullClientRequest, MsgTypeFlagBits.WithEvent);
            message.EventType = EventType.StartSession;
            message.SessionId = sessionId;
            message.Payload = payload;

            var waitTask = this.WaitForEventAsync(MsgType.FullServerResponse, EventType.SessionStarted, cancellationToken, null);
            await this.SendMessageAsync(message);
            return await waitTask;
        }

        protected async Task<Message> FinishSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            var message = Message.Create(MsgType.FullClientRequest, MsgTypeFlagBits.WithEvent);
            message.EventType = EventType.FinishSession;
            message.SessionId = sessionId;
            message.Payload = JsonHelper.SerializeToUtf8Bytes(new { });

            var waitTask = this.WaitForEventAsync(MsgType.FullServerResponse, EventType.SessionFinished, cancellationToken, null);
            await this.SendMessageAsync(message);
            try
            {
                return await waitTask;
            }
            catch (OperationCanceledException)
            {
                this.Logger.LogDebug("FinishSession cancelled for session {SessionId}", sessionId);
                throw;
            }
        }

        protected async Task<Message> FinishConnectionAsync(CancellationToken cancellationToken)
        {
            var message = Message.Create(MsgType.FullClientRequest, MsgTypeFlagBits.WithEvent);
            message.EventType = EventType.FinishConnection;
            message.Payload = JsonHelper.SerializeToUtf8Bytes(new { });

            var waitTask = this.WaitForEventAsync(MsgType.FullServerResponse, EventType.ConnectionFinished, cancellationToken, null);
            await this.SendMessageAsync(message);
            return await waitTask;
        }

        protected async Task FinalizeSessionAudioAsync(string sessionId)
        {
            List<float>? audioBuffer = null;
            lock (this._audioBufferLock)
            {
                if (this._sessionAudioBuffers.TryGetValue(sessionId, out var buffer))
                {
                    audioBuffer = new List<float>(buffer);
                    this._sessionAudioBuffers.Remove(sessionId);
                }
            }

            if (audioBuffer is not null && audioBuffer.Any())
            {
                await this.SaveAudioFileAsync(this.CurrentCall.DeviceId, this.CurrentCall.CallerNumber, this.CurrentCall.DialedNumber, sessionId, audioBuffer.ToArray()).ConfigureAwait(false);
            }
        }

        protected void ClearSessionAudioBuffer(string sessionId)
        {
            lock (this._audioBufferLock)
            {
                this._sessionAudioBuffers.Remove(sessionId);
            }
        }

        protected void ClearAudioBuffer()
        {
            lock (this._audioBufferLock)
            {
                this._sessionAudioBuffers.Clear();
            }
        }

        protected async Task<Message> CancelSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            var message = Message.Create(MsgType.FullClientRequest, MsgTypeFlagBits.WithEvent);
            message.EventType = EventType.CancelSession;
            message.SessionId = sessionId;
            message.Payload = JsonHelper.SerializeToUtf8Bytes(new { });

            var waitTask = this.WaitForEventAsync(MsgType.FullServerResponse, EventType.SessionCanceled, cancellationToken, null);
            await this.SendMessageAsync(message);
            return await waitTask;
        }

        protected Task<Message> WaitForEventAsync(MsgType msgType, EventType eventType, CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            var tcs = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pw = new PendingWait
            {
                Match = m => m.MsgType == msgType && m.EventType == eventType,
                Tcs = tcs
            };

            CancellationTokenRegistration ctr = default;
            CancellationTokenSource? timeoutCts = null;

            lock (this._waitsLock)
            {
                this._waits.Add(pw);
            }

            if (cancellationToken.CanBeCanceled)
            {
                ctr = cancellationToken.Register(() =>
                {
                    bool removed;
                    lock (this._waitsLock)
                    {
                        removed = this._waits.Remove(pw);
                    }
                    if (removed)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                    }
                });
            }

            var effectiveTimeout = timeout ?? s_defaultWaitTimeout;
            timeoutCts = new CancellationTokenSource();
            _ = Task.Delay(effectiveTimeout, timeoutCts.Token).ContinueWith(_ =>
            {
                bool removed;
                lock (this._waitsLock)
                {
                    removed = this._waits.Remove(pw);
                }
                if (removed)
                {
                    tcs.TrySetException(new TimeoutException(string.Format("等待 {0} 超时。", eventType)));
                }
            }, TaskScheduler.Default);

            return tcs.Task.ContinueWith(t =>
            {
                ctr.Dispose();
                timeoutCts.Cancel();
                timeoutCts.Dispose();
                return t.GetAwaiter().GetResult();
            }, TaskScheduler.Default);
        }
        #endregion

        protected virtual string GetSubtitle(Message message, bool isSentenceStart)
        {
            return JsonObject.Parse(message.Payload)?["text"]?.GetValue<string>() ?? string.Empty;
        }

        #region WebsocketClient
        protected void FailAllWaits(Exception ex)
        {
            lock (this._waitsLock)
            {
                foreach (var w in this._waits)
                {
                    w.Tcs.TrySetException(ex);
                }
                this._waits.Clear();
            }
        }

        private void WebSocketClient_OnOpen()
        {
            if (this.WebSocketClient is null)
            {
                return;
            }
            this.Logger.LogDebug("设备 {deviceId} 的火山 WebSocket 已连接。", this.CurrentCall?.DeviceId ?? "unknown");
        }

        private void WebSocketClient_OnClose(System.Net.WebSockets.WebSocketCloseStatus? status, string? desc)
        {
            string deviceId = this.CurrentCall?.DeviceId ?? "unknown";
            this.Logger.LogDebug("设备 {deviceId} 的火山 WebSocket 已关闭。状态：{status}，描述：{desc}", deviceId, status, desc);
            this.ClearAudioBuffer();
            this.FailAllWaits(new OperationCanceledException($"设备 {deviceId} 的火山 WebSocket 已关闭：{status} {desc}"));
            if (this.StreamingActive)
            {
                this.TTSEventCallback?.OnProcessed(string.Empty, false, false, TtsGenerateResult.Failed);
            }
        }

        private void WebSocketClient_OnError(System.Net.WebSockets.WebSocketError error, string message)
        {
            string deviceId = this.CurrentCall?.DeviceId ?? "unknown";
            this.Logger.LogError("设备 {deviceId} 的火山 WebSocket 已关闭。状态：{status}，描述：{desc}", deviceId, error, message);
            this.ClearAudioBuffer();
            this.FailAllWaits(new Exception($"设备 {deviceId} 的火山 WebSocket 已关闭。状态：{error}，描述：{message}"));
            if (this.StreamingActive)
            {
                this.TTSEventCallback?.OnProcessed(string.Empty, false, false, TtsGenerateResult.Failed);
            }
        }

        private void WebSocketClient_OnBinaryMessage(byte[] data)
        {
            if (this.WebSocketClient is null || data.Length == 0)
            {
                return;
            }

            Message message;
            try
            {
                message = Message.FromBytes(data);
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "无法解析 WebSocket 二进制消息。");
                return;
            }

            // Sentence start marker -> push empty first frame
            if (message.MsgType == MsgType.FullServerResponse && message.EventType == EventType.TTSSentenceStart)
            {
                if (this.AudioSavingConfig is not null && this.AudioSavingConfig.SaveFile && !string.IsNullOrWhiteSpace(message.SessionId))
                {
                    this.StartNewAudioBuffer(message.SessionId);
                }

                if (this.StreamingActive && !string.IsNullOrWhiteSpace(message.SessionId))
                {
                    string sentence = this.GetSubtitle(message, true);
                    this.TTSEventCallback?.OnSentenceStart(sentence, message.SessionId);
                }
                return;
            }

            // Audio frame streaming
            if (message.MsgType == MsgType.AudioOnlyServer && message.Payload != null && message.Payload.Length > 0)
            {
                if (this.AudioSavingConfig is not null && this.AudioSavingConfig.SaveFile && !string.IsNullOrWhiteSpace(message.SessionId))
                {
                    try
                    {
                        this.AppendAudioPayloadChunk(message.SessionId, message.Payload);
                    }
                    catch (Exception ex)
                    {
                        this.Logger.LogError(ex, "无法为 TTS 会话 {ttsSessionId} 追加音频数据。", message.SessionId);
                    }
                }

                float[] pcmAudioData = message.Payload.PcmBytesToFloat(16);
                if (this.StreamingActive)
                {
                    this.TTSEventCallback?.OnProcessing(pcmAudioData, false, false);
                }
                return;
            }

            // Sentence end marker -> seal current producing subtitle so subsequent samples go to next sentence
            if (message.MsgType == MsgType.FullServerResponse && message.EventType == EventType.TTSSentenceEnd)
            {
                if (this.AudioSavingConfig is not null && this.AudioSavingConfig.SaveFile && !string.IsNullOrWhiteSpace(message.SessionId))
                {
                    _ = this.FinalizeSessionAudioAsync(message.SessionId);
                }

                if (this.StreamingActive && !string.IsNullOrWhiteSpace(message.SessionId))
                {
                    string sentence = this.GetSubtitle(message, false);
                    this.TTSEventCallback?.OnSentenceEnd(sentence, message.SessionId);
                }
                return;
            }

            bool matched = false;
            lock (this._waitsLock)
            {
                for (int i = 0; i < this._waits.Count; i++)
                {
                    var pw = this._waits[i];
                    if (pw.Match(message))
                    {
                        this._waits.RemoveAt(i);
                        pw.Tcs.TrySetResult(message);
                        matched = true;
                        break;
                    }
                }
            }

            // Handle session finished -> finalize file
            if (message.MsgType == MsgType.FullServerResponse && message.EventType == EventType.SessionFinished)
            {
                return;
            }

            if (!matched)
            {
                if (message.MsgType == MsgType.FullServerResponse)
                {
                    if (message.EventType == EventType.ConnectionFailed || message.EventType == EventType.SessionFailed)
                    {
                        var ex = new Exception(string.Format("服务器报告失败：{0}", message));
                        this.FailScopedWaits(message, ex);
                        if (this.StreamingActive)
                        {
                            this.TTSEventCallback?.OnProcessed(string.Empty, false, false, TtsGenerateResult.Failed);
                        }
                    }
                }
                else if (message.MsgType == MsgType.Error)
                {
                    var ex = new Exception(string.Format("服务器错误：{0}", message));
                    this.FailScopedWaits(message, ex);
                    if (this.StreamingActive)
                    {
                        this.TTSEventCallback?.OnProcessed(string.Empty, false, false, TtsGenerateResult.Failed);
                    }
                }
            }
        }

        private void FailScopedWaits(Message message, Exception ex)
        {
            lock (this._waitsLock)
            {
                for (int i = this._waits.Count - 1; i >= 0; i--)
                {
                    var pw = this._waits[i];
                    if (pw.Match(message))
                    {
                        this._waits.RemoveAt(i);
                        pw.Tcs.TrySetException(ex);
                    }
                }
            }
        }

        #endregion
    }
}

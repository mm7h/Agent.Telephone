using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Common.Enums;
using Agent.Telephone.Common.Exceptions;
using Agent.Telephone.Helpers;
using Agent.Telephone.Media.Abstractions;
using Agent.Telephone.Providers.TTS.Huoshan.Protocols.Enums;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Providers.TTS.Huoshan
{
    internal class HuoshanBidirectionTTS : HuoshanStreamTTS<HuoshanBidirectionTTS>, ITts
    {
        private const string SERVICE_END_POINT = "wss://openspeech.bytedance.com/api/v3/tts/bidirection";
        private const string TTS_NAMESPACE = "BidirectionalTTS";
        private int _connectionStarted;

        public HuoshanBidirectionTTS(IAudioEditor audioEditor, ILogger<HuoshanBidirectionTTS> logger) : base(audioEditor, logger)
        {
        }

        public override string ModelName => nameof(HuoshanBidirectionTTS);



        public async Task SynthesisAsync(Workflow<OutSegment> workflow, CancellationToken token)
        {
            if (!this.CheckDeviceRegistered(workflow.DeviceId))
            {
                throw new SessionNotInitializedException();
            }
            if (this.WebSocketClient is null)
            {
                throw new InvalidOperationException("WebSocket 客户端未初始化。");
            }

            if (!this.WebSocketClient.IsConnected)
            {
                await this.ConnectAsync(SERVICE_END_POINT, token);
                await this.StartConnectionAsync(token);
                Interlocked.Exchange(ref this._connectionStarted, 1);
            }

            OutSegment seg = workflow.Data;

            if (string.IsNullOrWhiteSpace(seg.ParagraphId) || string.IsNullOrWhiteSpace(seg.SentenceId))
            {
                this.Logger.LogWarning("由于缺少段落 ID 或句子 ID，处理片段失败。");
                return;
            }

            this.StartNewAudioBuffer(seg.SentenceId);
            this.ProcessingSegments.TryAdd(seg.SentenceId, workflow.Data);

            this.StreamingActive = true;
            Dictionary<string, object> startReq = new Dictionary<string, object>
            {
                { "User", new { Uid = workflow.DeviceId } },
                { "Event", (int)EventType.StartSession },
                { "Namespace", TTS_NAMESPACE },
                { "ReqParams",
                    new {
                        Speaker = this.SpeakerId,
                        AudioParams = new {
                            Format = this.AudioEncoding,
                            SampleRate = this.GetTtsSampleRate(),
                            EnableTimestamp = false,
                            this.SpeechRate,
                            this.LoudnessRate,
                        }
                    }
                },
                { "Additions",
                    JsonHelper.Serialize(new {
                        DisableMarkdownFilter = false,
                        CacheConfig = new
                        {
                            TextType = 1,
                            UseCache = true
                        },
                        SectionId = seg.ParagraphId
                    })
                }
            };
            await this.StartSessionAsync(seg.SentenceId, JsonHelper.SerializeToUtf8Bytes(startReq), token);

            token.ThrowIfCancellationRequested();

            Dictionary<string, object> ttsReq = new Dictionary<string, object>
            {
                { "User", new { Uid = workflow.DeviceId } },
                { "Event", (int)EventType.TaskRequest },
                { "Namespace", TTS_NAMESPACE },
                { "ReqParams",
                    new {
                        Text = seg.Content,
                        Speaker = this.SpeakerId,
                        AudioParams = new {
                            Format = this.AudioEncoding,
                            SampleRate = this.GetTtsSampleRate(),
                            EnableTimestamp = false,
                            this.SpeechRate,
                            this.LoudnessRate
                        }
                    }
                },
            };

            this.TTSEventCallback?.OnBeforeProcessing(seg.Content, seg.IsFirstSegment, seg.IsLastSegment);

            await this.TaskRequestAsync(seg.SentenceId, JsonHelper.SerializeToUtf8Bytes(ttsReq));
            token.ThrowIfCancellationRequested();

            try
            {
                await this.FinishSessionAsync(seg.SentenceId, token);

                if (seg.IsLastSegment)
                {
                    await this.FinishActiveConnectionAsync(token);
                }

                this.TTSEventCallback?.OnProcessed(seg.Content, seg.IsFirstSegment, seg.IsLastSegment, TtsGenerateResult.Success);
            }
            catch (OperationCanceledException)
            {
                this.TTSEventCallback?.OnProcessed(seg.Content, seg.IsFirstSegment, seg.IsLastSegment, TtsGenerateResult.Aborted);
                this.Logger.LogWarning("TTS 合成已取消。");
                throw;
            }
            catch (Exception ex)
            {
                this.TTSEventCallback?.OnProcessed(seg.Content, seg.IsFirstSegment, seg.IsLastSegment, TtsGenerateResult.Failed);
                this.Logger.LogError(ex, "TTS 合成失败。");
                throw;
            }
            finally
            {
                this.ProcessingSegments.Remove(seg.SentenceId);
                this.StreamingActive = false;
            }
        }

        private async Task FinishActiveConnectionAsync(CancellationToken token)
        {
            if (Interlocked.Exchange(ref this._connectionStarted, 0) == 0)
            {
                return;
            }

            this.StreamingActive = false;
            try
            {
                await this.FinishConnectionAsync(token);
            }
            finally
            {
                if (this.WebSocketClient is not null)
                {
                    await this.WebSocketClient.CloseAsync();
                }
            }
        }

        public override void Dispose()
        {
            this.FailAllWaits(new OperationCanceledException("TTS 提供程序已释放"));
            this.ClearAudioBuffer();
            this.WebSocketClient?.Dispose();
            this.TTSEventCallback?.OnProcessed(string.Empty, false, false, TtsGenerateResult.Aborted);
        }
    }
}

using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.BuildConfigs;
using Agent.Telephone.Common.Contexts;
using Agent.Telephone.Helpers;
using Agent.Telephone.Media.Abstractions;
using Agent.Telephone.Providers.ASR.Contexts;
using Microsoft.Extensions.Logging;
using SherpaOnnx;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Agent.Telephone.Providers.ASR.Sherpa
{
    internal abstract class BaseSherpaAsr<TLogger> : BaseProvider<TLogger, ModelSetting>, IAsr
    {
        private readonly IAudioEditor _audioEditor;
        private readonly ConcurrentDictionary<string, IAsrEventCallback> _asrSessions;
        private readonly Channel<AsrRequest> _requestChannel;
        private readonly CancellationTokenSource _shutdownCts;

        private OfflineRecognizer? _offlineRecognizer;

        private Task? _backgroudProcessingTask;

        protected BaseSherpaAsr(IAudioEditor audioEditor, ILogger<TLogger> logger) : base(logger)
        {
            this._audioEditor = audioEditor;
            this._asrSessions = new ConcurrentDictionary<string, IAsrEventCallback>();
            this._requestChannel = Channel.CreateUnbounded<AsrRequest>();
            this._shutdownCts = new CancellationTokenSource();
        }


        public int MaxBatchSize { get; protected set; } = 10;
        public int BatchWaitTimeMs { get; protected set; } = 20;
        public AudioSavingConfig? AudioSavingConfig { get; protected set; }
        public override string ProviderType => "asr";
        public bool IsStreaming => false;

        protected void Build(OfflineRecognizerConfig offlineRecognizerConfig, ModelSetting modelSetting)
        {
            offlineRecognizerConfig.ModelConfig.Tokens = Path.Combine(this.ModelFileFoler, "tokens.txt");

            string? hotwordsFile = modelSetting.Config.GetConfigValueOrDefault("HotwordsFile");
            if (!string.IsNullOrWhiteSpace(hotwordsFile))
            {
                offlineRecognizerConfig.HotwordsFile = Path.Combine(this.ModelFileFoler, hotwordsFile);
                offlineRecognizerConfig.HotwordsScore = modelSetting.Config.GetConfigValueOrDefault("HotwordsScore", 1.5F);
                offlineRecognizerConfig.DecodingMethod = "modified_beam_search";
                offlineRecognizerConfig.MaxActivePaths = modelSetting.Config.GetConfigValueOrDefault("MaxActivePaths", 4);
            }
            else
            {
                offlineRecognizerConfig.DecodingMethod = "greedy_search";
            }
            //this._config.RuleFsts = this.ModelSetting.Config.RuleFsts;

            this.MaxBatchSize = modelSetting.Config.GetConfigValueOrDefault("MaxBatchSize", 10);
            this.BatchWaitTimeMs = modelSetting.Config.GetConfigValueOrDefault("BatchWaitTimeMs", 20);
            this.AudioSavingConfig = modelSetting.Config.GetConfigValueOrDefault("FileSavingOption", new AudioSavingConfig(false));
            if (this.AudioSavingConfig.SaveFile && !Directory.Exists(this.AudioSavingConfig.SavePath))
            {
                Directory.CreateDirectory(this.AudioSavingConfig.SavePath);
            }
            this._offlineRecognizer = new OfflineRecognizer(offlineRecognizerConfig);
            this._backgroudProcessingTask = Task.Run(this.ProcessingAsync);
        }

        public void RegisterDevice(ActiveCallContext activeCall, IAsrEventCallback callback)
        {
            this._asrSessions.AddOrUpdate(activeCall.DeviceId, callback, (_, _) => callback);
            base.RegisterDevice(activeCall);
            this.Logger.LogDebug("已注册 ASR 会话，设备: {deviceId}", activeCall.DeviceId);
        }

        public override void UnregisterDevice(ActiveCallContext activeCall)
        {
            if (this._asrSessions.TryRemove(activeCall.DeviceId, out _))
            {
                this.Logger.LogDebug("已注销 ASR 会话，设备: {deviceId}", activeCall.DeviceId);
            }
            base.UnregisterDevice(activeCall);
        }

        public override bool CheckDeviceRegistered(string deviceId)
        {
            return this._asrSessions.ContainsKey(deviceId);
        }

        public async Task ConvertSpeechTextAsync(Workflow<float[]> workflow, int sampleRate, CancellationToken token)
        {
            if (!this.CheckDeviceRegistered(workflow.DeviceId))
            {
                //throw new SessionNotInitializedException();
            }
            if (this._offlineRecognizer == null)
            {
                throw new ArgumentNullException("请先构建 ASR 提供程序。");
            }

            if (this._asrSessions.TryGetValue(workflow.DeviceId, out var callback))
            {
                OfflineStream? offlineStream = null;
                try
                {
                    if (this.AudioSavingConfig is not null && this.AudioSavingConfig.SaveFile)
                    {
                        string fileName = FileNameHelper.CreateAudioFileName(
                            this.ProviderType,
                            workflow.CallerNumber,
                            workflow.DialedNumber,
                            workflow.TurnId.ToString(),
                            this.AudioSavingConfig.Format);
                        string filePath = Path.Combine(this.AudioSavingConfig.SavePath, fileName);

                        int outputSampleRate = this.GetNegotiatedAudioSavingSampleRate();
                        float[] savedAudio = ResampleForAudioSaving(
                            workflow.Data,
                            sampleRate,
                            outputSampleRate);
                        bool userSpeechFileSavingResult = await this._audioEditor.SaveAudioFileAsync(
                            filePath,
                            savedAudio,
                            outputSampleRate,
                            channels: 1,
                            bitRate: 128000);
                        if (userSpeechFileSavingResult)
                        {
                            this.Logger.LogDebug("用户语音音频文件 {fileName} 保存成功。", fileName);
                        }
                        else
                        {
                            this.Logger.LogWarning("保存用户语音音频文件 {fileName} 失败。", fileName);
                        }
                    }

                    offlineStream = this._offlineRecognizer.CreateStream();
                    offlineStream.AcceptWaveform(sampleRate, workflow.Data);

                    AsrRequest asrRequest = new AsrRequest(
                        workflow.DeviceId,
                        offlineStream,
                        sampleRate,
                        workflow.TurnId,
                        callback,
                        token);

                    await this._requestChannel.Writer.WriteAsync(asrRequest, token);
                    offlineStream = null;
                }
                catch (OperationCanceledException)
                {
                    this.Logger.LogDebug("ASR 请求已取消，设备 {DeviceId}", workflow.DeviceId);
                    throw;
                }
                catch (Exception ex)
                {
                    this.Logger.LogError(ex, "{providerType} 发生意外错误。", this.ProviderType);
                    throw;
                }
                finally
                {
                    offlineStream?.Dispose();
                }
            }
        }

        public Task ConvertSpeechTextStreamingAsync(
            Workflow<float[]> workflow,
            int sampleRate,
            StreamingAsrOperation operation,
            CancellationToken token)
        {
            throw new NotSupportedException($"{this.ModelName} does not support streaming ASR.");
        }

        private async Task ProcessingAsync()
        {
            if (this._offlineRecognizer == null)
            {
                throw new ArgumentNullException("请先构建 ASR 提供程序。");
            }
            CancellationToken shutDownToken = this._shutdownCts.Token;

            while (!shutDownToken.IsCancellationRequested)
            {
                try
                {
                    if (!await this._requestChannel.Reader.WaitToReadAsync(shutDownToken))
                    {
                        break;
                    }

                    var batchRequests = new List<AsrRequest>(this.MaxBatchSize);

                    if (this._requestChannel.Reader.TryRead(out var firstRequest))
                    {
                        batchRequests.Add(firstRequest);
                    }

                    if (this.MaxBatchSize > 1)
                    {
                        while (batchRequests.Count < this.MaxBatchSize && this._requestChannel.Reader.TryRead(out var req))
                        {
                            batchRequests.Add(req);
                        }

                        if (this.BatchWaitTimeMs > 0 && batchRequests.Count < this.MaxBatchSize)
                        {
                            Task timeoutTask = Task.Delay(this.BatchWaitTimeMs, shutDownToken);

                            while (batchRequests.Count < this.MaxBatchSize)
                            {
                                Task waitToReadTask = this._requestChannel.Reader.WaitToReadAsync(shutDownToken).AsTask();
                                Task completed = await Task.WhenAny(waitToReadTask, timeoutTask);

                                if (completed == timeoutTask || shutDownToken.IsCancellationRequested)
                                {
                                    break;
                                }

                                while (batchRequests.Count < this.MaxBatchSize && this._requestChannel.Reader.TryRead(out var req))
                                {
                                    batchRequests.Add(req);
                                }
                            }
                        }
                    }

                    if (batchRequests.Count > 0)
                    {
                        // Filter cancelled requests
                        var validRequests = new List<AsrRequest>(batchRequests.Count);
                        foreach (var request in batchRequests)
                        {
                            if (request.Token.IsCancellationRequested)
                            {
                                request.Stream.Dispose();
                                this.Logger.LogDebug("ASR 请求在处理前已取消，设备 {DeviceId}", request.DeviceId);
                                continue;
                            }
                            validRequests.Add(request);
                        }


                        if (validRequests.Count > 0)
                        {
                            try
                            {
                                this._offlineRecognizer.Decode(validRequests.Select(b => b.Stream));
                            }
                            catch (Exception ex)
                            {
                                foreach (AsrRequest request in validRequests)
                                {
                                    try
                                    {
                                        request.Callback.OnSpeechTextConverted(request.TurnId, false, string.Empty);
                                    }
                                    catch (Exception callbackException)
                                    {
                                        this.Logger.LogError(callbackException, "通知设备 {DeviceId} 的 ASR 失败时出错。", request.DeviceId);
                                    }
                                    finally
                                    {
                                        request.Stream.Dispose();
                                    }
                                }

                                this.Logger.LogError(ex, "解码 ASR 请求时出错。");
                                continue;
                            }

                            foreach (AsrRequest request in validRequests)
                            {
                                try
                                {
                                    if (request.Token.IsCancellationRequested)
                                    {
                                        this.Logger.LogDebug("ASR 请求在解码后已取消，设备 {DeviceId}", request.DeviceId);
                                    }
                                    else
                                    {
                                        string resultText = request.Stream.Result.Text;
                                        request.Callback.OnSpeechTextConverted(request.TurnId, true, resultText);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    request.Callback.OnSpeechTextConverted(request.TurnId, false, string.Empty);
                                    this.Logger.LogError(ex, "处理 ASR 结果时出错，设备 {DeviceId}", request.DeviceId);
                                }
                                finally
                                {
                                    request.Stream.Dispose();
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    this.Logger.LogError(ex, "ASR 处理循环中发生错误。");
                }
            }
        }

        public override void Dispose()
        {
            this._shutdownCts.Cancel();

            try
            {
                this._backgroudProcessingTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                this.Logger.LogError("等待后台任务完成失败。");
            }

            this._offlineRecognizer?.Dispose();
            this._shutdownCts.Dispose();
        }

    }
}

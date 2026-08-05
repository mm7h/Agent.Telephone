using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Providers.Dtmf
{
    internal sealed class DtmfInputProvider : BaseProvider<DtmfInputProvider, ModelSetting>, IDtmfInput
    {
        private const int INPUT_TIMEOUT_SECONDS = 15;
        private const DtmfKey SUPPORTED_KEYS =
            DtmfKey.Zero | DtmfKey.One | DtmfKey.Two | DtmfKey.Three |
            DtmfKey.Four | DtmfKey.Five | DtmfKey.Six | DtmfKey.Seven |
            DtmfKey.Eight | DtmfKey.Nine | DtmfKey.Star | DtmfKey.Pound;

        private readonly object _windowLock = new();
        private ActiveCallContext? _windowCall;
        private DtmfKey _acceptedKeys;
        private CancellationTokenSource? _windowCts;

        public DtmfInputProvider(ILogger<DtmfInputProvider> logger) : base(logger)
        {
        }

        public override string ProviderType => "dtmf-input";

        public override string ModelName => nameof(DtmfInputProvider);

        public override bool Build(ModelSetting settings) => true;

        public Task<DtmfInputResult> RequestDtmfInputAsync(
            ActiveCallContext call,
            DtmfKey keys,
            CancellationToken cancellationToken)
        {
            if (!call.UserAgent.IsCallActive || call.CallToken.IsCancellationRequested)
            {
                return Task.FromResult(new DtmfInputResult(DtmfInputStatus.CallEnded, "当前通话已经结束。"));
            }

            if (!this.AreRequestedKeysAvailable(call, keys))
            {
                return Task.FromResult(new DtmfInputResult(DtmfInputStatus.InvalidKey, "按键未映射到当前可用功能。"));
            }

            CancellationTokenSource windowCts;
            lock (this._windowLock)
            {
                if (this._windowCall is not null)
                {
                    return Task.FromResult(new DtmfInputResult(DtmfInputStatus.AlreadyWaiting, "当前正在等待按键。"));
                }

                windowCts = CancellationTokenSource.CreateLinkedTokenSource(call.CallToken, cancellationToken);
                this._windowCall = call;
                this._acceptedKeys = keys;
                this._windowCts = windowCts;
                call.TurnTokenChanged += this.OnTurnTokenChanged;
            }

            _ = this.ExpireWindowAsync(call, windowCts);
            return Task.FromResult(new DtmfInputResult(DtmfInputStatus.Accepted));
        }

        public void HandleDtmfTone(ActiveCallContext call, byte tone)
        {
            if (!TryGetDtmfKey(tone, out DtmfKey key))
            {
                this.Logger.LogDebug("忽略不支持的 DTMF 事件码 {Tone}。", tone);
                return;
            }

            CancellationTokenSource? windowCts = null;
            lock (this._windowLock)
            {
                if (!ReferenceEquals(this._windowCall, call) ||
                    (this._acceptedKeys & key) == DtmfKey.None)
                {
                    return;
                }

                windowCts = this.ClearWindowLocked();
            }

            windowCts?.Cancel();
            windowCts?.Dispose();
            call.RestartTurn();
            _ = this.DispatchInputAsync(call, key);
        }

        public override void Dispose()
        {
            CancellationTokenSource? windowCts;
            lock (this._windowLock)
            {
                windowCts = this.ClearWindowLocked();
            }

            windowCts?.Cancel();
            windowCts?.Dispose();
        }

        private bool AreRequestedKeysAvailable(ActiveCallContext call, DtmfKey keys)
        {
            if (keys == DtmfKey.None || (keys & ~SUPPORTED_KEYS) != DtmfKey.None)
            {
                return false;
            }

            DtmfKey availableKeys = call.AIAgentContext.PrivateProvider.GetAvailableDtmfKeys();
            return (keys & availableKeys) == keys;
        }

        private async Task ExpireWindowAsync(ActiveCallContext call, CancellationTokenSource windowCts)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(INPUT_TIMEOUT_SECONDS), windowCts.Token);

                lock (this._windowLock)
                {
                    if (!ReferenceEquals(this._windowCall, call) || !ReferenceEquals(this._windowCts, windowCts))
                    {
                        return;
                    }

                    this.ClearWindowLocked();
                }

                await this.DispatchTimeoutAsync(call);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                windowCts.Dispose();
            }
        }

        private async Task DispatchInputAsync(ActiveCallContext call, DtmfKey key)
        {
            try
            {
                ILlm? llm = call.AIAgentContext.PrivateProvider.Llm;
                if (llm is not null && call.UserAgent.IsCallActive)
                {
                    await llm.StartDialogueAsync(
                        $"系统输入：用户在当前按键菜单中按下了 {GetDisplayName(key)}。请仅根据该按键和已授权功能继续处理。",
                        call.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "处理设备 {DeviceId} 的 DTMF 按键失败。", call.DeviceId);
            }
        }

        private async Task DispatchTimeoutAsync(ActiveCallContext call)
        {
            try
            {
                ILlm? llm = call.AIAgentContext.PrivateProvider.Llm;
                if (llm is not null && call.UserAgent.IsCallActive)
                {
                    await llm.StartDialogueAsync("系统输入：按键等待已超时，用户没有按键。", call.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                this.Logger.LogError(exception, "处理设备 {DeviceId} 的 DTMF 超时失败。", call.DeviceId);
            }
        }

        private void OnTurnTokenChanged(CancellationToken token)
        {
            CancellationTokenSource? windowCts;
            lock (this._windowLock)
            {
                windowCts = this.ClearWindowLocked();
            }

            windowCts?.Cancel();
        }

        private CancellationTokenSource? ClearWindowLocked()
        {
            ActiveCallContext? call = this._windowCall;
            if (call is not null)
            {
                call.TurnTokenChanged -= this.OnTurnTokenChanged;
            }

            this._windowCall = null;
            this._acceptedKeys = DtmfKey.None;
            CancellationTokenSource? windowCts = this._windowCts;
            this._windowCts = null;
            return windowCts;
        }

        private static bool TryGetDtmfKey(byte tone, out DtmfKey key)
        {
            key = tone switch
            {
                0 => DtmfKey.Zero,
                1 => DtmfKey.One,
                2 => DtmfKey.Two,
                3 => DtmfKey.Three,
                4 => DtmfKey.Four,
                5 => DtmfKey.Five,
                6 => DtmfKey.Six,
                7 => DtmfKey.Seven,
                8 => DtmfKey.Eight,
                9 => DtmfKey.Nine,
                10 => DtmfKey.Star,
                11 => DtmfKey.Pound,
                _ => DtmfKey.None,
            };
            return key != DtmfKey.None;
        }

        private static string GetDisplayName(DtmfKey key)
        {
            return key switch
            {
                DtmfKey.Star => "星号键",
                DtmfKey.Pound => "井号键",
                _ => ((int)Math.Log2((int)key)).ToString(),
            };
        }
    }
}

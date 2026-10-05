using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Providers.Dtmf
{
    internal sealed class DefaultDtmfInput : BaseProvider<DefaultDtmfInput, ModelSetting>, IDtmfInput
    {
        private static readonly TimeSpan INPUT_TIMEOUT = TimeSpan.FromSeconds(15);
        private const DtmfKey SUPPORTED_KEYS =
            DtmfKey.Zero | DtmfKey.One | DtmfKey.Two | DtmfKey.Three |
            DtmfKey.Four | DtmfKey.Five | DtmfKey.Six | DtmfKey.Seven |
            DtmfKey.Eight | DtmfKey.Nine | DtmfKey.Star | DtmfKey.Pound;

        private readonly object _windowLock = new();
        private DtmfInputWindow? _window;

        public DefaultDtmfInput(ILogger<DefaultDtmfInput> logger) : base(logger)
        {
        }

        public override string ProviderType => "dtmf-input";

        public override string ModelName => nameof(DefaultDtmfInput);

        public override bool Build(ModelSetting settings) => true;

        public async Task<DtmfInputResult> RequestDtmfInputAsync(
            ActiveCallContext call,
            DtmfKey keys,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            call.CallToken.ThrowIfCancellationRequested();
            if (!this.AreRequestedKeysAvailable(call, keys))
            {
                return new DtmfInputResult(DtmfInputStatus.InvalidKey, "按键未映射到当前可用功能。");
            }

            if (!this.TryStartWindow(call, keys, cancellationToken, out DtmfInputWindow? window))
            {
                return new DtmfInputResult(DtmfInputStatus.AlreadyWaiting, "当前正在等待按键。");
            }

            DtmfKey? selectedKey = await this.WaitForKeyAsync(window!, INPUT_TIMEOUT);
            return selectedKey is DtmfKey key
                ? new DtmfInputResult(DtmfInputStatus.Accepted) { SelectedKey = key }
                : new DtmfInputResult(DtmfInputStatus.TimedOut, "等待按键已超时。");
        }

        public async Task<DtmfKey?> WaitForDtmfKeyAsync(
            ActiveCallContext call,
            DtmfKey keys,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (call.CallToken.IsCancellationRequested ||
                !AreSupportedKeys(keys) || timeout <= TimeSpan.Zero)
            {
                return null;
            }

            if (!this.TryStartWindow(call, keys, cancellationToken, out DtmfInputWindow? window))
            {
                return null;
            }

            return await this.WaitForKeyAsync(window!, timeout);
        }

        public void HandleDtmfTone(ActiveCallContext call, byte tone)
        {
            if (!TryGetDtmfKey(tone, out DtmfKey key))
            {
                this.Logger.LogDebug("忽略不支持的 DTMF 事件码 {Tone}。", tone);
                return;
            }

            DtmfInputWindow? window;
            lock (this._windowLock)
            {
                window = this._window;
                if (window is null || !ReferenceEquals(window.Call, call) ||
                    (window.AcceptedKeys & key) == DtmfKey.None)
                {
                    return;
                }

                this.ClearWindowLocked(window);
            }

            window.Completion.TrySetResult(key);
        }

        private bool AreRequestedKeysAvailable(ActiveCallContext call, DtmfKey keys)
        {
            return AreSupportedKeys(keys) &&
                (keys & call.AIAgentContext.PrivateProvider.GetAvailableDtmfKeys()) == keys;
        }

        private static bool AreSupportedKeys(DtmfKey keys)
        {
            return keys != DtmfKey.None && (keys & ~SUPPORTED_KEYS) == DtmfKey.None;
        }

        private bool TryStartWindow(
            ActiveCallContext call,
            DtmfKey keys,
            CancellationToken cancellationToken,
            out DtmfInputWindow? window)
        {
            lock (this._windowLock)
            {
                if (this._window is not null)
                {
                    window = null;
                    return false;
                }

                CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    call.CallToken,
                    cancellationToken);
                window = new DtmfInputWindow(call, keys, cancellation);
                this._window = window;
                call.TurnTokenChanged += this.OnTurnTokenChanged;
                return true;
            }
        }

        private async Task<DtmfKey?> WaitForKeyAsync(DtmfInputWindow window, TimeSpan timeout)
        {
            try
            {
                Task timeoutTask = Task.Delay(timeout, window.Cancellation.Token);
                Task completedTask = await Task.WhenAny(window.Completion.Task, timeoutTask);
                if (completedTask == window.Completion.Task)
                {
                    return await window.Completion.Task;
                }

                window.Cancellation.Token.ThrowIfCancellationRequested();
                return null;
            }
            finally
            {
                lock (this._windowLock)
                {
                    this.ClearWindowLocked(window);
                }

                window.Cancellation.Cancel();
                window.Cancellation.Dispose();
            }
        }

        private void OnTurnTokenChanged(CancellationToken token)
        {
            DtmfInputWindow? window;
            lock (this._windowLock)
            {
                window = this._window;
                if (window is not null)
                {
                    this.ClearWindowLocked(window);
                }
            }

            window?.Cancellation.Cancel();
        }

        private void ClearWindowLocked(DtmfInputWindow window)
        {
            if (!ReferenceEquals(this._window, window))
            {
                return;
            }

            window.Call.TurnTokenChanged -= this.OnTurnTokenChanged;
            this._window = null;
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

        public override void Dispose()
        {
            DtmfInputWindow? window;
            lock (this._windowLock)
            {
                window = this._window;
                if (window is not null)
                {
                    this.ClearWindowLocked(window);
                }
            }

            window?.Cancellation.Cancel();
        }

        private sealed class DtmfInputWindow
        {
            public DtmfInputWindow(ActiveCallContext call, DtmfKey acceptedKeys, CancellationTokenSource cancellation)
            {
                this.Call = call;
                this.AcceptedKeys = acceptedKeys;
                this.Cancellation = cancellation;
                this.Completion = new TaskCompletionSource<DtmfKey>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public ActiveCallContext Call { get; }
            public DtmfKey AcceptedKeys { get; }
            public CancellationTokenSource Cancellation { get; }
            public TaskCompletionSource<DtmfKey> Completion { get; }
        }
    }
}

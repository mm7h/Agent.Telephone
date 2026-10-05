using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Handlers
{
    internal abstract class BaseHandler : IHandler
    {
        private readonly object _lifetimeLock = new();
        private bool _disposed;
        private CancellationTokenSource? _handlerCts;
        private CancellationTokenRegistration _tokenRegistration;
        private bool _continueAfterCallEnded;
        public BaseHandler(TelephoneConfig config, ILogger logger)
        {
            this.Config = config;
            this.Logger = logger;
        }

        public TelephoneConfig Config { get; }
        public ILogger Logger { get; }
        public abstract string HandlerName { get; }
        public ActiveCallContext ActiveCallContext { get; set; } = null!;

        public abstract bool Build();

        protected CancellationToken HandlerToken { get; private set; }
        public void RegisterCancellationToken(ActiveCallContext activeCall, bool continueAfterCallEnded = false)
        {
            lock (this._lifetimeLock)
            {
                if (this._disposed)
                {
                    return;
                }
                this._continueAfterCallEnded = continueAfterCallEnded;
                activeCall.TurnTokenChanged += this.OnTurnTokenChanged;
            }
            this.ReplaceHandlerToken(activeCall.Token);
        }

        protected bool CheckWorkflowValid<T>(Workflow<T> workflow)
        {
            return workflow.DeviceId == this.ActiveCallContext.DeviceId
                && workflow.CallId == this.ActiveCallContext.CallId
                && workflow.TurnId == this.ActiveCallContext.TurnId;
        }

        protected virtual void OnHandlerTokenChanged()
        {
        }

        private void OnTurnTokenChanged(CancellationToken token)
        {
            if (this.ReplaceHandlerToken(token))
            {
                this.NotifyHandlerTokenChanged();
            }
        }

        private void NotifyHandlerTokenChanged()
        {
            lock (this._lifetimeLock)
            {
                if (!this._disposed)
                {
                    this.OnHandlerTokenChanged();
                }
            }
        }

        private bool ReplaceHandlerToken(CancellationToken token)
        {
            CancellationTokenRegistration previousRegistration;
            CancellationTokenSource? previousCts;
            lock (this._lifetimeLock)
            {
                if (this._disposed || token != this.ActiveCallContext.Token)
                {
                    return false;
                }
                previousRegistration = this._tokenRegistration;
                previousCts = this._handlerCts;
                this._handlerCts = this._continueAfterCallEnded
                    ? CancellationTokenSource.CreateLinkedTokenSource(token)
                    : CancellationTokenSource.CreateLinkedTokenSource(token, this.ActiveCallContext.CallToken);
                this.HandlerToken = this._handlerCts.Token;
                this._tokenRegistration = this.HandlerToken.Register(this.NotifyHandlerTokenChanged);
            }
            // Dispose may wait for a callback that needs the lifetime lock.
            previousRegistration.Dispose();
            previousCts?.Dispose();
            return true;
        }

        public void Dispose()
        {
            CancellationTokenRegistration registration;
            CancellationTokenSource? cts;
            lock (this._lifetimeLock)
            {
                if (this._disposed)
                {
                    return;
                }
                this._disposed = true;
                registration = this._tokenRegistration;
                cts = this._handlerCts;
                this._handlerCts = null;
            }
            if (this.ActiveCallContext is not null)
            {
                this.ActiveCallContext.TurnTokenChanged -= this.OnTurnTokenChanged;
            }
            registration.Dispose();
            cts?.Dispose();
            this.DisposeResources();
        }

        protected virtual void DisposeResources()
        {
        }
    }
}

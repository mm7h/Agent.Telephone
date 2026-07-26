using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Handlers
{
    internal abstract class BaseHandler : IHandler
    {
        private CancellationTokenSource? _handlerCts;
        private CancellationTokenRegistration _tokenRegistration;
        private ActiveCallContext? _activeCall;
        private bool _continueAfterCallEnded;
        public BaseHandler(TelephoneConfig config, ILogger logger)
        {
            this.Config = config;
            this.Logger = logger;
        }

        public TelephoneConfig Config { get; }
        public ILogger Logger { get; }
        public abstract string HandlerName { get; }
        public DeviceContext DeviceContext { get; set; } = null!;

        public abstract bool Build();

        protected CancellationToken HandlerToken { get; private set; }
        protected ActiveCallContext ActiveCallContext => this._activeCall
            ?? throw new InvalidOperationException("The handler has not been bound to an active call.");

        protected void RegisterCancellationToken(DeviceContext deviceContext, bool continueAfterCallEnded = false)
        {
            this.DeviceContext = deviceContext;
            ActiveCallContext? activeCall = deviceContext.ActiveCall;
            if (activeCall is null)
            {
                throw new InvalidOperationException("An active call is required before building a handler.");
            }

            this._activeCall = activeCall;
            this._continueAfterCallEnded = continueAfterCallEnded;
            activeCall.TurnTokenChanged += this.OnTurnTokenChanged;
            this.ReplaceHandlerToken(activeCall.Token);
        }

        protected bool CheckWorkflowValid<T>(Workflow<T> workflow)
        {
            ActiveCallContext? activeCall = this._activeCall;
            return activeCall is not null
                && workflow.DeviceId == activeCall.DeviceId
                && workflow.CallId == activeCall.CallId
                && workflow.TurnId == activeCall.TurnId;
        }

        protected virtual void OnHandlerTokenChanged()
        {
        }

        private void OnTurnTokenChanged(CancellationToken token)
        {
            this.ReplaceHandlerToken(token);
            this.OnHandlerTokenChanged();
        }

        private void ReplaceHandlerToken(CancellationToken token)
        {
            this._tokenRegistration.Dispose();
            this._handlerCts?.Dispose();
            this._handlerCts = this._continueAfterCallEnded || this._activeCall is null
                ? CancellationTokenSource.CreateLinkedTokenSource(token)
                : CancellationTokenSource.CreateLinkedTokenSource(token, this._activeCall.CallToken);
            this.HandlerToken = this._handlerCts.Token;
            this._tokenRegistration = this.HandlerToken.Register(this.OnHandlerTokenChanged);
        }

        public virtual void Dispose()
        {
            if (this._activeCall is not null)
            {
                this._activeCall.TurnTokenChanged -= this.OnTurnTokenChanged;
            }
            this._tokenRegistration.Dispose();
            this._handlerCts?.Dispose();
        }
    }
}

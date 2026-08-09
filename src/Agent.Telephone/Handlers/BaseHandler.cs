using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Handlers
{
    internal abstract class BaseHandler : IHandler
    {
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
            this._continueAfterCallEnded = continueAfterCallEnded;
            activeCall.TurnTokenChanged += this.OnTurnTokenChanged;
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
            this.ReplaceHandlerToken(token);
            this.OnHandlerTokenChanged();
        }

        private void ReplaceHandlerToken(CancellationToken token)
        {
            this._tokenRegistration.Dispose();
            this._handlerCts?.Dispose();
            this._handlerCts = this._continueAfterCallEnded
                ? CancellationTokenSource.CreateLinkedTokenSource(token)
                : CancellationTokenSource.CreateLinkedTokenSource(token, this.ActiveCallContext.CallToken);
            this.HandlerToken = this._handlerCts.Token;
            this._tokenRegistration = this.HandlerToken.Register(this.OnHandlerTokenChanged);
        }

        public virtual void Dispose()
        {
            this.ActiveCallContext.TurnTokenChanged -= this.OnTurnTokenChanged;
            this._tokenRegistration.Dispose();
            this._handlerCts?.Dispose();
        }
    }
}

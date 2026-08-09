using Agent.Telephone.Handlers;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Common.Contexts
{
    internal sealed class HandlerPipelineLifetime : IDisposable
    {
        private IReadOnlyList<IHandler>? _handlers;
        private IReadOnlyList<Action>? _completeWriters;
        private readonly IReadOnlyList<Task> _handlerTasks;
        private readonly ILogger _logger;

        public HandlerPipelineLifetime(
            IReadOnlyList<IHandler> handlers,
            IReadOnlyList<Action> completeWriters,
            IReadOnlyList<Task> handlerTasks,
            ILogger logger)
        {
            this._handlers = handlers;
            this._completeWriters = completeWriters;
            this._handlerTasks = handlerTasks;
            this._logger = logger;
        }

        public void Dispose()
        {
            IReadOnlyList<Action>? completeWriters =
                Interlocked.Exchange(ref this._completeWriters, null);
            if (completeWriters is null)
            {
                return;
            }

            foreach (Action complete in completeWriters)
            {
                complete();
            }

            try
            {
                Task.WhenAll(this._handlerTasks).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                this._logger.LogError(
                    exception,
                    "等待通话 Handler 后台管线结束时失败。");
            }

            IReadOnlyList<IHandler>? handlers = Interlocked.Exchange(ref this._handlers, null);
            if (handlers is null)
            {
                return;
            }

            for (int index = handlers.Count - 1; index >= 0; index--)
            {
                handlers[index].Dispose();
            }
        }
    }
}

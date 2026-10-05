using Agent.Telephone.Handlers;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Agent.Telephone.Common.Contexts
{
    internal sealed class HandlerPipeline : IDisposable
    {
        private readonly object _lifetimeLock = new();
        private IReadOnlyList<IHandler>? _handlers;
        private IReadOnlyList<Action>? _completeWriters;
        private IReadOnlyList<Task>? _handlerTasks;
        private ILogger? _logger;
        private Task? _disposeTask;
        private bool _disposed;

        public void InitHandlerPipeline(
            IReadOnlyList<IHandler> handlers,
            IReadOnlyList<Action> completeWriters,
            IReadOnlyList<Task> handlerTasks,
            ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(handlers);
            ArgumentNullException.ThrowIfNull(completeWriters);
            ArgumentNullException.ThrowIfNull(handlerTasks);
            ArgumentNullException.ThrowIfNull(logger);

            lock (this._lifetimeLock)
            {
                ObjectDisposedException.ThrowIf(this._disposed, this);
                if (this._handlers is not null)
                {
                    throw new InvalidOperationException("The handler pipeline has already been initialized.");
                }

                this._handlers = handlers;
                this._completeWriters = completeWriters;
                this._handlerTasks = handlerTasks;
                this._logger = logger;
            }
        }

        public Task DisposeAsync()
        {
            IReadOnlyList<Action>? completeWriters;
            IReadOnlyList<Task>? handlerTasks;
            IReadOnlyList<IHandler>? handlers;
            ILogger? logger;
            lock (this._lifetimeLock)
            {
                if (this._disposeTask is not null)
                {
                    return this._disposeTask;
                }

                this._disposed = true;
                completeWriters = this._completeWriters;
                handlerTasks = this._handlerTasks;
                handlers = this._handlers;
                logger = this._logger;
                this._completeWriters = null;
                this._handlerTasks = null;
                this._handlers = null;
                this._logger = null;
                this._disposeTask = this.DisposeAsync(completeWriters, handlerTasks, handlers, logger);
                return this._disposeTask;
            }
        }

        public void Dispose()
        {
            this.DisposeAsync().GetAwaiter().GetResult();
        }

        private async Task DisposeAsync(
            IReadOnlyList<Action>? completeWriters,
            IReadOnlyList<Task>? handlerTasks,
            IReadOnlyList<IHandler>? handlers,
            ILogger? logger)
        {
            if (completeWriters is null)
            {
                return;
            }

            foreach (Action complete in completeWriters)
            {
                try
                {
                    complete();
                }
                catch (Exception exception)
                {
                    logger?.LogError(exception, "完成 Handler 管线 Channel 时失败。");
                }
            }

            if (handlerTasks is not null)
            {
                try
                {
                    await Task.WhenAll(handlerTasks);
                }
                catch (Exception exception)
                {
                    logger?.LogError(exception, "等待通话 Handler 管线结束时失败。");
                }
            }

            if (handlers is null)
            {
                return;
            }

            for (int index = handlers.Count - 1; index >= 0; index--)
            {
                try
                {
                    handlers[index].Dispose();
                }
                catch (Exception exception)
                {
                    logger?.LogError(exception, "释放 Handler {HandlerName} 时失败。", handlers[index].HandlerName);
                }
            }
        }
    }
}

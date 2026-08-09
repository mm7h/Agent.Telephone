using Agent.Telephone.Common.Contexts;
using System.Threading.Channels;

namespace Agent.Telephone.Handlers.AIAdapterHandlers
{
    internal interface IInAIAdapterHandler<T> : IHandler
    {
        ChannelReader<Workflow<T>> PreviousReader { get; set; }

        Task HandleAsync();
    }
}

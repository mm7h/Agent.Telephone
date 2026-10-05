using Agent.Telephone.Common.Contexts;
using System.Threading.Channels;

namespace Agent.Telephone.Handlers.AIAdapterHandlers
{
    internal interface IOutAIAdapterHandler<T> : IHandler
    {
        ChannelWriter<Workflow<T>> NextWriter { get; set; }
    }
}

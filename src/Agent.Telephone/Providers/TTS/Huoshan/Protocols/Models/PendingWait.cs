using System;
using System.Threading.Tasks;

namespace Agent.Telephone.Providers.TTS.Huoshan.Protocols.Models
{
    internal class PendingWait
    {
        public required Func<Message, bool> Match { get; init; }
        public required TaskCompletionSource<Message> Tcs { get; init; }
    }
}

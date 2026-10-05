using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.ObjectPool;

namespace Agent.Telephone.Common.ObjectPoolPolicies
{
    internal sealed class MixedAudioPacketPolicy : PooledObjectPolicy<MixedAudioPacket>
    {
        public override MixedAudioPacket Create() => new();

        public override bool Return(MixedAudioPacket obj)
        {
            obj.Reset();
            return true;
        }
    }
}

using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.ObjectPool;

namespace Agent.Telephone.Common.ObjectPoolPolicies
{
    internal sealed class OutAudioSegmentPolicy : PooledObjectPolicy<OutAudioSegment>
    {
        public override OutAudioSegment Create() => new();

        public override bool Return(OutAudioSegment obj)
        {
            obj.Reset();
            return true;
        }
    }
}

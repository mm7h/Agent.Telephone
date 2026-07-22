using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.ObjectPool;

namespace Agent.Telephone.Common.ObjectPoolPolicies
{
    internal sealed class OutSegmentPolicy : PooledObjectPolicy<OutSegment>
    {
        public override OutSegment Create() => new();

        public override bool Return(OutSegment obj)
        {
            obj.Reset();
            return true;
        }
    }
}

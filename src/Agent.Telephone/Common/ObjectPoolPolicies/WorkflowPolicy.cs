using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.ObjectPool;

namespace Agent.Telephone.Common.ObjectPoolPolicies
{
    internal sealed class WorkflowPolicy<T> : PooledObjectPolicy<Workflow<T>>
    {
        public override Workflow<T> Create() => new();

        public override bool Return(Workflow<T> obj)
        {
            obj.Reset();
            return true;
        }
    }
}

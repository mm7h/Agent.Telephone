using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Contexts;

namespace Agent.Telephone.Providers
{
    internal interface IDtmfInput : IProvider<ModelSetting>
    {
        Task<DtmfInputResult> RequestDtmfInputAsync(ActiveCallContext call, DtmfKey keys, CancellationToken cancellationToken);

        Task<DtmfKey?> WaitForDtmfKeyAsync(ActiveCallContext call, DtmfKey keys, TimeSpan timeout, CancellationToken cancellationToken);

        void HandleDtmfTone(ActiveCallContext call, byte tone);
    }
}

using Microsoft.Extensions.DependencyInjection;

namespace Agent.Telephone.Tests;

internal static class TestServices
{
    public static IServiceScopeFactory ScopeFactory { get; } = new ServiceCollection()
        .BuildServiceProvider()
        .GetRequiredService<IServiceScopeFactory>();
}

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HomeOps.Api;

internal static class ServiceCollectionExtensions
{
    internal static IServiceCollection AddSystemTimeProvider(this IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        return services;
    }
}

using APTM.Gate.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace APTM.Gate.Workers;

public static class DependencyInjection
{
    public static IServiceCollection AddWorkerServices(this IServiceCollection services)
    {
        services.AddHostedService<TcpReaderWorker>();
        services.AddSingleton<IReaderStatusProvider>(sp =>
            sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
              .OfType<TcpReaderWorker>()
              .First());
        services.AddHostedService<BufferProcessorWorker>();
        // WatchdogService disabled — healthcheck cron provides recovery instead
        // services.AddHostedService<WatchdogService>();
        return services;
    }
}

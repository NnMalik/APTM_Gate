using APTM.Gate.Core.Interfaces;
using APTM.Gate.Infrastructure.Persistence;
using APTM.Gate.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace APTM.Gate.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<GateDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("GateDb")));

        services.AddSingleton<IGateStatusProvider, GateStatusProvider>();
        services.AddScoped<IGateConfigService, GateConfigService>();
        services.AddScoped<ITagBufferService, TagBufferService>();
        services.AddScoped<IBufferProcessingService, BufferProcessingService>();
        services.AddScoped<ISyncHubService, SyncHubService>();
        services.AddScoped<IDiagnosticsService, DiagnosticsService>();

        services.AddHostedService<PostgresInitService>();

        return services;
    }
}

using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace APTM.Gate.Infrastructure.Services;

/// <summary>
/// Singleton cache over <see cref="IGateIdentityService.GetAsync"/>. Loads on first access,
/// re-loads when invalidated. Used by workers (early-exit on un-provisioned / wrong role) and by
/// endpoint filters (fast role-based authorization).
/// </summary>
public sealed class GateIdentityProvider : IGateIdentityProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GateIdentityProvider> _logger;
    private readonly object _lock = new();

    private GateIdentityInfo? _cached;
    private bool _loaded;

    public GateIdentityProvider(IServiceScopeFactory scopeFactory, ILogger<GateIdentityProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public GateIdentityInfo? Current
    {
        get
        {
            if (_loaded) return _cached;

            lock (_lock)
            {
                if (!_loaded) Load();
                return _cached;
            }
        }
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _loaded = false;
            _cached = null;
        }
    }

    private void Load()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IGateIdentityService>();
            // Synchronous wait is acceptable here — only happens on cache miss / startup.
            _cached = svc.GetAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load gate identity — treating as un-provisioned");
            _cached = null;
        }
        finally
        {
            _loaded = true;
        }
    }
}

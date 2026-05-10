using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace APTM.Gate.Infrastructure.Services;

/// <summary>
/// Singleton cache over <see cref="IReaderConfigService.GetAsync"/>. Loaded lazily on first
/// access so a brand-new NUC (no DB row, just appsettings defaults) still works. Workers
/// query <see cref="Current"/> on each reconnect, so a write to /gate/reader/settings
/// followed by <see cref="Invalidate"/> is picked up within one reconnect cycle (~5s).
/// </summary>
public sealed class ReaderConfigProvider : IReaderConfigProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReaderConfigProvider> _logger;
    private readonly object _lock = new();

    private ReaderSettingsInfo? _cached;
    private bool _loaded;

    public ReaderConfigProvider(IServiceScopeFactory scopeFactory, ILogger<ReaderConfigProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public ReaderSettingsInfo Current
    {
        get
        {
            if (_loaded && _cached is not null) return _cached;

            lock (_lock)
            {
                if (!_loaded || _cached is null) Load();
                return _cached!;
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
            var svc = scope.ServiceProvider.GetRequiredService<IReaderConfigService>();
            // Synchronous wait is acceptable here — only happens on cache miss / startup.
            _cached = svc.GetAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // If even the fallback to appsettings fails, surface the error and use hard-coded
            // safe defaults so the worker doesn't crash on startup.
            _logger.LogError(ex, "Failed to load reader config — using hard-coded defaults");
            _cached = new ReaderSettingsInfo
            {
                Host = "127.0.0.1",
                Port = 27011,
                DefaultPower = 20,
                EpcFilterBits = 0,
                ReconnectDelayMs = 5000,
                Source = "default"
            };
        }
        finally
        {
            _loaded = true;
        }
    }
}

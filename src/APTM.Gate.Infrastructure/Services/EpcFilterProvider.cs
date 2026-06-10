using System.Globalization;
using System.Numerics;
using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace APTM.Gate.Infrastructure.Services;

/// <summary>
/// Singleton cache over <see cref="IEpcFilterService.GetAsync"/>. Loaded lazily on first
/// access so a brand-new NUC (no DB row) still works — the filter simply reports as
/// disabled. <see cref="TagBufferService"/> queries <see cref="IsAccepted"/> on every
/// ingest batch, so a write to /gate/epc-filter followed by <see cref="Invalidate"/> is
/// picked up on the next batch with no service restart.
/// </summary>
public sealed class EpcFilterProvider : IEpcFilterProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EpcFilterProvider> _logger;
    private readonly object _lock = new();

    private EpcFilterInfo? _cached;
    private bool _loaded;

    // Parsed bounds, cached alongside _cached so IsAccepted doesn't re-parse the
    // configured range on every read. Only meaningful when _boundsValid is true.
    private bool _boundsValid;
    private BigInteger _lowerBound;
    private BigInteger _upperBound;

    public EpcFilterProvider(IServiceScopeFactory scopeFactory, ILogger<EpcFilterProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public EpcFilterInfo Current
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
            _boundsValid = false;
        }
    }

    public bool IsAccepted(string tagEpc)
    {
        var filter = Current;            // ensures the cache (and bounds) are loaded
        if (!filter.Enabled) return true;
        if (!_boundsValid) return true;  // enabled but misconfigured — fail open (accept all)
        if (!TryParseEpc(tagEpc, out var value)) return false; // unparseable read while filtering — reject
        return value >= _lowerBound && value <= _upperBound;
    }

    private void Load()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IEpcFilterService>();
            // Synchronous wait is acceptable here — only happens on cache miss / startup.
            _cached = svc.GetAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // If even reading the row fails, default to disabled so ingestion is never
            // blocked by a filter-load error.
            _logger.LogError(ex, "Failed to load EPC filter — defaulting to disabled (accept all)");
            _cached = new EpcFilterInfo { Enabled = false, Source = "default" };
        }
        finally
        {
            _boundsValid = _cached is { Enabled: true }
                && TryParseEpc(_cached.RangeStart, out _lowerBound)
                && TryParseEpc(_cached.RangeEnd, out _upperBound);
            if (_boundsValid && _lowerBound > _upperBound)
                (_lowerBound, _upperBound) = (_upperBound, _lowerBound);
            _loaded = true;
        }
    }

    /// <summary>
    /// Parses an EPC hex string into an unsigned <see cref="BigInteger"/>. Accepts an
    /// optional "0x" prefix and surrounding whitespace; rejects empty or non-hex input.
    /// </summary>
    public static bool TryParseEpc(string? epc, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (string.IsNullOrWhiteSpace(epc)) return false;

        var s = epc.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length == 0) return false;

        foreach (var c in s)
            if (!Uri.IsHexDigit(c)) return false;

        // Prefix "0" so the leading hex digit is never interpreted as a sign bit —
        // EPC values are unsigned identifiers, not two's-complement integers.
        value = BigInteger.Parse("0" + s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Uppercases an EPC and strips an optional "0x" prefix / surrounding whitespace.</summary>
    public static string NormalizeEpc(string epc)
    {
        var s = epc.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return s.ToUpperInvariant();
    }
}

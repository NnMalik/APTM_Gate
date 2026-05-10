namespace APTM.Gate.Infrastructure.Entities;

/// <summary>
/// Single-row table (Id always = 1) holding the active UHF reader connection settings.
/// When a row exists it overrides the Reader:* values from appsettings; if no row exists,
/// the worker falls back to appsettings. Settable at runtime via PUT /gate/reader/settings —
/// the next reconnect picks up the new values, no service restart required.
/// </summary>
public class ReaderConfigEntity
{
    public int Id { get; set; } = 1;

    /// <summary>Reader IP / hostname (e.g. "192.168.0.250").</summary>
    public string Host { get; set; } = default!;

    /// <summary>Reader TCP port (e.g. 27011).</summary>
    public int Port { get; set; }

    /// <summary>Default RF power applied at connect (0–30 dBm).</summary>
    public int DefaultPower { get; set; }

    /// <summary>Hardware EPC filter bit length applied at connect (0 = disabled).</summary>
    public int EpcFilterBits { get; set; }

    /// <summary>Delay between reconnect attempts when the link drops (ms).</summary>
    public int ReconnectDelayMs { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Token / device code that wrote this row — audit trail.</summary>
    public string UpdatedBy { get; set; } = default!;
}

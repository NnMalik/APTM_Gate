namespace APTM.Gate.Core.Models;

/// <summary>Snapshot of the active EPC range filter + where it came from ("db" or "default").</summary>
public sealed class EpcFilterInfo
{
    /// <summary>When true, only reads whose EPC is within [RangeStart, RangeEnd] are ingested.</summary>
    public bool Enabled { get; set; }

    /// <summary>Inclusive lower bound of the accepted EPC range (hex). Null when never configured.</summary>
    public string? RangeStart { get; set; }

    /// <summary>Inclusive upper bound of the accepted EPC range (hex). Null when never configured.</summary>
    public string? RangeEnd { get; set; }

    /// <summary>"db" when an epc_filter row exists, "default" when the filter has never been configured.</summary>
    public string Source { get; set; } = "default";

    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// PUT body for /gate/epc-filter. When <see cref="Enabled"/> is true, <see cref="RangeStart"/>
/// and <see cref="RangeEnd"/> are required and must be valid hexadecimal EPC values with
/// start &lt;= end. When disabled, omitted bounds preserve their previously saved value.
/// </summary>
public sealed class UpdateEpcFilterRequest
{
    public bool Enabled { get; set; }
    public string? RangeStart { get; set; }
    public string? RangeEnd { get; set; }
}

public sealed class SetEpcFilterResult
{
    public bool Success { get; private set; }
    public EpcFilterInfo? Filter { get; private set; }
    public string? Error { get; private set; }

    public static SetEpcFilterResult Ok(EpcFilterInfo info) => new() { Success = true, Filter = info };
    public static SetEpcFilterResult Fail(string error) => new() { Success = false, Error = error };
}

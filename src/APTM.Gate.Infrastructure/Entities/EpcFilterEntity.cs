namespace APTM.Gate.Infrastructure.Entities;

/// <summary>
/// Single-row table (Id always = 1) holding the configurable EPC acceptance range.
/// When <see cref="Enabled"/> is true, the gate ingests a tag read only if its EPC
/// falls within [<see cref="RangeStart"/>, <see cref="RangeEnd"/>] (inclusive, compared
/// as unsigned hexadecimal values). When no row exists or Enabled is false, every read
/// is accepted. Settable at runtime from the field app via PUT /gate/epc-filter — the
/// change takes effect on the next ingest batch, no service restart required.
/// </summary>
public class EpcFilterEntity
{
    public int Id { get; set; } = 1;

    /// <summary>When true, reads outside [RangeStart, RangeEnd] are dropped at ingestion.</summary>
    public bool Enabled { get; set; }

    /// <summary>Inclusive lower bound of the accepted EPC range (hex). Null when never configured.</summary>
    public string? RangeStart { get; set; }

    /// <summary>Inclusive upper bound of the accepted EPC range (hex). Null when never configured.</summary>
    public string? RangeEnd { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Token / device code that wrote this row — audit trail.</summary>
    public string UpdatedBy { get; set; } = default!;
}

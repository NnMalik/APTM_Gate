using APTM.Gate.Core.Models;

namespace APTM.Gate.Core.Interfaces;

/// <summary>
/// Singleton, in-memory cache of the EPC range filter. Reads from the DB (epc_filter
/// table) on first access; treated as disabled (accept all) when no row exists.
/// Re-loaded after a successful write to /gate/epc-filter via <see cref="Invalidate"/>.
/// </summary>
public interface IEpcFilterProvider
{
    /// <summary>Active filter settings — never null (defaults to disabled).</summary>
    EpcFilterInfo Current { get; }

    /// <summary>
    /// True when the read should be ingested: either the filter is disabled, or the
    /// EPC falls inside the configured inclusive range. A read whose EPC cannot be
    /// parsed is rejected while the filter is enabled.
    /// </summary>
    bool IsAccepted(string tagEpc);

    /// <summary>Drops the cached value so the next access re-queries the DB.</summary>
    void Invalidate();
}

/// <summary>
/// Reads + writes the singleton <c>epc_filter</c> row. Used by <c>EpcFilterEndpoints</c>.
/// Implementations invalidate the <see cref="IEpcFilterProvider"/> cache after a
/// successful upsert.
/// </summary>
public interface IEpcFilterService
{
    Task<EpcFilterInfo> GetAsync(CancellationToken ct = default);
    Task<SetEpcFilterResult> SetAsync(UpdateEpcFilterRequest request, string updatedBy, CancellationToken ct = default);
}

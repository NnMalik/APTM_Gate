using APTM.Gate.Core.Models;

namespace APTM.Gate.Core.Interfaces;

/// <summary>
/// Singleton, in-memory cache of the reader connection settings. Reads from the DB
/// (reader_config table) on first access, falling back to <c>Reader:*</c> values
/// in appsettings when no row exists. Re-loaded after a successful write to
/// /gate/reader/settings via <see cref="Invalidate"/>.
/// </summary>
public interface IReaderConfigProvider
{
    /// <summary>Returns the active settings — never null (always falls back to config defaults).</summary>
    ReaderSettingsInfo Current { get; }

    /// <summary>Drops the cached value so the next <see cref="Current"/> read re-queries the DB.</summary>
    void Invalidate();
}

/// <summary>
/// Reads + writes the reader_config row. Used by <c>ReaderSettingsEndpoints</c>.
/// Implementations also invalidate the <see cref="IReaderConfigProvider"/> cache after a
/// successful upsert and (best-effort) signal the worker to reconnect.
/// </summary>
public interface IReaderConfigService
{
    Task<ReaderSettingsInfo> GetAsync(CancellationToken ct = default);
    Task<SetReaderSettingsResult> SetAsync(UpdateReaderSettingsRequest request, string updatedBy, CancellationToken ct = default);
}

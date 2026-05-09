using APTM.Gate.Core.Models;

namespace APTM.Gate.Core.Interfaces;

/// <summary>
/// Singleton, in-memory cache of the current <see cref="GateIdentityInfo"/> so endpoint filters
/// and worker lifecycle checks don't hit the DB on every request. Backing data is loaded lazily
/// on first access. Call <see cref="Invalidate"/> after a successful PUT /gate/identity write
/// so the next read picks up the new row.
/// </summary>
public interface IGateIdentityProvider
{
    /// <summary>
    /// Current identity, or null if the gate has not been provisioned. Triggers a DB load on
    /// first access; subsequent reads are served from the cache.
    /// </summary>
    GateIdentityInfo? Current { get; }

    /// <summary>Drops the cached value so the next <see cref="Current"/> read re-queries the DB.</summary>
    void Invalidate();
}

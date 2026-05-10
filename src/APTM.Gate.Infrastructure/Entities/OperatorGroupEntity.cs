namespace APTM.Gate.Infrastructure.Entities;

/// <summary>
/// A trainer-scoped subset of candidates within the active test, replicated from Main
/// via the config package. The gate uses this for two things:
///   1. Per-group display counters on the finish-gate screen (e.g. "Group A: 12 finished").
///   2. Audit when a finish-gate read is attributed to the wrong group (forensic).
///
/// Membership comes via the normalized <see cref="OperatorGroupCandidateEntity"/> table.
/// The denormalized <see cref="CandidateIds"/> array is kept in sync at config-import
/// time as a fast-path for batch-membership checks during finish processing.
///
/// See DESIGN_OPERATOR_GROUPS.md §3.2.
/// </summary>
public class OperatorGroupEntity
{
    public Guid GroupId { get; set; }
    public string Name { get; set; } = default!;

    /// <summary>
    /// Denormalized roster — same data as <see cref="OperatorGroupCandidateEntity"/>
    /// rows for this group, but stored as a PostgreSQL <c>uuid[]</c> for set-membership
    /// queries during finish-gate processing without a join.
    /// </summary>
    public Guid[] CandidateIds { get; set; } = Array.Empty<Guid>();
}

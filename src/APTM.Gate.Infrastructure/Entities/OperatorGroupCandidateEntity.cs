namespace APTM.Gate.Infrastructure.Entities;

/// <summary>
/// Many-to-many between <see cref="OperatorGroupEntity"/> and a candidate. Composite PK
/// on (GroupId, CandidateId) with a secondary index on CandidateId so the gate can
/// answer "which group does this candidate belong to?" during finish processing without
/// scanning every group.
/// </summary>
public class OperatorGroupCandidateEntity
{
    public Guid GroupId { get; set; }
    public Guid CandidateId { get; set; }
}

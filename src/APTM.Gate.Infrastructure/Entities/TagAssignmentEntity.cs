namespace APTM.Gate.Infrastructure.Entities;

public class TagAssignmentEntity
{
    public Guid Id { get; set; }
    public Guid CandidateId { get; set; }
    public string TagEPC { get; set; } = default!;

    public CandidateEntity Candidate { get; set; } = default!;
}

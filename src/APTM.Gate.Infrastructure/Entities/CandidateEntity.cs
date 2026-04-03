namespace APTM.Gate.Infrastructure.Entities;

public class CandidateEntity
{
    public Guid CandidateId { get; set; }
    public string ServiceNumber { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Gender { get; set; } = default!;
    public int CandidateTypeId { get; set; }
    public DateOnly DateOfBirth { get; set; }
    public int? JacketNumber { get; set; }
}

namespace APTM.Gate.Infrastructure.Entities;

public class ScoringStatusEntity
{
    public int ScoringStatusId { get; set; }
    public int ScoringTypeId { get; set; }
    public string StatusCode { get; set; } = default!;
    public string StatusLabel { get; set; } = default!;
    public int Sequence { get; set; }
    public bool IsPassingStatus { get; set; }

    public ScoringTypeEntity ScoringType { get; set; } = default!;
}

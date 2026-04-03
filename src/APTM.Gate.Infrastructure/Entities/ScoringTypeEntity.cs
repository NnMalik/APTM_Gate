namespace APTM.Gate.Infrastructure.Entities;

public class ScoringTypeEntity
{
    public int ScoringTypeId { get; set; }
    public string Name { get; set; } = default!;

    public ICollection<ScoringStatusEntity> ScoringStatuses { get; set; } = [];
}

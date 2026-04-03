namespace APTM.Gate.Infrastructure.Entities;

public class CheckpointConfig
{
    public int Id { get; set; }
    public string RouteName { get; set; } = default!;
    public int Sequence { get; set; }
    public string CheckpointName { get; set; } = default!;
}

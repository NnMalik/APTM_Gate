namespace APTM.Gate.Infrastructure.Entities;

public class AcceptedTokenEntity
{
    public Guid Id { get; set; }
    public string Token { get; set; } = default!;
    public string Label { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

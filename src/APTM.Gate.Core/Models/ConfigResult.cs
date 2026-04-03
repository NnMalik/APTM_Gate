namespace APTM.Gate.Core.Models;

public sealed class ConfigResult
{
    public bool Success { get; init; }
    public string? Status { get; init; }
    public string? GateRole { get; init; }
    public int CandidateCount { get; init; }
    public string? Error { get; init; }

    public static ConfigResult Ok(string gateRole, int candidateCount) =>
        new() { Success = true, Status = "configured", GateRole = gateRole, CandidateCount = candidateCount };

    public static ConfigResult Fail(string error) =>
        new() { Success = false, Error = error };
}

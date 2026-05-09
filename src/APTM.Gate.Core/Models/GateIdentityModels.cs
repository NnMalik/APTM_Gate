namespace APTM.Gate.Core.Models;

/// <summary>Snapshot of the current gate identity. Returned by GET /gate/identity.</summary>
public sealed class GateIdentityInfo
{
    public string Role { get; init; } = default!;
    public int? CheckpointSequence { get; init; }
    public string DeviceCode { get; init; } = default!;
    public DateTimeOffset SetAt { get; init; }
    public string SetBy { get; init; } = default!;
}

/// <summary>Body for PUT /gate/identity.</summary>
public sealed class SetGateIdentityRequest
{
    public string Role { get; init; } = default!;
    public int? CheckpointSequence { get; init; }
}

/// <summary>Result of <c>IGateIdentityService.SetAsync</c>.</summary>
public sealed class SetIdentityResult
{
    public bool Success { get; init; }
    public bool Conflict { get; init; }
    public bool RestartRequired { get; init; }
    public GateIdentityInfo? Identity { get; init; }
    public string? Error { get; init; }

    public static SetIdentityResult Ok(GateIdentityInfo identity, bool restartRequired) =>
        new() { Success = true, Identity = identity, RestartRequired = restartRequired };

    public static SetIdentityResult Mismatch(GateIdentityInfo current, string error) =>
        new() { Success = false, Conflict = true, Identity = current, Error = error };

    public static SetIdentityResult Fail(string error) =>
        new() { Success = false, Error = error };
}

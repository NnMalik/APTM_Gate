namespace APTM.Gate.Core.Models;

public sealed class DisplayData
{
    public string GateRole { get; set; } = default!;
    public bool ReaderConnected { get; set; }
    public bool IsProcessingActive { get; set; }
    public int? ActiveEventId { get; set; }
    public string? ActiveEventName { get; set; }
    public string TestInstanceName { get; set; } = default!;
    public string ScheduledDate { get; set; } = default!;
    public int TotalCandidates { get; set; }
    public int TotalGroups { get; set; }

    /// <summary>
    /// "SPRINT" (one group at a time — current single-heat layout) or "PARALLEL"
    /// (several groups live at once — per-group rows with their own start time + timer).
    /// Chosen per active event; the display swaps layout on this value.
    /// </summary>
    public string DisplayMode { get; set; } = "SPRINT";

    /// <summary>Most recent live heat — kept for SPRINT mode and back-compat.</summary>
    public ActiveHeatData? ActiveHeat { get; set; }

    /// <summary>
    /// All live (non-cancelled) heats of the active event, newest first. PARALLEL mode
    /// renders one row per entry; SPRINT mode ignores this and uses <see cref="ActiveHeat"/>.
    /// Empty when no heat is running (idle).
    /// </summary>
    public List<ActiveHeatData> ActiveHeats { get; set; } = [];

    public List<FinishReadData> FinishReads { get; set; } = [];
    public List<StartReadData> StartReads { get; set; } = [];
    public AttendanceData? Attendance { get; set; }
}

public sealed class StartReadData
{
    public Guid CandidateId { get; set; }
    public string Name { get; set; } = default!;
    public int? JacketNumber { get; set; }
    public string? TagEPC { get; set; }
    public DateTimeOffset ReadTime { get; set; }
}

public sealed class ActiveHeatData
{
    public Guid HeatId { get; set; }
    public int HeatNumber { get; set; }
    /// <summary>The operator group this heat belongs to (null for ad-hoc heats).</summary>
    public Guid? GroupId { get; set; }
    /// <summary>Operator-group name when known, else "Group {HeatNumber}". Drives the per-group row label.</summary>
    public string GroupLabel { get; set; } = default!;

    /// <summary>
    /// Short, uppercased group code derived from <see cref="GroupLabel"/> (e.g. "Group Alpha" → "ALP").
    /// The per-group display renders "H{HeatNumber} · {Abbrev}" so heats from different HHTs — which
    /// each number from 1 — stay distinguishable. Falls back to "G{HeatNumber}" when no group is known.
    /// </summary>
    public string Abbrev { get; set; } = default!;

    /// <summary>Code of the HHT that started this heat (e.g. "HHT-02"); null on legacy rows. Lets the
    /// field app show which device ran each heat alongside the operator group.</summary>
    public string? SourceDeviceCode { get; set; }
    public bool HasStartTime { get; set; }
    public DateTimeOffset? GunStartTime { get; set; }
    public DateTimeOffset? OriginalGunStartTime { get; set; }
    public List<HeatCandidateData> Candidates { get; set; } = [];

    /// <summary>Total candidates expected to finish (= Candidates.Count).</summary>
    public int ExpectedCount { get; set; }

    /// <summary>How many of the expected roster have finished so far.</summary>
    public int FinishedCount { get; set; }

    /// <summary>Timestamp when the last candidate finished (or force-close wall time). Null = heat in progress.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>"auto" | "force_close" | null. Helps the display label the freeze appropriately.</summary>
    public string? ClosureReason { get; set; }

    /// <summary>
    /// Authoritative total heat time (seconds) from the finish gate, when known. The start display
    /// freezes on this so both LEDs show an identical value; null falls back to (CompletedAt − gun).
    /// </summary>
    public double? CompletedDurationSeconds { get; set; }
}

public sealed class HeatCandidateData
{
    public Guid CandidateId { get; set; }
    public string Name { get; set; } = default!;
    public int? JacketNumber { get; set; }
}

public sealed class FinishReadData
{
    public int Position { get; set; }
    public Guid CandidateId { get; set; }
    public string Name { get; set; } = default!;
    public int? JacketNumber { get; set; }
    public string? TagEPC { get; set; }
    public DateTimeOffset ReadTime { get; set; }
    public decimal? ElapsedSeconds { get; set; }
    public int? HeatNumber { get; set; }
}

public sealed class AttendanceData
{
    public int TotalPresent { get; set; }
    public int TotalAbsent { get; set; }
    public int TotalNotScanned { get; set; }
}

namespace APTM.Gate.Core.Models;

public sealed class DisplayData
{
    public string GateRole { get; set; } = default!;
    public int? ActiveEventId { get; set; }
    public string? ActiveEventName { get; set; }
    public string TestInstanceName { get; set; } = default!;
    public string ScheduledDate { get; set; } = default!;
    public int TotalCandidates { get; set; }
    public ActiveHeatData? ActiveHeat { get; set; }
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
    public int HeatNumber { get; set; }
    public bool HasStartTime { get; set; }
    public DateTimeOffset? GunStartTime { get; set; }
    public List<HeatCandidateData> Candidates { get; set; } = [];
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

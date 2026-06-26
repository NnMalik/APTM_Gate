using System.Text.Json.Serialization;

namespace APTM.Gate.Core.Models;

public class ConfigPackageDto
{
    public Guid TestInstanceId { get; set; }
    public string TestInstanceName { get; set; } = default!;
    public DateOnly ScheduledDate { get; set; }
    public int TestTypeId { get; set; }
    public string TestTypeName { get; set; } = default!;
    public Guid BatchId { get; set; }
    public int DataSnapshotVersion { get; set; }
    public DateTime GeneratedAt { get; set; }
    public DateTime ServerReferenceTime { get; set; }

    public List<ConfigCandidateDto> Candidates { get; set; } = [];
    public List<ConfigEventDto> Events { get; set; } = [];
    public List<ConfigGateDto> Gates { get; set; } = [];
    public List<ConfigCheckpointRouteDto> CheckpointRoutes { get; set; } = [];
    public List<ConfigScoringTypeDto> ScoringTypes { get; set; } = [];
    public List<GateWifiCredentialDto> GateWifiCredentials { get; set; } = [];

    /// <summary>
    /// Site-defined custom column schema (Unit / Rank / Wing / etc.). Forwarded from
    /// Main; gate doesn't process it directly today but stashes the JSON so the next
    /// HHT pulling from the gate (Phase 3+) gets the schema. Empty when the site has
    /// no custom columns or when running against a Main version that pre-dates this
    /// field — old gates ignore the field gracefully.
    /// </summary>
    public List<ConfigCustomColumnDto> CustomColumns { get; set; } = [];

    /// <summary>
    /// Operator groups defined on Main for this test. The gate persists these into
    /// its own <c>operator_group</c> tables so the finish-gate display can show
    /// per-group counters and so other devices polling <c>GET /gate/operator-groups</c>
    /// see authoritative state. Empty when running in legacy "no groups" mode.
    /// </summary>
    public List<ConfigOperatorGroupDto> OperatorGroups { get; set; } = [];

    /// <summary>
    /// Group → device assignments shipped from Main. Stored on the gate purely for
    /// surfacing via <c>GET /gate/operator-groups</c> so other HHTs can detect overlap
    /// when picking their selection (decision #3).
    /// </summary>
    public List<ConfigOperatorGroupAssignmentDto> OperatorGroupAssignments { get; set; } = [];
}

public class ConfigCandidateDto
{
    public Guid CandidateId { get; set; }
    public string ServiceNumber { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Gender { get; set; } = default!;
    public int CandidateTypeId { get; set; }
    public DateOnly DateOfBirth { get; set; }
    public string? TagEPC { get; set; }
    public int? JacketNumber { get; set; }

    /// <summary>
    /// Custom attributes (column name → value). Forwarded as-is to HHT via the gate.
    /// The gate doesn't query this today; it's pure pass-through. <c>JsonExtensionData</c>
    /// would be cleaner but a typed dictionary is simpler and keeps the gate's
    /// <c>GateConfigService</c> (which doesn't persist this) trivially correct.
    /// </summary>
    public Dictionary<string, string?> CustomData { get; set; } = new();
}

/// <summary>Schema row for a custom candidate column. Pure pass-through on the gate.</summary>
public class ConfigCustomColumnDto
{
    public int ColumnId { get; set; }
    public string Name { get; set; } = default!;
    public string DisplayLabel { get; set; } = default!;
    public string DataType { get; set; } = default!;
    public bool IsFilterable { get; set; }
}

public class ConfigOperatorGroupDto
{
    public Guid GroupId { get; set; }
    public string Name { get; set; } = default!;
    public List<Guid> CandidateIds { get; set; } = [];
}

public class ConfigOperatorGroupAssignmentDto
{
    public Guid GroupId { get; set; }
    public Guid DeviceId { get; set; }
    public string DeviceCode { get; set; } = default!;
}

public class ConfigEventDto
{
    public int TestTypeEventId { get; set; }
    public int EventId { get; set; }
    public string EventName { get; set; } = default!;
    public string EventType { get; set; } = default!;
    public int Sequence { get; set; }
    public bool IsRequired { get; set; }
    public int ScoringTypeId { get; set; }
    public string ScoringTypeName { get; set; } = default!;
    /// <summary>"SPRINT" | "PARALLEL", resolved by Main. Null on older Main versions.</summary>
    public string? DisplayMode { get; set; }
    public List<ConfigScoringStatusDto> ScoringStatuses { get; set; } = [];
}

public class ConfigGateDto
{
    public Guid DeviceId { get; set; }
    public string DeviceCode { get; set; } = default!;
    public string GateType { get; set; } = default!;
    public int? CheckpointSequence { get; set; }
    public int? EventId { get; set; }
    [JsonPropertyName("wifiSSID")]
    public string? WifiSSID { get; set; }
    public string? WifiPassword { get; set; }
    public string? NucIpAddress { get; set; }
}

public class ConfigCheckpointRouteDto
{
    public int RouteId { get; set; }
    public string RouteName { get; set; } = default!;
    public List<ConfigCheckpointItemDto> Items { get; set; } = [];
}

public class ConfigCheckpointItemDto
{
    public int Sequence { get; set; }
    public string Name { get; set; } = default!;
}

public class ConfigScoringTypeDto
{
    public int ScoringTypeId { get; set; }
    public string Name { get; set; } = default!;
    public List<ConfigScoringStatusDto> Statuses { get; set; } = [];
}

public class ConfigScoringStatusDto
{
    public int ScoringStatusId { get; set; }
    public string StatusCode { get; set; } = default!;
    public string StatusLabel { get; set; } = default!;
    public int Sequence { get; set; }
    public bool IsPassingStatus { get; set; }
}

public class GateWifiCredentialDto
{
    public Guid DeviceId { get; set; }
    public string DeviceCode { get; set; } = default!;
    [JsonPropertyName("ssid")]
    public string SSID { get; set; } = default!;
    public string Password { get; set; } = default!;
    public string NucIpAddress { get; set; } = default!;
}

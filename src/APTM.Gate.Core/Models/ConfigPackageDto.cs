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

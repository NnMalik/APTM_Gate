namespace APTM.Gate.Core.Models;

/// <summary>
/// NUC host health snapshot for the field app's gate-control screen. Every metric is nullable and
/// best-effort — a metric the host can't provide (e.g. /proc on Windows, or no thermal sensor)
/// comes back null rather than failing the whole call.
/// </summary>
public sealed class SystemHealthDto
{
    public string? HostName { get; set; }
    /// <summary>OS uptime (seconds) since last boot.</summary>
    public double? UptimeSeconds { get; set; }
    /// <summary>Gate service-process uptime (seconds) since it last (re)started.</summary>
    public double? ServiceUptimeSeconds { get; set; }

    public double? LoadAvg1 { get; set; }
    public double? LoadAvg5 { get; set; }
    public double? LoadAvg15 { get; set; }
    public int? CpuCount { get; set; }
    /// <summary>CPU package temperature in °C, when a thermal sensor is exposed.</summary>
    public double? CpuTempC { get; set; }

    public long? MemTotalBytes { get; set; }
    public long? MemAvailableBytes { get; set; }
    public long? DiskTotalBytes { get; set; }
    public long? DiskFreeBytes { get; set; }

    public DateTimeOffset ServerTime { get; set; }
}

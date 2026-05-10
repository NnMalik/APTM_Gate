namespace APTM.Gate.Core.Models;

/// <summary>Snapshot of the active reader settings + where they came from ("db" or "config").</summary>
public sealed class ReaderSettingsInfo
{
    public string Host { get; set; } = default!;
    public int Port { get; set; }
    public int DefaultPower { get; set; }
    public int EpcFilterBits { get; set; }
    public int ReconnectDelayMs { get; set; }
    /// <summary>"db" when an override row exists, "config" when falling back to appsettings.</summary>
    public string Source { get; set; } = "config";
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// PUT body for /gate/reader/settings. Host + Port are required; the rest are optional —
/// when omitted the existing DB value (or appsettings fallback) is preserved.
/// </summary>
public sealed class UpdateReaderSettingsRequest
{
    public string Host { get; set; } = default!;
    public int Port { get; set; }
    public int? DefaultPower { get; set; }
    public int? EpcFilterBits { get; set; }
    public int? ReconnectDelayMs { get; set; }
}

public sealed class SetReaderSettingsResult
{
    public bool Success { get; private set; }
    public ReaderSettingsInfo? Settings { get; private set; }
    public string? Error { get; private set; }

    public static SetReaderSettingsResult Ok(ReaderSettingsInfo info) => new() { Success = true, Settings = info };
    public static SetReaderSettingsResult Fail(string error) => new() { Success = false, Error = error };
}

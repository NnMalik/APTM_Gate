namespace APTM.Gate.Core.Interfaces;

public interface IReaderStatusProvider
{
    bool IsConnected { get; }
    DateTimeOffset? LastReadAt { get; }
    string? ReaderModel { get; }
    string? FirmwareVersion { get; }
    string? ReaderId { get; }
    int AntennaCount { get; }  // 4 or 8, auto-detected from reader type

    Task<bool> SetPowerAsync(byte power, CancellationToken ct = default);
    Task<bool> ResetReaderAsync(CancellationToken ct = default);
    Task<string> GetReaderModeAsync(CancellationToken ct = default);
    Task<string> CheckAntennaHealthAsync(byte port, CancellationToken ct = default);

    // Ported from old UHFReaderService
    Task<(string Version, byte Type, byte Power)?> GetReaderInfoAsync(CancellationToken ct = default);
    Task<bool> SetModeAsync(byte mode, CancellationToken ct = default);
    Task<byte[]?> GetAntennaPowersAsync(CancellationToken ct = default);
    Task<bool> ControlBuzzerAsync(byte activeDuration, byte silentDuration, byte times, CancellationToken ct = default);
    Task<bool> SetEpcFilterAsync(byte bits, CancellationToken ct = default);
    Task<bool> DisableFilterAsync(CancellationToken ct = default);
    Task<bool> SetDuplicateFilterTimeAsync(byte value, CancellationToken ct = default);
    Task<byte?> GetDuplicateFilterTimeAsync(CancellationToken ct = default);
    Task<(int ConnectedCount, byte AntennaBitmask)?> GetAntennaConfigAsync(CancellationToken ct = default);

    // Connection control
    Task<bool> DisconnectReaderAsync(CancellationToken ct = default);
    Task<bool> ReconnectReaderAsync(CancellationToken ct = default);
}

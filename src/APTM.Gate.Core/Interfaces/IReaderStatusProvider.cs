namespace APTM.Gate.Core.Interfaces;

public interface IReaderStatusProvider
{
    bool IsConnected { get; }
    DateTimeOffset? LastReadAt { get; }
    string? ReaderModel { get; }
    string? FirmwareVersion { get; }
    string? ReaderId { get; }

    Task<bool> SetPowerAsync(byte power, CancellationToken ct = default);
    Task<bool> ResetReaderAsync(CancellationToken ct = default);
    Task<string> GetReaderModeAsync(CancellationToken ct = default);
    Task<string> CheckAntennaHealthAsync(byte port, CancellationToken ct = default);
}

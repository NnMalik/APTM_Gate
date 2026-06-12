namespace APTM.Gate.Core.Models;

public sealed class DiagnosticsResponse
{
    public ReaderDiagnostics Reader { get; set; } = new();
    public List<AntennaDiagnostics> Antennas { get; set; } = [];
    public BufferDiagnostics Buffer { get; set; } = new();
    public DatabaseDiagnostics Database { get; set; } = new();
}

public sealed class ReaderDiagnostics
{
    public bool Connected { get; set; }
    public bool ModeVerified { get; set; }
    public string? Model { get; set; }
    public string? FirmwareVersion { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}

public sealed class AntennaDiagnostics
{
    public int Port { get; set; }
    public bool Connected { get; set; }
    public DateTimeOffset? LastReadAt { get; set; }
    public long ReadCount { get; set; }
}

public sealed class BufferDiagnostics
{
    public long PendingCount { get; set; }
    public long ProcessedCount { get; set; }
    public long UnresolvedCount { get; set; }
    public long DuplicateCount { get; set; }

    /// <summary>
    /// Reads parsed off the wire but not yet inserted into raw_tag_buffer (in-memory
    /// channel between the TCP read loop and the DB consumer). Normally 0–200; a
    /// sustained high value means PostgreSQL is down/slow and inserts are retrying.
    /// </summary>
    public int IngestQueueDepth { get; set; }
}

public sealed class DatabaseDiagnostics
{
    public int ConnectionPoolUsed { get; set; }
    public int ConnectionPoolMax { get; set; }
    public decimal DiskUsageMb { get; set; }
}

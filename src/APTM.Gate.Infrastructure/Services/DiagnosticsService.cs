using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Infrastructure.Services;

public sealed class DiagnosticsService : IDiagnosticsService
{
    private readonly GateDbContext _db;
    private readonly IReaderStatusProvider _readerStatus;

    public DiagnosticsService(GateDbContext db, IReaderStatusProvider readerStatus)
    {
        _db = db;
        _readerStatus = readerStatus;
    }

    public async Task<DiagnosticsResponse> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        var pendingCount = await _db.RawTagBuffers.CountAsync(r => r.Status == "PENDING", ct);
        var processedCount = await _db.RawTagBuffers.CountAsync(r => r.Status == "PROCESSED", ct);
        var unresolvedCount = await _db.RawTagBuffers.CountAsync(r => r.Status == "UNRESOLVED", ct);
        var duplicateCount = await _db.RawTagBuffers.CountAsync(r => r.Status == "DUPLICATE", ct);

        return new DiagnosticsResponse
        {
            Reader = new ReaderDiagnostics
            {
                Connected = _readerStatus.IsConnected,
                Model = _readerStatus.ReaderModel,
                FirmwareVersion = _readerStatus.FirmwareVersion,
                LastSeenAt = _readerStatus.LastReadAt
            },
            Buffer = new BufferDiagnostics
            {
                PendingCount = pendingCount,
                ProcessedCount = processedCount,
                UnresolvedCount = unresolvedCount,
                DuplicateCount = duplicateCount
            },
            Database = new DatabaseDiagnostics()
        };
    }
}

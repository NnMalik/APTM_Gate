using APTM.Gate.Core.Models;

namespace APTM.Gate.Core.Interfaces;

public interface IDiagnosticsService
{
    Task<DiagnosticsResponse> GetDiagnosticsAsync(CancellationToken ct = default);
}

namespace APTM.Gate.Core.Interfaces;

public interface IBufferProcessingService
{
    Task<int> ProcessBatchAsync(int batchSize = 100, CancellationToken ct = default);
}

using APTM.Gate.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace APTM.Gate.Workers;

/// <summary>
/// Signal-driven background worker that polls raw_tag_buffer for PENDING rows.
/// Lifecycle:
///   1. IDLE: blocks until gate status becomes active.
///   2. ACTIVE: polls every 500ms calling IBufferProcessingService.ProcessBatchAsync(100).
///   3. After 2 consecutive empty cycles, returns to IDLE.
/// </summary>
public sealed class BufferProcessorWorker : BackgroundService
{
    private const int PollIntervalMs = 500;
    private const int BatchSize = 100;
    private const int ConsecutiveEmptyThreshold = 2;

    private readonly ILogger<BufferProcessorWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGateStatusProvider _statusProvider;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    public BufferProcessorWorker(
        ILogger<BufferProcessorWorker> logger,
        IServiceScopeFactory scopeFactory,
        IGateStatusProvider statusProvider)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _statusProvider = statusProvider;

        // Wake up when gate status changes
        _statusProvider.StatusChanged += () =>
        {
            if (_statusProvider.IsActive && _wakeSignal.CurrentCount == 0)
                _wakeSignal.Release();
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BufferProcessorWorker started — waiting for gate to become active");

        while (!stoppingToken.IsCancellationRequested)
        {
            // IDLE: wait for signal
            if (!_statusProvider.IsActive)
            {
                _logger.LogDebug("BufferProcessorWorker IDLE — waiting for active signal");
                try
                {
                    await _wakeSignal.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                _logger.LogInformation("BufferProcessorWorker ACTIVE — beginning poll loop");
            }

            // ACTIVE: poll loop
            int consecutiveEmpty = 0;

            while (_statusProvider.IsActive && !stoppingToken.IsCancellationRequested)
            {
                try
                {
                    int processed;
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var service = scope.ServiceProvider.GetRequiredService<IBufferProcessingService>();
                        processed = await service.ProcessBatchAsync(BatchSize, stoppingToken);
                    }

                    if (processed > 0)
                    {
                        consecutiveEmpty = 0;
                        _logger.LogDebug("Processed {Count} buffer rows", processed);
                    }
                    else
                    {
                        consecutiveEmpty++;
                        if (consecutiveEmpty >= ConsecutiveEmptyThreshold)
                        {
                            _logger.LogDebug("BufferProcessorWorker: {Threshold} consecutive empty cycles — returning to IDLE", ConsecutiveEmptyThreshold);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "BufferProcessorWorker processing error");
                }

                await Task.Delay(PollIntervalMs, stoppingToken);
            }
        }

        _logger.LogInformation("BufferProcessorWorker stopped");
    }
}

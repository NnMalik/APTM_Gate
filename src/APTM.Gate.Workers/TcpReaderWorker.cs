using System.Net.Sockets;
using System.Threading.Channels;
using APTM.Gate.Core.Enums;
using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace APTM.Gate.Workers;

/// <summary>
/// Maintains a persistent TCP connection to a UHF RFID reader (Series V2.20).
/// On connect: initialises the reader (serial, power, antennas, real-time mode).
/// While connected: reads tag frames, parses them, and inserts into raw_tag_buffer.
/// Exposes reader control commands via IReaderStatusProvider (semaphore-protected).
/// </summary>
public sealed class TcpReaderWorker : BackgroundService, IReaderStatusProvider
{
    private readonly ILogger<TcpReaderWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGateIdentityProvider _identityProvider;
    private readonly IReaderConfigProvider _readerConfigProvider;

    // Per-connection snapshot of settings — captured at the start of each ConnectAndReadAsync
    // iteration so a write to /gate/reader/settings is picked up on the next reconnect.
    private string _readerHost = "127.0.0.1";
    private int _readerPort = 27011;
    private int _reconnectDelayMs = 5000;
    private byte _defaultPower = 20;
    private byte _epcFilterBits = 0;

    // TCP state
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    // Status
    private volatile bool _isConnected;
    private volatile bool _manualDisconnect;
    private volatile bool _modeVerified;
    private DateTimeOffset? _lastReadAt;
    private DateTimeOffset? _lastFrameAt;
    private string? _readerId;
    private string? _readerModel;
    private string? _firmwareVersion;
    private int _antennaCount = 4; // Default 4-port, auto-detected from reader info

    // Decouples the TCP read loop from PostgreSQL: the loop only enqueues, a consumer
    // task batches inserts with retry. Bounded so a long DB outage can't exhaust memory;
    // DropOldest keeps the most recent reads, which matter most for a live race.
    private readonly Channel<RawTagFrame> _ingestChannel = Channel.CreateBounded<RawTagFrame>(
        new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    // If the line is silent this long (no tags, no heartbeats), actively probe the
    // reader — a reader that lost power without a TCP FIN looks identical to an empty
    // read field until we ask it something.
    private static readonly TimeSpan SilenceProbeAfter = TimeSpan.FromSeconds(30);

    public bool IsConnected => _isConnected;
    public bool ModeVerified => _modeVerified;
    public int IngestQueueDepth => _ingestChannel.Reader.Count;
    public DateTimeOffset? LastReadAt => _lastReadAt;
    public DateTimeOffset? LastFrameAt => _lastFrameAt;
    public string? ReaderModel => _readerModel;
    public string? FirmwareVersion => _firmwareVersion;
    public string? ReaderId => _readerId;
    public int AntennaCount => _antennaCount;

    public TcpReaderWorker(
        ILogger<TcpReaderWorker> logger,
        IServiceScopeFactory scopeFactory,
        IGateIdentityProvider identityProvider,
        IReaderConfigProvider readerConfigProvider)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _identityProvider = identityProvider;
        _readerConfigProvider = readerConfigProvider;
    }

    /// <summary>
    /// Re-reads the active reader settings from the provider. Called at the start of each
    /// reconnect iteration so a write to /gate/reader/settings takes effect within ~5s
    /// without a service restart.
    /// </summary>
    private void RefreshSettingsSnapshot()
    {
        var s = _readerConfigProvider.Current;
        _readerHost = s.Host;
        _readerPort = s.Port;
        _reconnectDelayMs = s.ReconnectDelayMs;
        _defaultPower = (byte)Math.Clamp(s.DefaultPower, 0, 30);
        _epcFilterBits = (byte)Math.Clamp(s.EpcFilterBits, 0, 128);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Role gate: Start gates have no reader; un-provisioned gates have nothing to do.
        // We deliberately do NOT poll for identity changes here — provisioning is one-time
        // and a service restart is the documented path for role flips.
        var identity = _identityProvider.Current;
        if (identity is null)
        {
            _logger.LogInformation("TcpReaderWorker exiting — gate is not provisioned. PUT /gate/identity then restart the service.");
            return;
        }
        if (!Enum.TryParse<GateRole>(identity.Role, out var role) || role == GateRole.Start)
        {
            _logger.LogInformation("TcpReaderWorker exiting — role is {Role}; no reader expected.", identity.Role);
            return;
        }

        // Initial snapshot for the startup log line.
        RefreshSettingsSnapshot();
        _logger.LogInformation("TcpReaderWorker starting — role {Role}, target {Host}:{Port}", role, _readerHost, _readerPort);

        // DB consumer runs for the worker's whole lifetime, across reconnects.
        var ingestTask = Task.Run(() => IngestLoopAsync(stoppingToken), CancellationToken.None);

        while (!stoppingToken.IsCancellationRequested)
        {
            // If manually disconnected, wait until reconnect is requested
            if (_manualDisconnect)
            {
                await Task.Delay(500, stoppingToken);
                continue;
            }

            try
            {
                await ConnectAndReadAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                _modeVerified = false;
                if (!_manualDisconnect)
                {
                    _logger.LogWarning(ex, "Reader connection lost. Reconnecting in {Delay}ms", _reconnectDelayMs);
                    await Task.Delay(_reconnectDelayMs, stoppingToken);
                }
            }
        }

        _isConnected = false;
        _modeVerified = false;

        // Best-effort flush of reads still queued for the DB before the host exits.
        _ingestChannel.Writer.TryComplete();
        try
        {
            await ingestTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Ingest queue flush timed out — {Count} reads not persisted", _ingestChannel.Reader.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ingest queue flush failed");
        }

        _logger.LogInformation("TcpReaderWorker stopped");
    }

    private async Task ConnectAndReadAsync(CancellationToken ct)
    {
        // Clean up any previous connection
        DisposeConnection();

        // Re-read settings every reconnect — picks up changes from /gate/reader/settings.
        RefreshSettingsSnapshot();

        _modeVerified = false;
        _client = new TcpClient { NoDelay = true, ReceiveBufferSize = 65536 };

        // TCP keepalive — second line of defence (after the silence probe) against
        // half-open connections when the reader loses power without a FIN.
        _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
        _client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
        _client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);

        _logger.LogInformation("Connecting to reader at {Host}:{Port}...", _readerHost, _readerPort);
        await _client.ConnectAsync(_readerHost, _readerPort, ct);
        _stream = _client.GetStream();
        _stream.WriteTimeout = 5000;
        _isConnected = true;
        _logger.LogInformation("Connected to reader at {Host}:{Port}", _readerHost, _readerPort);

        // Drain any data the reader is already streaming (e.g., from a previous
        // connection that left it in Real-Time mode)
        await DrainStreamAsync(ct);
        await Task.Delay(200, ct);

        // Initialize the reader
        await InitializeReaderAsync(ct);

        // Enter real-time read loop
        await ReadLoopAsync(ct);

        _isConnected = false;
    }

    /// <summary>
    /// Sends initialization commands: serial, info, power, antennas, filter, real-time mode.
    /// </summary>
    private async Task InitializeReaderAsync(CancellationToken ct)
    {
        // 1. Get serial number (0x4C)
        try
        {
            var serialCmd = UhfFrameParser.BuildCommandFrame(0x00, 0x4C, ReadOnlySpan<byte>.Empty);
            var serialResp = await SendCommandInternalAsync(serialCmd, ct);
            if (serialResp is { Length: >= 8 } && serialResp[3] == 0x00)
            {
                _readerId = BitConverter.ToString(serialResp, 4, 4).Replace("-", "");
                _logger.LogInformation("Reader serial: {Serial}", _readerId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read reader serial number");
        }

        // 2. Get reader info (0x21) — also detects antenna count from reader type
        try
        {
            var infoCmd = UhfFrameParser.BuildCommandFrame(0x00, 0x21, ReadOnlySpan<byte>.Empty);
            var infoResp = await SendCommandInternalAsync(infoCmd, ct);
            if (infoResp is { Length: >= 8 } && infoResp[3] == 0x00)
            {
                _firmwareVersion = $"{infoResp[4]}.{infoResp[5]}";
                byte readerType = infoResp[6];
                _readerModel = $"Type-{readerType}";

                // Auto-detect antenna count from reader type byte
                // Types with 8+ ports use higher type codes; common: Type-4 = 4-port, Type-8 = 8-port
                // Also check Ant byte (index 8 if present) for current antenna config bitmask
                if (infoResp.Length >= 12)
                {
                    byte antByte = infoResp[8 + 3]; // Ant is at offset 8 from Data[] start = index 11
                    // If any bits 4-7 are set, it's an 8-port reader
                    if ((antByte & 0xF0) != 0)
                        _antennaCount = 8;
                    else
                        _antennaCount = 4;
                }

                // Also use reader type as a hint (Type >= 8 likely means 8-port)
                if (readerType >= 8) _antennaCount = 8;

                _logger.LogInformation("Reader info — Version: {Version}, Model: {Model}, Power: {Power}, AntennaCount: {AntCount}",
                    _firmwareVersion, _readerModel, infoResp[7], _antennaCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read reader info");
        }

        // 3. Set power (0x2F)
        try
        {
            var powerCmd = UhfFrameParser.BuildCommandFrame(0x00, 0x2F, [_defaultPower]);
            var powerResp = await SendCommandInternalAsync(powerCmd, ct);
            if (powerResp is { Length: >= 4 } && powerResp[3] == 0x00)
                _logger.LogInformation("Reader power set to {Power}", _defaultPower);
            else
                _logger.LogWarning("Failed to set reader power");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not set reader power");
        }

        // 4. Enable antennas — Format 1 (4-port) or Format 2 (8-port)
        try
        {
            byte[] antData;
            if (_antennaCount >= 8)
            {
                // Format 2: [SetOnce=0x01 (don't persist), AntCfg1=0x00 (reserved for 8-port), AntCfg2=0xFF (all 8)]
                antData = [0x01, 0x00, 0xFF];
                _logger.LogInformation("Enabling all 8 antennas (Format 2)...");
            }
            else
            {
                // Format 1: [Ant=0x0F] bits 0-3 = antennas 1-4
                antData = [0x0F];
                _logger.LogInformation("Enabling antennas 1-4 (Format 1)...");
            }

            var antCmd = UhfFrameParser.BuildCommandFrame(0x00, 0x3F, antData);
            var antResp = await SendCommandInternalAsync(antCmd, ct);
            if (antResp is { Length: >= 4 } && antResp[3] == 0x00)
                _logger.LogInformation("Antennas enabled ({Count}-port)", _antennaCount);
            else
                _logger.LogWarning("Failed to enable antennas");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enable antennas");
        }

        // 5. Set hardware EPC filter if configured (0x2C)
        if (_epcFilterBits > 0)
        {
            try
            {
                byte[] filterData = [0x01, 0x00, 0x20, _epcFilterBits, 0x00, 0x00, 0x00];
                var filterCmd = UhfFrameParser.BuildCommandFrame(0x00, 0x2C, filterData);
                var filterResp = await SendCommandInternalAsync(filterCmd, ct);
                if (filterResp is { Length: >= 4 } && filterResp[3] == 0x00)
                    _logger.LogInformation("Hardware EPC filter set to {Bits} bits", _epcFilterBits);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not set hardware EPC filter");
            }
        }

        // 6. Switch to Real-Time Inventory mode (0x76 with 0x01) and VERIFY via
        // read-back (0x77). A reader that is connected but not in real-time mode
        // reads nothing, so a failed switch must surface as a reconnect — not a
        // warning followed by a deaf read loop.
        const int maxModeAttempts = 3;
        for (int attempt = 1; attempt <= maxModeAttempts && !_modeVerified; attempt++)
        {
            try
            {
                var modeCmd = UhfFrameParser.BuildCommandFrame(0x00, 0x76, [0x01]);
                var modeResp = await SendCommandInternalAsync(modeCmd, ct);
                if (modeResp is { Length: >= 4 } && modeResp[3] != 0x00)
                    _logger.LogWarning("Set Real-Time mode returned status 0x{Status:X2} (attempt {Attempt})",
                        modeResp[3], attempt);

                // Read back regardless of the set response — the reader may already
                // be streaming and the set ack can be missed, while the read-back
                // still confirms the mode.
                var checkCmd = UhfFrameParser.BuildCommandFrame(0x00, 0x77, ReadOnlySpan<byte>.Empty);
                var checkResp = await SendCommandInternalAsync(checkCmd, ct);
                if (checkResp is { Length: >= 5 } && checkResp[3] == 0x00 && checkResp[4] == 0x01)
                {
                    _modeVerified = true;
                    _logger.LogInformation("Real-Time Inventory mode verified (attempt {Attempt})", attempt);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Real-Time mode switch attempt {Attempt} failed", attempt);
            }

            if (!_modeVerified && attempt < maxModeAttempts)
                await Task.Delay(300, ct);
        }

        if (!_modeVerified)
            throw new InvalidOperationException(
                "Could not verify Real-Time Inventory mode after initialization — forcing reconnect.");
    }

    /// <summary>
    /// Continuously reads from the TCP stream, extracts frames, and enqueues tag
    /// reads for ingestion. Uses the reader's [Len] byte framing protocol. Any valid
    /// frame (tag or heartbeat) counts as liveness; if the line goes silent the
    /// reader is actively probed so a half-open connection turns into a reconnect
    /// instead of an indefinitely "connected" but deaf gate.
    /// </summary>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var receiveBuffer = new byte[4096];
        var accumulated = new List<byte>(8192);
        var lastActivity = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested && _client?.Connected == true)
        {
            int bytesRead = 0;
            bool dataAvailable = false;

            // Acquire semaphore for stream read
            await _semaphore.WaitAsync(ct);
            try
            {
                if (_stream is null || !_client.Connected) break;

                dataAvailable = _stream.DataAvailable;
                if (dataAvailable)
                    bytesRead = await _stream.ReadAsync(receiveBuffer.AsMemory(), ct);
            }
            finally
            {
                _semaphore.Release();
            }

            if (!dataAvailable)
            {
                if (DateTimeOffset.UtcNow - lastActivity > SilenceProbeAfter)
                {
                    _logger.LogInformation("No data from reader for {Seconds}s — probing connection",
                        (int)SilenceProbeAfter.TotalSeconds);
                    var mode = await GetReaderModeAsync(ct);
                    if (mode.StartsWith("Error", StringComparison.Ordinal))
                        throw new IOException("Reader is unresponsive to probe — forcing reconnect.");
                    lastActivity = DateTimeOffset.UtcNow;
                    continue;
                }

                await Task.Delay(10, ct);
                continue;
            }

            if (bytesRead == 0)
            {
                _logger.LogWarning("Reader stream ended (0 bytes read)");
                break;
            }

            accumulated.AddRange(receiveBuffer.AsSpan(0, bytesRead).ToArray());

            // Extract every complete frame: tag reports AND heartbeats. Heartbeats
            // carry no data but prove the reader is alive.
            var (frames, consumed) = UhfFrameParser.ExtractFrames(
                accumulated.ToArray().AsSpan());

            if (consumed > 0)
            {
                accumulated.RemoveRange(0, consumed);
            }

            if (frames.Count > 0)
            {
                lastActivity = DateTimeOffset.UtcNow;
                _lastFrameAt = lastActivity;

                var tags = CollectTagFrames(frames);
                if (tags.Count > 0)
                {
                    _lastReadAt = lastActivity;
                    EnqueueTags(tags);
                }
            }
        }
    }

    /// <summary>Filters successful 0xEE tag reports out of a mixed frame list and parses them.</summary>
    private static List<RawTagFrame> CollectTagFrames(List<byte[]> frames)
    {
        var tags = new List<RawTagFrame>(frames.Count);
        foreach (var frame in frames)
        {
            if (frame[2] != UhfFrameParser.TagReportCmd || frame[3] != UhfFrameParser.SuccessStatus)
                continue;
            var tag = UhfFrameParser.ParseTagFrame(frame);
            if (tag is not null)
                tags.Add(tag);
        }
        return tags;
    }

    /// <summary>
    /// Sends a command and reads its response. Must only be called when the semaphore
    /// is NOT held by the caller (init sequence calls it directly; public API methods
    /// go through SendCommandAsync). Pending stream data is consumed — and any tag
    /// frames in it salvaged — before sending, and the response is matched by command
    /// byte, so a reader streaming real-time tags cannot pollute the response.
    /// </summary>
    private async Task<byte[]?> SendCommandInternalAsync(byte[] command, CancellationToken ct)
    {
        if (_stream is null) return null;

        // Consume pending real-time data (salvaging tag reads) so stale frames
        // from before the command can't be matched as its response.
        await DrainStreamAsync(ct);

        // Send command
        _logger.LogDebug("Sending command: {Cmd}", BitConverter.ToString(command));
        await _stream.WriteAsync(command, ct);
        await _stream.FlushAsync(ct);

        return await ReadResponseFrameAsync(command[2], ct);
    }

    /// <summary>
    /// Reads frames until the response to <paramref name="expectedCmd"/> arrives or
    /// the timeout elapses. Interleaved 0xEE frames (tags/heartbeats) are NOT treated
    /// as the response — tag reads among them are enqueued for ingestion, not lost.
    /// Handles fragmented responses naturally via the accumulation buffer.
    /// </summary>
    private async Task<byte[]?> ReadResponseFrameAsync(byte expectedCmd, CancellationToken ct, int timeoutMs = 2500)
    {
        if (_stream is null) return null;

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        var accumulated = new List<byte>(2048);
        var buffer = new byte[2048];
        var salvagedTags = new List<RawTagFrame>();

        try
        {
            while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (!_stream.DataAvailable)
                {
                    await Task.Delay(20, ct);
                    continue;
                }

                int bytesRead = await _stream.ReadAsync(buffer.AsMemory(), ct);
                if (bytesRead == 0) return null;

                accumulated.AddRange(buffer.AsSpan(0, bytesRead).ToArray());
                var (frames, consumed) = UhfFrameParser.ExtractFrames(accumulated.ToArray().AsSpan());
                if (consumed > 0)
                    accumulated.RemoveRange(0, consumed);

                foreach (var frame in frames)
                {
                    _lastFrameAt = DateTimeOffset.UtcNow;

                    if (frame[2] == UhfFrameParser.TagReportCmd)
                    {
                        if (frame[3] == UhfFrameParser.SuccessStatus)
                        {
                            var tag = UhfFrameParser.ParseTagFrame(frame);
                            if (tag is not null)
                                salvagedTags.Add(tag);
                        }
                        continue; // tag report or heartbeat — not our response
                    }

                    if (frame[2] == expectedCmd)
                    {
                        _logger.LogDebug("Response: {Data}", BitConverter.ToString(frame));
                        return frame;
                    }

                    _logger.LogDebug("Skipping unexpected frame (cmd 0x{Cmd:X2}) while waiting for 0x{Expected:X2}",
                        frame[2], expectedCmd);
                }
            }

            _logger.LogWarning("Timed out waiting for response to command 0x{Cmd:X2}", expectedCmd);
            return null;
        }
        finally
        {
            if (salvagedTags.Count > 0)
            {
                _lastReadAt = DateTimeOffset.UtcNow;
                EnqueueTags(salvagedTags);
            }
        }
    }

    /// <summary>
    /// Sends a command with semaphore protection. Used by public API methods.
    /// </summary>
    private async Task<byte[]?> SendCommandAsync(byte[] command, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            return await SendCommandInternalAsync(command, ct);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Consumes all pending data from the stream so stale frames can't pollute the
    /// next command's response. In Real-Time mode "pending data" is almost always
    /// live tag reads, so complete tag frames are salvaged into the ingest queue
    /// instead of being discarded.
    /// </summary>
    private async Task DrainStreamAsync(CancellationToken ct)
    {
        if (_stream is null) return;

        var pending = new List<byte>(4096);
        var buffer = new byte[4096];

        while (_stream.DataAvailable)
        {
            int read = await _stream.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) break;
            pending.AddRange(buffer.AsSpan(0, read).ToArray());
            if (pending.Count > 100 * 1024) break; // Safety limit
        }

        if (pending.Count == 0) return;

        var (frames, _) = UhfFrameParser.ExtractFrames(pending.ToArray().AsSpan());
        if (frames.Count > 0)
            _lastFrameAt = DateTimeOffset.UtcNow;

        var tags = CollectTagFrames(frames);
        if (tags.Count > 0)
        {
            _lastReadAt = DateTimeOffset.UtcNow;
            EnqueueTags(tags);
        }

        _logger.LogDebug("Consumed {Bytes} pending bytes before command — salvaged {Tags} tag reads",
            pending.Count, tags.Count);
    }

    /// <summary>Hands parsed tag frames to the DB consumer. Never blocks the read path.</summary>
    private void EnqueueTags(IReadOnlyList<RawTagFrame> tags)
    {
        foreach (var tag in tags)
            _ingestChannel.Writer.TryWrite(tag); // bounded DropOldest — TryWrite always succeeds
    }

    /// <summary>
    /// Consumes queued tag reads and batch-inserts them into raw_tag_buffer. A failed
    /// insert is retried with backoff instead of dropping the batch — a brief DB
    /// outage mid-race must not lose reads. Runs until the channel is completed
    /// (worker shutdown), then drains what it can.
    /// </summary>
    private async Task IngestLoopAsync(CancellationToken ct)
    {
        var reader = _ingestChannel.Reader;

        while (await reader.WaitToReadAsync(CancellationToken.None))
        {
            var batch = new List<RawTagFrame>(200);
            while (batch.Count < 200 && reader.TryRead(out var frame))
                batch.Add(frame);
            if (batch.Count == 0) continue;

            if (reader.Count > 40_000)
                _logger.LogWarning("Ingest queue depth {Depth} — DB is falling behind; oldest reads drop at 50k", reader.Count);

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var tagBuffer = scope.ServiceProvider.GetRequiredService<ITagBufferService>();
                    await tagBuffer.InsertRawTagsAsync(batch, CancellationToken.None);
                    _logger.LogDebug("Ingested {Count} tag frames", batch.Count);
                    break;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested && attempt >= 2)
                    {
                        _logger.LogError(ex, "Dropping {Count} reads — shutting down and DB insert keeps failing", batch.Count);
                        break;
                    }

                    var delayMs = Math.Min(30_000, 500 * (1 << Math.Min(attempt, 6)));
                    _logger.LogError(ex, "Insert of {Count} reads failed (attempt {Attempt}) — retrying in {Delay}ms (queued: {Depth})",
                        batch.Count, attempt, delayMs, reader.Count);

                    try { await Task.Delay(delayMs, ct); }
                    catch (OperationCanceledException) { /* shutting down — one final attempt */ }
                }
            }
        }
    }

    private void DisposeConnection()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public override void Dispose()
    {
        DisposeConnection();
        _semaphore.Dispose();
        base.Dispose();
    }

    // --- IReaderStatusProvider command implementations ---

    public async Task<bool> SetPowerAsync(byte power, CancellationToken ct = default)
    {
        if (power > 30) power = 30;
        var cmd = UhfFrameParser.BuildCommandFrame(0x00, 0x2F, [power]);
        var resp = await SendCommandAsync(cmd, ct);
        return resp is { Length: >= 4 } && resp[3] == 0x00;
    }

    public async Task<bool> ResetReaderAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            if (_stream is null) return false;
            await DrainStreamAsync(ct);

            var cmd = UhfFrameParser.BuildCommandFrame(0x00, 0x24, ReadOnlySpan<byte>.Empty);
            await _stream.WriteAsync(cmd, ct);
            await _stream.FlushAsync(ct);

            _logger.LogInformation("Reset command sent. Reader is rebooting...");
            _isConnected = false;
            _modeVerified = false;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during reader reset");
            _isConnected = false;
            _modeVerified = false;
            return false;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<string> GetReaderModeAsync(CancellationToken ct = default)
    {
        var cmd = UhfFrameParser.BuildCommandFrame(0x00, 0x77, ReadOnlySpan<byte>.Empty);
        var resp = await SendCommandAsync(cmd, ct);
        if (resp is { Length: >= 5 } && resp[3] == 0x00)
        {
            return resp[4] switch
            {
                0x00 => "Answering Mode",
                0x01 => "Real Time Inventory Mode",
                0x02 => "Real Time Inventory Mode with Trigger",
                _ => $"Unknown (0x{resp[4]:X2})"
            };
        }
        return "Error: Could not retrieve mode";
    }

    public async Task<string> CheckAntennaHealthAsync(byte port, CancellationToken ct = default)
    {
        // Test frequency 920.125 MHz = 0x000E0A3D
        byte[] data = [0x00, 0x0E, 0x0A, 0x3D, port];
        var cmd = UhfFrameParser.BuildCommandFrame(0x00, 0x91, data);
        var resp = await SendCommandAsync(cmd, ct);

        if (resp is { Length: >= 5 } && resp[3] == 0x00)
        {
            byte returnLoss = resp[4];
            if (returnLoss < 3) return $"Antenna {port + 1}: Not Connected (RL: {returnLoss}dB)";
            if (returnLoss < 10) return $"Antenna {port + 1}: Poor Connection (RL: {returnLoss}dB)";
            return $"Antenna {port + 1}: Healthy (RL: {returnLoss}dB)";
        }
        return $"Antenna {port + 1}: Check Failed";
    }

    // --- Ported from old UHFReaderService ---

    public async Task<(string Version, byte Type, byte Power)?> GetReaderInfoAsync(CancellationToken ct = default)
    {
        var cmd = UhfFrameParser.BuildCommandFrame(0x00, 0x21, ReadOnlySpan<byte>.Empty);
        var resp = await SendCommandAsync(cmd, ct);
        if (resp is { Length: >= 8 } && resp[3] == 0x00)
        {
            var version = $"{resp[4]}.{resp[5]}";
            return (version, resp[6], resp[7]);
        }
        return null;
    }

    public async Task<bool> SetModeAsync(byte mode, CancellationToken ct = default)
    {
        var cmd = UhfFrameParser.BuildCommandFrame(0x00, 0x76, [mode]);
        var resp = await SendCommandAsync(cmd, ct);
        var ok = resp is { Length: >= 4 } && resp[3] == 0x00;
        if (ok)
            _modeVerified = mode == 0x01; // manual switch away from real-time un-verifies
        return ok;
    }

    public async Task<byte[]?> GetAntennaPowersAsync(CancellationToken ct = default)
    {
        var cmd = UhfFrameParser.BuildCommandFrame(0x00, 0x94, ReadOnlySpan<byte>.Empty);
        var resp = await SendCommandAsync(cmd, ct);
        if (resp is { Length: >= 5 } && resp[3] == 0x00)
        {
            // Power data starts at index 4, each byte is power for one antenna
            var powerCount = resp[0] - 4; // Len - (Adr + reCmd + Status + CRC(2)) + 1
            if (powerCount <= 0) return null;
            var powers = new byte[powerCount];
            Array.Copy(resp, 4, powers, 0, powerCount);
            return powers;
        }
        return null;
    }

    public async Task<bool> ControlBuzzerAsync(byte activeDuration, byte silentDuration, byte times, CancellationToken ct = default)
    {
        byte[] data = [activeDuration, silentDuration, times];
        var cmd = UhfFrameParser.BuildCommandFrame(0x00, 0x33, data);
        var resp = await SendCommandAsync(cmd, ct);
        return resp is { Length: >= 4 } && resp[3] == 0x00;
    }

    public async Task<bool> SetEpcFilterAsync(byte bits, CancellationToken ct = default)
    {
        // Sel=0x01 (EPC bank), Address=0x0020 (32 bits = start of EPC), MaskLen=bits, Trun=0x00
        byte[] data = [0x01, 0x00, 0x20, bits, 0x00];
        var cmd = UhfFrameParser.BuildCommandFrame(0x00, 0x2C, data);
        var resp = await SendCommandAsync(cmd, ct);
        return resp is { Length: >= 4 } && resp[3] == 0x00;
    }

    public async Task<bool> DisableFilterAsync(CancellationToken ct = default)
    {
        // Sel=0x01, Address=0x0020, MaskLen=0x00, Trun=0x00
        byte[] data = [0x01, 0x00, 0x20, 0x00, 0x00];
        var cmd = UhfFrameParser.BuildCommandFrame(0x00, 0x2C, data);
        var resp = await SendCommandAsync(cmd, ct);
        return resp is { Length: >= 4 } && resp[3] == 0x00;
    }

    public async Task<bool> SetDuplicateFilterTimeAsync(byte value, CancellationToken ct = default)
    {
        var cmd = UhfFrameParser.BuildCommandFrame(0x00, 0x3D, [value]);
        var resp = await SendCommandAsync(cmd, ct);
        return resp is { Length: >= 4 } && resp[3] == 0x00;
    }

    public async Task<byte?> GetDuplicateFilterTimeAsync(CancellationToken ct = default)
    {
        var cmd = UhfFrameParser.BuildCommandFrame(0x00, 0x3E, ReadOnlySpan<byte>.Empty);
        var resp = await SendCommandAsync(cmd, ct);
        if (resp is { Length: >= 5 } && resp[3] == 0x00)
            return resp[4];
        return null;
    }

    public async Task<(int ConnectedCount, byte AntennaBitmask)?> GetAntennaConfigAsync(CancellationToken ct = default)
    {
        var cmd = UhfFrameParser.BuildCommandFrame(0x00, 0x21, ReadOnlySpan<byte>.Empty);
        var resp = await SendCommandAsync(cmd, ct);
        if (resp is { Length: >= 12 } && resp[3] == 0x00)
        {
            byte antByte = resp[11];
            int count = 0;
            for (int i = 0; i < 4; i++)
                if ((antByte & (1 << i)) != 0) count++;
            return (count, antByte);
        }
        return null;
    }

    // Connection control

    public Task<bool> DisconnectReaderAsync(CancellationToken ct = default)
    {
        _manualDisconnect = true;
        _isConnected = false;
        _modeVerified = false;
        DisposeConnection();
        _logger.LogInformation("Reader manually disconnected");
        return Task.FromResult(true);
    }

    public async Task<bool> ReconnectReaderAsync(CancellationToken ct = default)
    {
        // If already connected, return immediately
        if (_isConnected)
        {
            _logger.LogInformation("Reader already connected");
            return true;
        }

        _manualDisconnect = false;
        _logger.LogInformation("Reader reconnect requested — waiting for connection...");

        // Wait up to 10 seconds for connection to establish
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(500, ct);
            if (_isConnected)
            {
                _logger.LogInformation("Reader connected successfully");
                return true;
            }
        }

        _logger.LogWarning("Reader did not connect within 10 seconds");
        return false;
    }
}

using System.Net.Sockets;
using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using Microsoft.Extensions.Configuration;
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
    private readonly string _readerHost;
    private readonly int _readerPort;
    private readonly int _reconnectDelayMs;
    private readonly byte _defaultPower;
    private readonly byte _epcFilterBits;

    // TCP state
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    // Status
    private volatile bool _isConnected;
    private volatile bool _manualDisconnect;
    private DateTimeOffset? _lastReadAt;
    private string? _readerId;
    private string? _readerModel;
    private string? _firmwareVersion;

    public bool IsConnected => _isConnected;
    public DateTimeOffset? LastReadAt => _lastReadAt;
    public string? ReaderModel => _readerModel;
    public string? FirmwareVersion => _firmwareVersion;
    public string? ReaderId => _readerId;

    public TcpReaderWorker(
        ILogger<TcpReaderWorker> logger,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _readerHost = configuration["Reader:Host"] ?? "127.0.0.1";
        _readerPort = int.TryParse(configuration["Reader:Port"], out var port) ? port : 27011;
        _reconnectDelayMs = int.TryParse(configuration["Reader:ReconnectDelayMs"], out var delay) ? delay : 5000;
        _defaultPower = byte.TryParse(configuration["Reader:DefaultPower"], out var power) ? power : (byte)20;
        _epcFilterBits = byte.TryParse(configuration["Reader:EpcFilterBits"], out var filter) ? filter : (byte)0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TcpReaderWorker starting — target {Host}:{Port}", _readerHost, _readerPort);

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
                if (!_manualDisconnect)
                {
                    _logger.LogWarning(ex, "Reader connection lost. Reconnecting in {Delay}ms", _reconnectDelayMs);
                    await Task.Delay(_reconnectDelayMs, stoppingToken);
                }
            }
        }

        _isConnected = false;
        _logger.LogInformation("TcpReaderWorker stopped");
    }

    private async Task ConnectAndReadAsync(CancellationToken ct)
    {
        // Clean up any previous connection
        DisposeConnection();

        _client = new TcpClient { NoDelay = true, ReceiveBufferSize = 65536 };

        _logger.LogInformation("Connecting to reader at {Host}:{Port}...", _readerHost, _readerPort);
        await _client.ConnectAsync(_readerHost, _readerPort, ct);
        _stream = _client.GetStream();
        _stream.ReadTimeout = 5000;
        _stream.WriteTimeout = 5000;
        _isConnected = true;
        _logger.LogInformation("Connected to reader at {Host}:{Port}", _readerHost, _readerPort);

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

        // 2. Get reader info (0x21)
        try
        {
            var infoCmd = UhfFrameParser.BuildCommandFrame(0x00, 0x21, ReadOnlySpan<byte>.Empty);
            var infoResp = await SendCommandInternalAsync(infoCmd, ct);
            if (infoResp is { Length: >= 8 } && infoResp[3] == 0x00)
            {
                _firmwareVersion = $"{infoResp[4]}.{infoResp[5]}";
                _readerModel = $"Type-{infoResp[6]}";
                _logger.LogInformation("Reader info — Version: {Version}, Type: {Type}, Power: {Power}",
                    _firmwareVersion, _readerModel, infoResp[7]);
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

        // 4. Enable antennas 1-4 (0x3F with 0x0F)
        try
        {
            var antCmd = UhfFrameParser.BuildCommandFrame(0x00, 0x3F, [0x0F]);
            var antResp = await SendCommandInternalAsync(antCmd, ct);
            if (antResp is { Length: >= 4 } && antResp[3] == 0x00)
                _logger.LogInformation("Antennas 1-4 enabled");
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

        // 6. Switch to Real-Time Inventory mode (0x76 with 0x01)
        try
        {
            var modeCmd = UhfFrameParser.BuildCommandFrame(0x00, 0x76, [0x01]);
            var modeResp = await SendCommandInternalAsync(modeCmd, ct);
            if (modeResp is { Length: >= 4 } && modeResp[3] == 0x00)
                _logger.LogInformation("Switched to Real-Time Inventory mode");
            else
                _logger.LogWarning("Failed to switch to Real-Time mode — status: 0x{Status:X2}",
                    modeResp?.Length >= 4 ? modeResp[3] : 0xFF);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not switch to Real-Time mode");
        }
    }

    /// <summary>
    /// Continuously reads from the TCP stream, extracts tag frames, and ingests them.
    /// Uses the reader's [Len] byte framing protocol.
    /// </summary>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var receiveBuffer = new byte[4096];
        var accumulated = new List<byte>(8192);

        while (!ct.IsCancellationRequested && _client?.Connected == true)
        {
            int bytesRead;

            // Acquire semaphore for stream read
            await _semaphore.WaitAsync(ct);
            try
            {
                if (_stream is null || !_client.Connected) break;

                // Non-blocking check — if no data available, release and wait briefly
                if (!_stream.DataAvailable)
                {
                    _semaphore.Release();
                    await Task.Delay(10, ct);
                    continue;
                }

                bytesRead = await _stream.ReadAsync(receiveBuffer.AsMemory(), ct);
            }
            catch
            {
                _semaphore.Release();
                throw;
            }

            _semaphore.Release();

            if (bytesRead == 0)
            {
                _logger.LogWarning("Reader stream ended (0 bytes read)");
                break;
            }

            accumulated.AddRange(receiveBuffer.AsSpan(0, bytesRead).ToArray());

            // Extract complete tag frames from accumulated buffer
            var (frames, consumed) = UhfFrameParser.ExtractTagFrames(
                accumulated.ToArray().AsSpan());

            if (consumed > 0)
            {
                accumulated.RemoveRange(0, consumed);
            }

            if (frames.Count > 0)
            {
                _lastReadAt = DateTimeOffset.UtcNow;
                await IngestFramesAsync(frames, ct);
            }
        }
    }

    /// <summary>
    /// Sends a command and reads the response. Drains pending data first.
    /// Must only be called when semaphore is NOT held by caller (init sequence),
    /// or refactored for internal use.
    /// </summary>
    private async Task<byte[]?> SendCommandInternalAsync(byte[] command, CancellationToken ct)
    {
        if (_stream is null) return null;

        // Drain any pending real-time data
        await DrainStreamAsync(ct);

        // Send command
        _logger.LogDebug("Sending command: {Cmd}", BitConverter.ToString(command));
        await _stream.WriteAsync(command, ct);
        await _stream.FlushAsync(ct);

        // Wait for reader to process
        await Task.Delay(150, ct);

        // Read response
        var buffer = new byte[1024];
        int bytesRead = await _stream.ReadAsync(buffer.AsMemory(), ct);

        if (bytesRead < 5)
        {
            _logger.LogWarning("Response too short: {Bytes} bytes", bytesRead);
            return null;
        }

        // Frame alignment: first byte is Len
        int frameLen = buffer[0];
        int expectedTotal = frameLen + 1;

        if (bytesRead < expectedTotal)
        {
            _logger.LogWarning("Fragmented response: received {Actual} of {Expected}", bytesRead, expectedTotal);
            return null;
        }

        var response = new byte[expectedTotal];
        Array.Copy(buffer, 0, response, 0, expectedTotal);

        // Verify CRC
        if (!UhfFrameParser.VerifyCRC(response))
        {
            _logger.LogWarning("CRC verification failed for response: {Data}", BitConverter.ToString(response));
            return null;
        }

        _logger.LogDebug("Response: {Data}", BitConverter.ToString(response));
        return response;
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
    /// Drains all pending data from the stream to prevent pollution of command responses.
    /// </summary>
    private async Task DrainStreamAsync(CancellationToken ct)
    {
        if (_stream is null) return;

        int drained = 0;
        var discard = new byte[4096];

        while (_stream.DataAvailable)
        {
            int read = await _stream.ReadAsync(discard.AsMemory(), ct);
            drained += read;
            if (drained > 100 * 1024) break; // Safety limit
        }

        if (drained > 0)
            _logger.LogDebug("Drained {Bytes} bytes before command", drained);
    }

    private async Task IngestFramesAsync(IReadOnlyList<RawTagFrame> frames, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tagBuffer = scope.ServiceProvider.GetRequiredService<ITagBufferService>();
            await tagBuffer.InsertRawTagsAsync(frames, ct);
            _logger.LogDebug("Ingested {Count} tag frames", frames.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ingest {Count} tag frames", frames.Count);
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
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during reader reset");
            _isConnected = false;
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
        return resp is { Length: >= 4 } && resp[3] == 0x00;
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
        DisposeConnection();
        _logger.LogInformation("Reader manually disconnected");
        return Task.FromResult(true);
    }

    public Task<bool> ReconnectReaderAsync(CancellationToken ct = default)
    {
        _manualDisconnect = false;
        _logger.LogInformation("Reader reconnect requested — will connect on next cycle");
        return Task.FromResult(true);
    }
}

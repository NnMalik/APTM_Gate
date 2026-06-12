using System.Net.Sockets;

namespace ReaderTester;

public sealed record TagReading(string Epc, byte Antenna, int? Rssi, DateTime Time);

/// <summary>
/// TCP client for a UHF reader. One background read loop parses every frame coming
/// off the wire: 0xEE tag reports are raised as TagRead events, anything else is
/// treated as the response to the currently pending command. This means commands
/// can be sent while the reader streams real-time tags WITHOUT discarding reads.
/// </summary>
public sealed class ReaderClient : IDisposable
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readTask;

    private readonly SemaphoreSlim _cmdLock = new(1, 1);
    private TaskCompletionSource<byte[]>? _pendingResponse;
    private byte _pendingCmd;

    public bool IsConnected => _client?.Connected == true;

    public event Action<TagReading>? TagRead;
    public event Action<string>? Log;
    public event Action<string>? RawHex;          // every chunk received, hex-dumped
    public event Action<string>? Disconnected;    // reason

    public async Task ConnectAsync(string host, int port, int timeoutMs = 5000)
    {
        _client = new TcpClient { NoDelay = true, ReceiveBufferSize = 65536 };
        using var connectCts = new CancellationTokenSource(timeoutMs);
        await _client.ConnectAsync(host, port, connectCts.Token);
        _stream = _client.GetStream();
        _cts = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoopAsync(_cts.Token));
        Log?.Invoke($"Connected to {host}:{port}");
    }

    public void Disconnect(string reason = "user requested")
    {
        _cts?.Cancel();
        _pendingResponse?.TrySetCanceled();
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        Disconnected?.Invoke(reason);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var accumulated = new List<byte>(16384);
        string reason = "connection closed by reader";

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream!.ReadAsync(buffer, ct);
                if (n == 0) break; // remote closed

                RawHex?.Invoke(UhfProtocol.ToHex(buffer.AsSpan(0, n)));
                accumulated.AddRange(buffer.Take(n));

                var (frames, consumed) = UhfProtocol.ExtractFrames(accumulated.ToArray());
                if (consumed > 0)
                    accumulated.RemoveRange(0, consumed);

                foreach (var frame in frames)
                    RouteFrame(frame);
            }
        }
        catch (OperationCanceledException) { reason = "disconnected"; }
        catch (ObjectDisposedException) { reason = "disconnected"; }
        catch (Exception ex) { reason = $"read error: {ex.Message}"; }

        if (!ct.IsCancellationRequested)
        {
            _pendingResponse?.TrySetCanceled();
            _stream?.Dispose();
            _client?.Dispose();
            _stream = null;
            _client = null;
            Disconnected?.Invoke(reason);
        }
    }

    private void RouteFrame(byte[] frame)
    {
        byte reCmd = frame[2];
        byte status = frame[3];

        if (reCmd == UhfProtocol.TagReportCmd)
        {
            if (status == UhfProtocol.SuccessStatus)
            {
                var tag = UhfProtocol.ParseTagFrame(frame);
                if (tag is not null)
                    TagRead?.Invoke(new TagReading(tag.Value.Epc, tag.Value.Antenna, tag.Value.Rssi, DateTime.Now));
            }
            // status 0x28 = heartbeat — ignore silently
            return;
        }

        // Not a tag report → response to a pending command, or unsolicited
        var pending = _pendingResponse;
        if (pending is not null && reCmd == _pendingCmd)
        {
            pending.TrySetResult(frame);
        }
        else
        {
            Log?.Invoke($"Unsolicited frame (cmd 0x{reCmd:X2}, status 0x{status:X2}): {UhfProtocol.ToHex(frame)}");
        }
    }

    /// <summary>
    /// Sends a command and waits for its response frame (matched by command byte).
    /// Returns null on timeout. Tag frames arriving meanwhile are still processed.
    /// </summary>
    public async Task<byte[]?> SendCommandAsync(byte cmd, byte[] data, int timeoutMs = 2500)
    {
        if (_stream is null) { Log?.Invoke("Not connected."); return null; }

        await _cmdLock.WaitAsync();
        try
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingCmd = cmd;
            _pendingResponse = tcs;

            var frame = UhfProtocol.BuildCommand(0x00, cmd, data);
            Log?.Invoke($"TX: {UhfProtocol.ToHex(frame)}");
            await _stream.WriteAsync(frame);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completed != tcs.Task)
            {
                Log?.Invoke($"Timeout waiting for response to 0x{cmd:X2}");
                return null;
            }

            var resp = await tcs.Task;
            Log?.Invoke($"RX: {UhfProtocol.ToHex(resp)}");
            return resp;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Command 0x{cmd:X2} failed: {ex.Message}");
            return null;
        }
        finally
        {
            _pendingResponse = null;
            _cmdLock.Release();
        }
    }

    // --- High-level operations ---

    public async Task<bool> SetModeAsync(byte mode) // 0=answer, 1=real-time, 2=trigger
    {
        var resp = await SendCommandAsync(UhfProtocol.CmdSetWorkMode, [mode]);
        return resp is { Length: >= 4 } && resp[3] == 0x00;
    }

    public async Task<string> GetModeAsync()
    {
        var resp = await SendCommandAsync(UhfProtocol.CmdGetWorkMode, []);
        if (resp is { Length: >= 5 } && resp[3] == 0x00)
        {
            return resp[4] switch
            {
                0x00 => "Answering",
                0x01 => "Real-Time",
                0x02 => "Real-Time (Trigger)",
                _ => $"Unknown (0x{resp[4]:X2})"
            };
        }
        return "unknown";
    }

    public async Task<bool> SetPowerAsync(byte power)
    {
        var resp = await SendCommandAsync(UhfProtocol.CmdSetRfPower, [Math.Min(power, (byte)30)]);
        return resp is { Length: >= 4 } && resp[3] == 0x00;
    }

    public async Task<bool> BeepAsync(byte activeTime = 5, byte silentTime = 5, byte times = 3)
    {
        var resp = await SendCommandAsync(UhfProtocol.CmdBuzzer, [activeTime, silentTime, times]);
        return resp is { Length: >= 4 } && resp[3] == 0x00;
    }

    public async Task<string?> GetReaderInfoAsync()
    {
        var resp = await SendCommandAsync(UhfProtocol.CmdGetReaderInfo, []);
        if (resp is { Length: >= 8 } && resp[3] == 0x00)
            return $"Firmware {resp[4]}.{resp[5]}, Type 0x{resp[6]:X2}, Power {resp[7]} dBm";
        return null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _stream?.Dispose();
        _client?.Dispose();
        _cmdLock.Dispose();
    }
}

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using APTM.Gate.Core.Models;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Api.Endpoints;

/// <summary>
/// NUC host control + observability for the field app: a health snapshot, a recent-log tail, and a
/// "reload the display" command. All require auth. Reads are best-effort (Linux /proc + sysfs); a
/// metric the host can't provide comes back null rather than failing the call.
/// </summary>
public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gate")
            .RequireAuthorization()
            .WithTags("System");

        // ── Host health snapshot ────────────────────────────────────────────
        group.MapGet("/system/health", () =>
        {
            var health = new SystemHealthDto
            {
                HostName = SafeGet(() => Environment.MachineName),
                CpuCount = SafeGet(() => (int?)Environment.ProcessorCount),
                ServiceUptimeSeconds = SafeGet(() =>
                    (double?)(DateTimeOffset.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime())
                        .TotalSeconds),
                ServerTime = DateTimeOffset.UtcNow
            };

            // OS uptime — /proc/uptime first field is seconds since boot.
            SafeRun(() =>
            {
                var fields = File.ReadAllText("/proc/uptime").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 0 && double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var up))
                    health.UptimeSeconds = up;
            });

            // Load averages — /proc/loadavg: "0.00 0.01 0.05 1/123 4567".
            SafeRun(() =>
            {
                var parts = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    health.LoadAvg1 = ParseInv(parts[0]);
                    health.LoadAvg5 = ParseInv(parts[1]);
                    health.LoadAvg15 = ParseInv(parts[2]);
                }
            });

            // Memory — /proc/meminfo lines are "MemTotal:  16312345 kB".
            SafeRun(() =>
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:")) health.MemTotalBytes = MemKbToBytes(line);
                    else if (line.StartsWith("MemAvailable:")) health.MemAvailableBytes = MemKbToBytes(line);
                    if (health.MemTotalBytes is not null && health.MemAvailableBytes is not null) break;
                }
            });

            // CPU temperature — first thermal zone, millidegrees.
            SafeRun(() =>
            {
                const string zone = "/sys/class/thermal/thermal_zone0/temp";
                if (File.Exists(zone) && int.TryParse(File.ReadAllText(zone).Trim(), out var milli))
                    health.CpuTempC = milli / 1000.0;
            });

            // Disk — the volume the service binary lives on.
            SafeRun(() =>
            {
                var root = Path.GetPathRoot(AppContext.BaseDirectory);
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    health.DiskTotalBytes = drive.TotalSize;
                    health.DiskFreeBytes = drive.AvailableFreeSpace;
                }
            });

            return Results.Ok(health);
        })
        .WithName("GetSystemHealth")
        .WithSummary("NUC host health snapshot")
        .WithDescription("CPU temp/load, memory, disk, and OS/service uptime. Best-effort; unsupported metrics are null.");

        // ── Recent service logs ─────────────────────────────────────────────
        group.MapGet("/logs", async (int? lines, CancellationToken ct) =>
        {
            var n = Math.Clamp(lines ?? 200, 1, 1000);
            var (ok, output) = await RunCaptureAsync(
                "journalctl", $"-u aptm-gate -n {n} --no-pager -o cat", ct);

            if (!ok)
                return Results.Ok(new
                {
                    available = false,
                    message = "journalctl unavailable on this host (non-systemd / Windows).",
                    lines = Array.Empty<string>()
                });

            var split = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return Results.Ok(new { available = true, count = split.Length, lines = split });
        })
        .WithName("GetGateLogs")
        .WithSummary("Tail recent gate service logs")
        .WithDescription("Returns the last N (default 200, max 1000) journald lines for the aptm-gate unit.");

        // ── Download the full rolling log archive ────────────────────────────
        group.MapGet("/logs/download", () =>
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.log") : [];
            if (files.Length == 0)
                return Results.NotFound(new { message = "No log files on disk yet." });

            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in files)
                {
                    var entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
                    using var es = entry.Open();
                    // ReadWrite share so we don't clash with Serilog's active writer on today's file.
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.CopyTo(es);
                }
            }
            ms.Position = 0;
            var name = $"aptm-gate-logs-{Sanitize(Environment.MachineName)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
            return Results.File(ms, "application/zip", name);
        })
        .WithName("DownloadGateLogs")
        .WithSummary("Download all on-disk gate logs as a zip")
        .WithDescription("Zips the rolling daily log files (up to 90 days) for offline root-cause analysis.");

        // ── Reload the kiosk display ────────────────────────────────────────
        group.MapPost("/display/reload", async (GateDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            // Fires the display_command channel; SseNotificationService forwards it to every connected
            // display, where display.js does a hard location.reload(). Recovers a stuck/blank kiosk
            // page without rebooting the whole NUC.
            //
            // The payload MUST be parameterized: ExecuteSqlRaw runs the SQL through string.Format, so
            // an inline JSON literal's '{' braces throw a FormatException (HTTP 500). Interpolated
            // passes the payload as a bound parameter — same pattern as the config_updated NOTIFY.
            const string payload = "{\"action\":\"reload\"}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_notify('display_command', {payload})", ct);
            logger.LogInformation("Display reload requested via /gate/display/reload.");
            return Results.Ok(new { status = "reload_sent", message = "Reload signal sent to the gate display(s)." });
        })
        .WithName("ReloadDisplay")
        .WithSummary("Force the kiosk display to reload")
        .WithDescription("Signals connected displays to hard-reload their page — recovers a stuck screen without a reboot.");
    }

    /// <summary>Strip characters that aren't safe in a download filename.</summary>
    private static string Sanitize(string s) =>
        new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());

    private static double? ParseInv(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static long? MemKbToBytes(string metaLine)
    {
        // "MemTotal:       16312345 kB"
        var parts = metaLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var kb) ? kb * 1024L : null;
    }

    private static T? SafeGet<T>(Func<T?> get)
    {
        try { return get(); } catch { return default; }
    }

    private static void SafeRun(Action run)
    {
        try { run(); } catch { /* best-effort metric */ }
    }

    /// <summary>Runs a command, capturing stdout. Returns (exit==0, stdout). Never throws.</summary>
    private static async Task<(bool Ok, string Output)> RunCaptureAsync(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (proc is null) return (false, string.Empty);

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode == 0, stdout);
        }
        catch
        {
            return (false, string.Empty);
        }
    }
}

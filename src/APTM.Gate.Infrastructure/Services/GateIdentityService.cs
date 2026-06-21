using APTM.Gate.Core.Enums;
using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using APTM.Gate.Infrastructure.Entities;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APTM.Gate.Infrastructure.Services;

public sealed class GateIdentityService : IGateIdentityService
{
    private readonly GateDbContext _db;
    private readonly IGateIdentityProvider _identityProvider;
    private readonly string _deviceCode;
    private readonly string _connectionString;

    public GateIdentityService(GateDbContext db, IGateIdentityProvider identityProvider, IConfiguration configuration)
    {
        _db = db;
        _identityProvider = identityProvider;
        _deviceCode = configuration["Gate:DeviceCode"] ?? throw new InvalidOperationException("Gate:DeviceCode not configured");
        _connectionString = configuration.GetConnectionString("GateDb") ?? throw new InvalidOperationException("GateDb connection string not configured");
    }

    public async Task<GateIdentityInfo?> GetAsync(CancellationToken ct = default)
    {
        var row = await _db.GateIdentities.AsNoTracking().FirstOrDefaultAsync(ct);
        return row is null ? null : ToInfo(row);
    }

    public async Task<SetIdentityResult> SetAsync(SetGateIdentityRequest request, string setBy, bool force, CancellationToken ct = default)
    {
        // Validate role string against enum
        if (!Enum.TryParse<GateRole>(request.Role, ignoreCase: false, out var parsedRole))
            return SetIdentityResult.Fail($"Invalid role '{request.Role}'. Expected one of: Start, Checkpoint, Finish.");

        // Checkpoint requires a sequence; other roles must NOT have one
        if (parsedRole == GateRole.Checkpoint && request.CheckpointSequence is null or < 1)
            return SetIdentityResult.Fail("CheckpointSequence is required (>= 1) when role = Checkpoint.");
        if (parsedRole != GateRole.Checkpoint && request.CheckpointSequence is not null)
            return SetIdentityResult.Fail($"CheckpointSequence must be null when role = {parsedRole}.");

        var existing = await _db.GateIdentities.FirstOrDefaultAsync(ct);

        // Normalize the name: blank → null so empty strings don't masquerade as a value.
        var name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim();

        // Fully idempotent — nothing changed at all.
        if (existing is not null
            && existing.Role == request.Role
            && existing.CheckpointSequence == request.CheckpointSequence
            && existing.Name == name)
        {
            return SetIdentityResult.Ok(ToInfo(existing), restartRequired: false);
        }

        // Same ROLE — only the sequence and/or name differ. The role is what drives worker
        // registration (TcpReaderWorker checks the role once at startup; BufferProcessingService
        // re-reads the identity every batch and never depends on the sequence). So this is a
        // safe LIVE update: no force, no read purge, no restart. Covers both a pure rename and
        // correcting a checkpoint's sequence number — neither should be destructive or require
        // re-provisioning. Only an actual role change falls through to the conflict/force path.
        if (existing is not null && existing.Role == request.Role)
        {
            existing.CheckpointSequence = request.CheckpointSequence;
            existing.Name = name;
            existing.SetAt = DateTimeOffset.UtcNow;
            existing.SetBy = setBy;
            await _db.SaveChangesAsync(ct);
            // Invalidate the in-memory cache so the buffer processor picks up the new sequence
            // on its next batch without a service restart.
            _identityProvider.Invalidate();
            return SetIdentityResult.Ok(ToInfo(existing), restartRequired: false);
        }

        // Conflict: existing identity differs and caller didn't force
        if (existing is not null && !force)
        {
            return SetIdentityResult.Mismatch(
                ToInfo(existing),
                $"Identity already provisioned as {existing.Role}" +
                (existing.CheckpointSequence is null ? "" : $" (seq {existing.CheckpointSequence})") +
                $". Use ?force=true to override and purge race state.");
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Force-flip: purge race state so reads from the previous role don't bleed across semantics
            if (existing is not null && force)
            {
                await _db.Database.ExecuteSqlRawAsync(
                    "TRUNCATE TABLE raw_tag_buffer, processed_events RESTART IDENTITY CASCADE", ct);
            }

            // Capture previous values BEFORE mutation so the restart-required check is correct.
            var prevRole = existing?.Role;
            var prevSequence = existing?.CheckpointSequence;

            if (existing is null)
            {
                _db.GateIdentities.Add(new GateIdentity
                {
                    Id = 1,
                    Role = request.Role,
                    CheckpointSequence = request.CheckpointSequence,
                    Name = name,
                    DeviceCode = _deviceCode,
                    SetAt = DateTimeOffset.UtcNow,
                    SetBy = setBy
                });
            }
            else
            {
                existing.Role = request.Role;
                existing.CheckpointSequence = request.CheckpointSequence;
                existing.Name = name;
                existing.DeviceCode = _deviceCode;
                existing.SetAt = DateTimeOffset.UtcNow;
                existing.SetBy = setBy;
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Invalidate the in-memory cache so workers / filters see the new value on next access.
            _identityProvider.Invalidate();

            // Best-effort NOTIFY so any cached identity provider refreshes
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    role = request.Role,
                    checkpointSequence = request.CheckpointSequence
                });
                await using var cmd = new NpgsqlCommand($"NOTIFY identity_updated, '{payload}'", conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch { /* best-effort */ }

            // Restart required when:
            // - first-time provisioning (prevRole is null — workers need to register)
            // - role change (different worker set needs to be wired up at startup)
            // A sequence-only change applies live: BufferProcessingService re-reads the
            // identity from the (just invalidated) provider on every batch, and
            // TcpReaderWorker only checks the role at startup, never the sequence.
            var restartRequired = prevRole != request.Role;

            // Re-read fresh row for response
            var info = await GetAsync(ct);
            return SetIdentityResult.Ok(info!, restartRequired);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return SetIdentityResult.Fail($"Set identity failed: {ex.Message}");
        }
    }

    private static GateIdentityInfo ToInfo(GateIdentity row) => new()
    {
        Role = row.Role,
        CheckpointSequence = row.CheckpointSequence,
        Name = row.Name,
        DeviceCode = row.DeviceCode,
        SetAt = row.SetAt,
        SetBy = row.SetBy
    };
}

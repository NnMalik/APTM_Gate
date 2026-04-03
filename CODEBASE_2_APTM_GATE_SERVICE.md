# Codebase 2 — APTM Gate Service (New App)

> **New .NET 10 ASP.NET Core Web API for NUC/Micro PC (Ubuntu)**
> Handles UHF tag capture, processing, display serving, and local sync hub functionality.

---

## 1. Overview

The Gate Service runs on each gate's NUC (6 instances: 5 movable + 1 fixed finish). It captures UHF tags from the directly connected reader, processes them into resolved candidate events, serves real-time data to LED displays via SSE, and acts as a local sync hub for any device on the gate's Wi-Fi.

**Runtime:** .NET 10 ASP.NET Core (Minimal API style)
**Database:** PostgreSQL 16
**OS:** Ubuntu 22.04+ on NUC/Micro PC (i3/i5, 8GB RAM, 128GB SSD)
**Auth:** Device Token (reused from APTM Main)

---

## 2. Solution Structure

```
APTM.Gate/
├── APTM.Gate.sln
├── src/
│   ├── APTM.Gate.Api/                    ← Executable — endpoints, SSE, middleware
│   │   ├── Endpoints/                    ← Minimal API endpoint groups
│   │   │   ├── ConfigEndpoints.cs
│   │   │   ├── SyncEndpoints.cs
│   │   │   ├── DisplayEndpoints.cs
│   │   │   ├── DiagnosticsEndpoints.cs
│   │   │   └── HealthEndpoints.cs
│   │   ├── Services/
│   │   │   ├── SseNotificationService.cs ← Listens to PG NOTIFY, pushes SSE
│   │   │   └── DeviceTokenAuthHandler.cs ← Copied from APTM Main
│   │   ├── Program.cs
│   │   └── appsettings.json
│   ├── APTM.Gate.Core/                   ← Business logic, interfaces
│   │   ├── Interfaces/
│   │   │   ├── IGateConfigService.cs
│   │   │   ├── ITagBufferService.cs
│   │   │   ├── IBufferProcessingService.cs
│   │   │   ├── ISyncHubService.cs
│   │   │   └── IDiagnosticsService.cs
│   │   ├── Models/                       ← DTOs, config models
│   │   │   ├── ConfigPackage.cs
│   │   │   ├── ProcessedEvent.cs
│   │   │   ├── SyncPushPayload.cs
│   │   │   ├── SyncPullResponse.cs
│   │   │   └── DisplayData.cs
│   │   └── Enums/
│   │       ├── GateRole.cs               ← Start, Checkpoint, Finish
│   │       └── EventType.cs
│   ├── APTM.Gate.Infrastructure/         ← PostgreSQL, EF Core, services
│   │   ├── Persistence/
│   │   │   ├── GateDbContext.cs
│   │   │   ├── Configurations/           ← EF Core fluent configs
│   │   │   └── Migrations/
│   │   ├── Services/
│   │   │   ├── GateConfigService.cs
│   │   │   ├── TagBufferService.cs
│   │   │   ├── BufferProcessingService.cs
│   │   │   ├── SyncHubService.cs
│   │   │   ├── DiagnosticsService.cs
│   │   │   └── PostgresNotifyService.cs  ← Fires NOTIFY after inserts
│   │   └── DependencyInjection.cs
│   ├── APTM.Gate.Workers/               ← Background services
│   │   ├── TcpReaderWorker.cs           ← Adapted from APTM Main's ReaderWorkerManager
│   │   ├── BufferProcessorWorker.cs     ← Adapted from APTM Main
│   │   └── DependencyInjection.cs
│   └── APTM.Gate.Display/              ← Static web files for displays
│       ├── wwwroot/
│       │   ├── start-display.html
│       │   ├── finish-display.html
│       │   ├── display.css
│       │   └── display.js
└── tests/
    └── APTM.Gate.Tests/
```

---

## 3. PostgreSQL Database Schema

### 3.1 Table Definitions

```sql
-- Gate configuration (one active row at a time)
CREATE TABLE gate_config (
    id SERIAL PRIMARY KEY,
    test_instance_id UUID NOT NULL,
    test_instance_name VARCHAR(200) NOT NULL,
    device_id UUID NOT NULL,
    device_code VARCHAR(50) NOT NULL,
    gate_role VARCHAR(20) NOT NULL,           -- 'Start', 'Checkpoint', 'Finish'
    checkpoint_sequence INT,                   -- NULL unless role = Checkpoint
    scheduled_date DATE NOT NULL,
    data_snapshot_version INT NOT NULL,
    clock_offset_ms INT DEFAULT 0,
    is_active BOOLEAN NOT NULL DEFAULT true,
    received_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Candidate subset for this test instance
CREATE TABLE candidates (
    candidate_id UUID PRIMARY KEY,
    service_number VARCHAR(50) NOT NULL,
    name VARCHAR(200) NOT NULL,
    gender VARCHAR(10) NOT NULL,
    candidate_type_id INT NOT NULL,
    date_of_birth DATE NOT NULL,
    jacket_number INT
);

-- Tag-to-candidate mapping
CREATE TABLE tag_assignments (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    candidate_id UUID NOT NULL REFERENCES candidates(candidate_id),
    tag_epc VARCHAR(64) NOT NULL,
    UNIQUE(tag_epc)
);
CREATE INDEX idx_tag_assignments_epc ON tag_assignments(tag_epc);

-- Checkpoint route config
CREATE TABLE checkpoint_config (
    id SERIAL PRIMARY KEY,
    route_name VARCHAR(100) NOT NULL,
    sequence INT NOT NULL,
    checkpoint_name VARCHAR(100) NOT NULL
);

-- High-throughput raw tag ingestion buffer
CREATE TABLE raw_tag_buffer (
    id BIGSERIAL PRIMARY KEY,
    tag_epc VARCHAR(64) NOT NULL,
    read_time TIMESTAMPTZ NOT NULL,
    antenna_port INT,
    rssi DECIMAL(6,2),
    status VARCHAR(20) NOT NULL DEFAULT 'PENDING',   -- PENDING, PROCESSED, DUPLICATE, UNRESOLVED
    is_duplicate BOOLEAN NOT NULL DEFAULT false,
    inserted_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_raw_tag_buffer_status ON raw_tag_buffer(status) WHERE status = 'PENDING';

-- Resolved/processed tag events
CREATE TABLE processed_events (
    id BIGSERIAL PRIMARY KEY,
    candidate_id UUID NOT NULL REFERENCES candidates(candidate_id),
    tag_epc VARCHAR(64) NOT NULL,
    event_type VARCHAR(20) NOT NULL,            -- 'start_attendance', 'checkpoint', 'finish'
    read_time TIMESTAMPTZ NOT NULL,
    duration_seconds DECIMAL(10,3),             -- Computed if start time known
    checkpoint_sequence INT,
    is_first_read BOOLEAN NOT NULL DEFAULT true,-- First Read Rule
    raw_buffer_id BIGINT REFERENCES raw_tag_buffer(id),
    processed_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_processed_events_candidate ON processed_events(candidate_id);
CREATE INDEX idx_processed_events_type ON processed_events(event_type);

-- Data received from external devices (HHT, tablet, server)
CREATE TABLE received_sync_data (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    client_record_id VARCHAR(64) NOT NULL UNIQUE,
    source_device_id UUID NOT NULL,
    source_device_code VARCHAR(50) NOT NULL,
    data_type VARCHAR(30) NOT NULL,             -- 'race_start', 'attendance', 'ground_reading', 'checkpoint_read', 'finish_read'
    payload JSONB NOT NULL,
    received_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_received_sync_client_record ON received_sync_data(client_record_id);

-- Race start times (from HHT or server push)
CREATE TABLE race_start_times (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    heat_id UUID NOT NULL,
    heat_number INT NOT NULL,
    gun_start_time TIMESTAMPTZ NOT NULL,
    source_device_id UUID NOT NULL,
    candidate_ids UUID[] NOT NULL,              -- Array of candidate IDs in this heat
    received_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(heat_id)
);

-- Sync tracking (what has been pulled by which device)
CREATE TABLE sync_log (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    puller_device_id UUID NOT NULL,
    puller_device_code VARCHAR(50) NOT NULL,
    last_processed_event_id BIGINT NOT NULL DEFAULT 0,
    last_received_sync_id UUID,
    pulled_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Scoring types (from config package, for display purposes)
CREATE TABLE scoring_types (
    scoring_type_id INT PRIMARY KEY,
    name VARCHAR(100) NOT NULL
);

CREATE TABLE scoring_statuses (
    scoring_status_id INT PRIMARY KEY,
    scoring_type_id INT NOT NULL REFERENCES scoring_types(scoring_type_id),
    status_code VARCHAR(30) NOT NULL,
    status_label VARCHAR(100) NOT NULL,
    sequence INT NOT NULL,
    is_passing_status BOOLEAN NOT NULL
);

-- Events in this test (from config package)
CREATE TABLE test_events (
    test_type_event_id INT PRIMARY KEY,
    event_id INT NOT NULL,
    event_name VARCHAR(100) NOT NULL,
    event_type VARCHAR(20) NOT NULL,
    sequence INT NOT NULL,
    scoring_type_id INT REFERENCES scoring_types(scoring_type_id)
);
```

### 3.2 LISTEN/NOTIFY Triggers

```sql
-- Auto-notify on processed event insert
CREATE OR REPLACE FUNCTION notify_tag_event() RETURNS trigger AS $$
DECLARE
    payload JSON;
BEGIN
    SELECT json_build_object(
        'id', NEW.id,
        'candidate_id', NEW.candidate_id,
        'event_type', NEW.event_type,
        'read_time', NEW.read_time,
        'duration_seconds', NEW.duration_seconds,
        'is_first_read', NEW.is_first_read,
        'jacket_number', c.jacket_number,
        'name', c.name
    ) INTO payload
    FROM candidates c WHERE c.candidate_id = NEW.candidate_id;

    PERFORM pg_notify('tag_event', payload::text);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_notify_tag_event
    AFTER INSERT ON processed_events
    FOR EACH ROW
    WHEN (NEW.is_first_read = true)
    EXECUTE FUNCTION notify_tag_event();

-- Auto-notify on race start time insert
CREATE OR REPLACE FUNCTION notify_race_start() RETURNS trigger AS $$
BEGIN
    PERFORM pg_notify('race_start', json_build_object(
        'heat_id', NEW.heat_id,
        'heat_number', NEW.heat_number,
        'gun_start_time', NEW.gun_start_time,
        'candidate_ids', NEW.candidate_ids
    )::text);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_notify_race_start
    AFTER INSERT ON race_start_times
    FOR EACH ROW
    EXECUTE FUNCTION notify_race_start();

-- Auto-notify on received sync data
CREATE OR REPLACE FUNCTION notify_sync_data() RETURNS trigger AS $$
BEGIN
    PERFORM pg_notify('sync_data', json_build_object(
        'id', NEW.id,
        'data_type', NEW.data_type,
        'source_device_code', NEW.source_device_code
    )::text);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_notify_sync_data
    AFTER INSERT ON received_sync_data
    FOR EACH ROW
    EXECUTE FUNCTION notify_sync_data();
```

---

## 4. API Endpoints — Detailed Contracts

### 4.1 POST /gate/config

Receives the config package from a field tablet. Replaces all current config data.

**Auth:** Device Token
**Request Body:** The `ConfigPackageDto` JSON from Codebase 1's export endpoint (same structure).
**Response:** `200 OK` with `{ "status": "configured", "gateRole": "Finish", "candidateCount": 45 }`
**Error:** `400` if validation fails, `409` if gate is mid-race (status = active)

**Handler Logic:**
1. Begin transaction.
2. Truncate all config tables: `candidates`, `tag_assignments`, `checkpoint_config`, `scoring_types`, `scoring_statuses`, `test_events`.
3. Insert new data from the config package.
4. Upsert `gate_config` with the new role, test instance, and snapshot version.
5. Compute and store clock offset: `clock_offset_ms = serverReferenceTime - localUtcNow`.
6. Commit transaction.
7. Fire `NOTIFY config_updated` so displays re-fetch.

### 4.2 GET /gate/events?since={sequenceId}

Returns processed events newer than the given sequence ID.

**Auth:** Device Token
**Query Params:** `since` (BIGINT, default 0) — the `id` of the last event the caller received.
**Response:**
```json
{
    "events": [
        {
            "id": 1234,
            "candidateId": "guid",
            "tagEpc": "E200...",
            "eventType": "finish",
            "readTime": "2026-03-14T10:15:37.456Z",
            "durationSeconds": 7.34,
            "checkpointSequence": null,
            "isFirstRead": true,
            "candidateName": "Kumar",
            "jacketNumber": 12,
            "processedAt": "2026-03-14T10:15:37.510Z"
        }
    ],
    "highWaterMark": 1250,
    "totalEvents": 1250
}
```

### 4.3 POST /gate/sync/push

Accepts sync data from any authorized device on the gate's Wi-Fi.

**Auth:** Device Token
**Request Body:**
```json
{
    "deviceId": "guid",
    "deviceCode": "HHT-001",
    "dataType": "race_start",
    "clientRecordId": "uuid",
    "payload": {
        "heatId": "guid",
        "heatNumber": 1,
        "gunStartTime": "2026-03-14T10:15:30.123Z",
        "candidates": [
            { "candidateId": "guid", "attendanceStatus": "PRESENT" }
        ]
    }
}
```

**Supported `dataType` values:**

| dataType | payload content | Gate action |
|---|---|---|
| `race_start` | heatId, heatNumber, gunStartTime, candidates[] | INSERT into `race_start_times`. If gate is finish: triggers display elapsed time mode. |
| `attendance` | attendanceSessionId, scans[] (tagEPC, candidateId, scannedAt) | INSERT into `received_sync_data`. If gate is start: display updates attendance. |
| `finish_read` | raceSessionId, candidateId, readTime, tagEPC | INSERT into `received_sync_data`. For relay: tablet pushes another gate's finish data. |
| `checkpoint_read` | raceSessionId, candidateId, checkpointSequence, readTime, tagEPC | INSERT into `received_sync_data`. For relay. |
| `ground_reading` | testInstanceEventId, candidateId, scoringStatusId, recordedAt | INSERT into `received_sync_data`. For relay. |

**Response:** `200 OK` with `{ "accepted": true, "clientRecordId": "uuid" }`
**Duplicate:** `200 OK` with `{ "accepted": false, "reason": "duplicate", "clientRecordId": "uuid" }` — idempotent, no error.

**Handler Logic:**
1. Check `client_record_id` uniqueness in `received_sync_data`. If duplicate, return accepted=false.
2. If `dataType` is `race_start`: also INSERT into `race_start_times` (triggers NOTIFY).
3. INSERT into `received_sync_data` with the full payload as JSONB.
4. Return accepted=true.

### 4.4 GET /gate/sync/pull?since={sequenceId}

Returns all data the gate knows about — its own processed events + received sync data.

**Auth:** Device Token
**Response:**
```json
{
    "processedEvents": [ /* same as GET /gate/events */ ],
    "receivedSyncData": [
        {
            "id": "uuid",
            "clientRecordId": "uuid",
            "sourceDeviceCode": "HHT-001",
            "dataType": "race_start",
            "payload": { /* original JSON */ },
            "receivedAt": "2026-03-14T10:15:30.500Z"
        }
    ],
    "raceStartTimes": [
        {
            "heatId": "guid",
            "heatNumber": 1,
            "gunStartTime": "2026-03-14T10:15:30.123Z",
            "sourceDeviceId": "guid"
        }
    ],
    "highWaterMark": 1250
}
```

**Handler Logic:** Also logs the pull in `sync_log` for audit.

### 4.5 GET /gate/sync/status

**Auth:** Device Token
**Response:**
```json
{
    "gateRole": "Finish",
    "testInstanceId": "guid",
    "processedEventCount": 1250,
    "receivedSyncDataCount": 15,
    "raceStartTimesCount": 3,
    "lastEventAt": "2026-03-14T10:15:37.456Z",
    "syncPulls": [
        { "deviceCode": "TAB-001", "lastPulledAt": "2026-03-14T10:20:00Z", "eventsPulled": 1250 }
    ]
}
```

### 4.6 GET /gate/display-data

Returns full current display state. No auth (local Wi-Fi only).

**Response:**
```json
{
    "gateRole": "Finish",
    "testInstanceName": "BPET March 2026",
    "scheduledDate": "2026-03-14",
    "totalCandidates": 45,
    "activeHeat": {
        "heatNumber": 3,
        "hasStartTime": true,
        "gunStartTime": "2026-03-14T10:15:30.123Z",
        "candidates": [
            { "candidateId": "guid", "name": "Kumar", "jacketNumber": 12 }
        ]
    },
    "finishReads": [
        {
            "position": 1,
            "candidateId": "guid",
            "name": "Kumar",
            "jacketNumber": 12,
            "readTime": "2026-03-14T10:15:37.456Z",
            "elapsedSeconds": 7.333,
            "heatNumber": 3
        }
    ],
    "attendance": {
        "totalPresent": 42,
        "totalAbsent": 3,
        "totalNotScanned": 0
    }
}
```

### 4.7 GET /gate/display-stream

SSE (Server-Sent Events) endpoint. No auth.

**Implementation:**

```csharp
app.MapGet("/gate/display-stream", async (HttpContext context, SseNotificationService sseService) =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.Append("Cache-Control", "no-cache");
    context.Response.Headers.Append("Connection", "keep-alive");

    await sseService.StreamEvents(context.Response, context.RequestAborted);
});
```

The `SseNotificationService` subscribes to PostgreSQL channels (`tag_event`, `race_start`, `sync_data`) using Npgsql's `NpgsqlConnection.WaitAsync()` and writes each notification as an SSE event:

```
event: tag_event
data: {"id":1234,"candidateId":"...","name":"Kumar","jacketNumber":12,"eventType":"finish","readTime":"...","durationSeconds":7.333}

event: race_start
data: {"heatId":"...","heatNumber":3,"gunStartTime":"..."}

event: config_updated
data: {"gateRole":"Finish","candidateCount":45}
```

### 4.8 GET /gate/health

No auth. Returns `200 OK` with basic status.

```json
{
    "status": "healthy",
    "database": "connected",
    "readerConnected": true,
    "gateConfigured": true,
    "uptime": "02:15:30"
}
```

### 4.9 GET /gate/diagnostics

**Auth:** Device Token

```json
{
    "reader": {
        "connected": true,
        "model": "Impinj R420",
        "firmwareVersion": "7.2.0",
        "lastSeenAt": "2026-03-14T10:15:37Z"
    },
    "antennas": [
        { "port": 1, "connected": true, "lastReadAt": "2026-03-14T10:15:37Z", "readCount": 312 },
        { "port": 2, "connected": true, "lastReadAt": "2026-03-14T10:15:36Z", "readCount": 298 }
    ],
    "buffer": {
        "pendingCount": 0,
        "processedCount": 1250,
        "unresolvedCount": 3,
        "duplicateCount": 4200
    },
    "database": {
        "connectionPoolUsed": 5,
        "connectionPoolMax": 100,
        "diskUsageMb": 12.5
    }
}
```

### 4.10 PUT /gate/status

**Auth:** Device Token
**Request:** `{ "status": "active" }` or `{ "status": "idle" }`
**Response:** `200 OK` with `{ "previousStatus": "idle", "newStatus": "active" }`

When set to `active`: BufferProcessorWorker begins polling. When `idle`: worker stops.

---

## 5. Background Workers

### 5.1 TcpReaderWorker

Adapted from APTM Main's `ReaderWorkerManager` + `TcpReaderWorker`.

**Changes from original:**
- Connects to a single local reader (TCP `127.0.0.1:<port>` or configured IP).
- Writes directly to PostgreSQL `raw_tag_buffer` table (no need for Channel&lt;T&gt; — PostgreSQL handles concurrent writes).
- Includes `antenna_port` and `rssi` in the buffer row.

**Configuration:** Reader IP and port from `appsettings.json`.

### 5.2 BufferProcessorWorker

Adapted from APTM Main's `BufferProcessorWorker`.

**Changes from original:**
- Reads from PostgreSQL instead of SQL Server.
- After processing, fires PostgreSQL NOTIFY via `PostgresNotifyService` (or relies on the trigger).
- EPC resolution uses the local `tag_assignments` table.
- First Read Rule: queries `processed_events` for existing reads by the same candidate in the same heat.
- For finish gates: if a `race_start_times` row exists for the active heat, computes `duration_seconds = read_time - gun_start_time`.

---

## 6. SSE Notification Service

The `SseNotificationService` is the bridge between PostgreSQL LISTEN/NOTIFY and browser SSE connections.

**Implementation Pattern:**

```csharp
public class SseNotificationService
{
    private readonly string _connectionString;
    private readonly List<StreamWriter> _clients = new();

    public async Task StreamEvents(HttpResponse response, CancellationToken ct)
    {
        var writer = new StreamWriter(response.Body);
        lock (_clients) _clients.Add(writer);

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        finally
        {
            lock (_clients) _clients.Remove(writer);
        }
    }

    // Background task: listens to PostgreSQL NOTIFY
    public async Task ListenLoop(CancellationToken ct)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        conn.Notification += (_, e) =>
        {
            var sseEvent = $"event: {e.Channel}\ndata: {e.Payload}\n\n";
            BroadcastToClients(sseEvent);
        };

        await using (var cmd = new NpgsqlCommand("LISTEN tag_event; LISTEN race_start; LISTEN sync_data; LISTEN config_updated;", conn))
            await cmd.ExecuteNonQueryAsync(ct);

        while (!ct.IsCancellationRequested)
            await conn.WaitAsync(ct);
    }

    private void BroadcastToClients(string sseEvent)
    {
        lock (_clients)
        {
            foreach (var writer in _clients.ToList())
            {
                try
                {
                    writer.Write(sseEvent);
                    writer.Flush();
                }
                catch { _clients.Remove(writer); }
            }
        }
    }
}
```

Register the listen loop as a hosted service.

---

## 7. Display Apps (Static HTML)

Both displays are single HTML files served from `wwwroot/`. They run fullscreen in a browser on the LED display device.

### 7.1 Start/Mid Display (`start-display.html`)

**On load:**
1. Fetch `GET /gate/display-data` for initial state.
2. Open SSE connection to `GET /gate/display-stream`.
3. Render based on gate role.

**Layout (Start gate):**
- Header: date, time, test name, total candidates
- Attendance counters: Present / Absent / Not Scanned
- Scrolling candidate table: Sr No, Jacket, Name, Status (present/absent)
- Active heat section: Heat number, candidate lineup

**Layout (Mid gate):**
- Header: date, time, test name, checkpoint name
- Pass-through table: Jacket, Name, Split Time, ordered by read time

**SSE event handlers:**
- `tag_event`: add row to candidate table (attendance) or pass-through table (checkpoint)
- `race_start`: update active heat section
- `config_updated`: re-fetch full state

**Auto-reconnect:** If SSE drops, retry every 3 seconds. On reconnect, re-fetch full state.

### 7.2 Finish Display (`finish-display.html`)

**On load:**
1. Fetch `GET /gate/display-data` for initial state.
2. Open SSE connection to `GET /gate/display-stream`.

**Layout:**
- Header: date, time, test name, current heat
- Results table: Position, Jacket, Name, Elapsed Time (or "—" if no start time)
- Live feed: new finishers animate in at the top

**Two modes:**
- `hasStartTime = true`: compute `elapsedSeconds = readTime - gunStartTime`, display as "7.34s"
- `hasStartTime = false`: display position and finish timestamp only

**SSE event handlers:**
- `tag_event` (eventType=finish): add finisher row. If start time known, compute elapsed.
- `race_start`: store `gunStartTime` in JS. Recalculate all existing rows. Flash "Live Timing Active" indicator.
- `config_updated`: re-fetch full state.

---

## 8. Configuration

### 8.1 appsettings.json

```json
{
    "ConnectionStrings": {
        "GateDb": "Host=localhost;Port=5432;Database=aptm_gate;Username=aptm;Password=<password>"
    },
    "Reader": {
        "Host": "127.0.0.1",
        "Port": 5084,
        "ReconnectDelayMs": 5000
    },
    "Gate": {
        "DeviceCode": "GATE-001",
        "ApiToken": "<hashed-token>"
    },
    "Kestrel": {
        "Endpoints": {
            "Http": { "Url": "http://0.0.0.0:5000" }
        }
    },
    "Logging": {
        "LogLevel": {
            "Default": "Information",
            "Microsoft.AspNetCore": "Warning",
            "Npgsql": "Warning"
        }
    }
}
```

### 8.2 PostgreSQL Setup (Ubuntu)

```bash
sudo apt install postgresql postgresql-contrib
sudo -u postgres createuser aptm --pwprompt
sudo -u postgres createdb aptm_gate --owner=aptm
# Apply schema from Section 3.1 and triggers from Section 3.2
```

### 8.3 Systemd Service

```ini
[Unit]
Description=APTM Gate Service
After=postgresql.service

[Service]
WorkingDirectory=/opt/aptm-gate
ExecStart=/opt/aptm-gate/APTM.Gate.Api
Restart=always
RestartSec=5
User=aptm
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_CONTENTROOT=/opt/aptm-gate

[Install]
WantedBy=multi-user.target
```

---

## 9. Implementation Steps

Build in this order:

### Phase 1: Project Setup
1. Create the solution and project structure as defined in Section 2.
2. Add NuGet packages: `Npgsql.EntityFrameworkCore.PostgreSQL`, `Npgsql` (for LISTEN/NOTIFY), `FluentValidation`.
3. Set up `GateDbContext` with EF Core + Npgsql provider.
4. Create entity classes matching the PostgreSQL schema.
5. Generate initial EF Core migration. Apply to local PostgreSQL instance.
6. Apply LISTEN/NOTIFY triggers from Section 3.2.

### Phase 2: Config + Health
7. Implement `POST /gate/config` — accept config package, populate tables.
8. Implement `GET /gate/health` — check DB connection and reader status.
9. Implement `GET /gate/status` — return current gate role and counters.
10. Test: push a config package via curl/Postman, verify DB is populated.

### Phase 3: Tag Capture
11. Adapt `TcpReaderWorker` from APTM Main — connect to local UHF reader, write to `raw_tag_buffer`.
12. Adapt `BufferProcessorWorker` — read pending buffer rows, resolve EPC via `tag_assignments`, apply First Read Rule, write to `processed_events`.
13. Test: simulate tag reads (or connect a real reader), verify processed events appear in DB.

### Phase 4: Display SSE
14. Implement `SseNotificationService` — listen to PostgreSQL NOTIFY channels, broadcast to SSE clients.
15. Implement `GET /gate/display-stream` — SSE endpoint.
16. Implement `GET /gate/display-data` — full state JSON.
17. Build `start-display.html` and `finish-display.html`.
18. Test: open display in browser, insert a processed event manually, verify SSE pushes to display.

### Phase 5: Sync Hub
19. Implement `POST /gate/sync/push` — accept sync data from HHTs/tablets, dedup by clientRecordId.
20. Implement `GET /gate/sync/pull` — return all events + received data.
21. Implement `GET /gate/sync/status`.
22. Implement `GET /gate/diagnostics`.
23. Test: simulate HHT pushing a race start time, verify finish display switches to elapsed time mode.

### Phase 6: Device Auth
24. Copy `DeviceTokenAuthHandler` from APTM Main. Configure for the gate's own token.
25. Apply `[Authorize]` to all endpoints except health, display-data, and display-stream.
26. Test: verify unauthorized requests are rejected.

### Phase 7: Integration Testing
27. Full flow test: push config → start reader → fire gun (simulate HHT push) → simulate tag crossings → verify display shows live elapsed times.
28. Test sync pull: verify tablet can pull all data from the gate.
29. Test idempotency: push the same clientRecordId twice, verify no duplicate.

---

## 10. Key Implementation Notes

- **EF Core with PostgreSQL:** Use `UseNpgsql()` in `GateDbContext`. Do NOT use `UseSnakeCaseNamingConvention()` unless you want all C# property names auto-mapped — be explicit with `HasColumnName()` in configurations.
- **LISTEN/NOTIFY connection:** The notification listener needs its own dedicated `NpgsqlConnection` that stays open. Do NOT use the EF Core connection pool for this — open a separate connection in the `SseNotificationService`.
- **UUID generation:** PostgreSQL `gen_random_uuid()` handles UUID PKs. For EF Core, use `ValueGeneratedOnAdd()`.
- **BIGSERIAL PKs:** For `raw_tag_buffer` and `processed_events`, use `long` in C# with `ValueGeneratedOnAdd()`.
- **TIMESTAMPTZ:** All timestamps should be `DateTimeOffset` in C# and `TIMESTAMPTZ` in PostgreSQL. Npgsql handles the mapping.
- **Static files:** Configure Kestrel to serve `wwwroot/` for the display HTML files. Use `app.UseStaticFiles()`.
- **CORS:** Not needed — all requests come from the same origin (gate's Wi-Fi) or from devices calling the API directly.

---

*Reference: APTM_Application_Architecture_v2.md, Sections 3.2, 3.3, 3.4*

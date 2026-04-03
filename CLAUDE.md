# APTM Gate Service

## Project Overview
Gate Service — .NET 10 ASP.NET Core Minimal API running on each gate's NUC (Ubuntu). Captures UHF tags from a directly connected reader via TCP, processes them into resolved candidate events, serves real-time data to LED displays via SSE, and acts as a local sync hub for devices on the gate's Wi-Fi.

## Tech Stack
- .NET 10 (C#)
- ASP.NET Core Minimal API (not controllers)
- PostgreSQL 16 with Npgsql + EF Core
- PostgreSQL LISTEN/NOTIFY → SSE bridge for real-time display
- TCP socket-based UHF RFID reader integration
- Swagger/OpenAPI via Swashbuckle 8.x

## Solution Structure
```
APTM.Gate.slnx
├── src/APTM.Gate.Api/              ← Executable — endpoints, SSE, auth, Swagger, static display files
│   ├── Endpoints/                  ← Minimal API endpoint groups (Config, Sync, Display, Diagnostics, Health)
│   ├── Services/                   ← SseNotificationService, DeviceTokenAuthHandler
│   ├── wwwroot/                    ← Display HTML/CSS/JS served via UseStaticFiles()
│   └── Program.cs
├── src/APTM.Gate.Core/             ← Interfaces, models, enums (zero external deps)
│   ├── Enums/                      ← GateRole, EventType, BufferStatus
│   ├── Interfaces/                 ← IGateConfigService, ITagBufferService, IBufferProcessingService, etc.
│   └── Models/                     ← DTOs: ConfigPackageDto, DisplayData, SyncPushPayload, etc.
├── src/APTM.Gate.Infrastructure/   ← PostgreSQL persistence, EF Core, service implementations
│   ├── Entities/                   ← 12 EF entities (snake_case mapped)
│   ├── Persistence/                ← GateDbContext, Configurations/, Migrations/, init_triggers.sql
│   └── Services/                   ← GateConfigService, BufferProcessingService, SyncHubService, etc.
└── src/APTM.Gate.Workers/          ← BackgroundService workers
    ├── TcpReaderWorker.cs          ← Full TCP reader: persistent connection, frame parsing, auto-reconnect
    ├── UhfFrameParser.cs           ← Length-prefixed binary frame parser
    ├── BufferProcessorWorker.cs    ← Signal-driven batch processor (polls 500ms, batch 100)
    └── DependencyInjection.cs
```

## Dependency Graph
```
Core (no deps)
  ↑
Infrastructure → Core
  ↑
Workers → Core, Infrastructure
  ↑
Api → Core, Infrastructure, Workers
```

## Database
- PostgreSQL 16 (`aptm_gate` database)
- EF Core with Code-First migrations in `Infrastructure/Persistence/Migrations/`
- Migrations + NOTIFY triggers auto-applied at startup via `PostgresInitService`
- Connection string in `appsettings.json` → `ConnectionStrings:GateDb`
- All columns use explicit snake_case via `HasColumnName()` in Fluent API configs
- UUID PKs use `gen_random_uuid()`, BIGSERIAL for raw_tag_buffer and processed_events

### EF Migration Commands
```bash
# Add migration
dotnet ef migrations add <Name> --project src/APTM.Gate.Infrastructure --startup-project src/APTM.Gate.Api --output-dir Persistence/Migrations

# Remove last migration
dotnet ef migrations remove --project src/APTM.Gate.Infrastructure --startup-project src/APTM.Gate.Api
```

### PostgreSQL LISTEN/NOTIFY Channels
- `tag_event` — trigger on processed_events INSERT (first reads only), joins candidates for name/jacket
- `race_start` — trigger on race_start_times INSERT, includes candidate_ids array
- `sync_data` — trigger on received_sync_data INSERT, uses source_device_code
- `config_updated` — fired manually by GateConfigService after config apply

## Build & Run
```bash
dotnet build APTM.Gate.slnx
dotnet run --project src/APTM.Gate.Api
```

## API Endpoints
| Method | Path | Auth | Description |
|--------|------|------|-------------|
| POST | /gate/config | Yes | Apply config package from APTM Main |
| PUT | /gate/status | Yes | Toggle active/idle |
| GET | /gate/events?since= | Yes | Processed tag events |
| POST | /gate/sync/push | Yes | Push sync data (race_start, attendance, etc.) |
| GET | /gate/sync/pull?since= | Yes | Pull all gate data |
| GET | /gate/sync/status | Yes | Sync hub status |
| GET | /gate/display-data | No | Full display state JSON |
| GET | /gate/display-stream | No | SSE event stream |
| GET | /gate/diagnostics | Yes | Reader + buffer diagnostics |
| GET | /gate/health | No | Health check |

## Display Files
Static HTML served from `Api/wwwroot/` via `UseStaticFiles()`:
- `start-display.html` — Start gate (attendance) + Checkpoint gate (pass-through reads)
- `finish-display.html` — Finish gate (results with elapsed time, heat timer)
- `display.css` — Shared dark theme styles
- `display.js` — Shared SSE connection, clock, feed, data loading

Decision: Display files live in Api/wwwroot (not a separate project) because UseStaticFiles() serves from the executable's wwwroot by default, and running a second process on each NUC adds unnecessary deployment complexity.

## Auth
Bearer token auth via `DeviceTokenAuthHandler`. Token is bcrypt-hashed in `appsettings.json` → `Gate:ApiToken`. Health, display-data, and display-stream endpoints have no auth.

## Development Guidelines
- Follow existing code style and conventions
- Minimal API style — no controllers, use endpoint groups with extension methods
- All DI registration goes through `DependencyInjection.cs` extension methods per project
- Entity configurations use Fluent API with explicit `HasColumnName("snake_case")` — do NOT use UseSnakeCaseNamingConvention()
- Core layer must have ZERO external dependencies
- Workers are class libraries registered as `IHostedService` — NOT separate executables
- The SSE notification listener uses its own dedicated NpgsqlConnection (not from EF pool)
- All timestamps are `DateTimeOffset` in C# → `TIMESTAMPTZ` in PostgreSQL
- Spec reference: `CODEBASE_2_APTM_GATE_SERVICE.md` in repo root

## Relationship to APTM Main
- APTM Main (`C:\Users\DeLL\source\repos\APTM_SN\APTM_SN`) exports ConfigPackageDto via `GET /api/Sync/config-package/{testInstanceId}`
- Gate receives this via `POST /gate/config` (same DTO shape)
- Clock sync via APTM Main's `GET /api/Sync/reference-time`
- ReaderWorkerManager and BufferProcessorWorker moved FROM Main TO Gate Service

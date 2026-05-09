# IMPLEMENTATION PLAN — Predefined Gate Roles

## Context

Refactor APTM Gate Service + APTM Field App so a NUC's role (Start / Checkpoint / Finish) is predefined per device, set once by the field app, and never changes during operation. Treated as a fresh install — no migration / backward compatibility needed; existing demo NUCs will be re-imaged.

## Three Roles

| Role | Reader | Display | Behavior |
|---|---|---|---|
| **Start** | None | `led-start-display.html` | Shows event name + race-start info pushed from HHT. No tag processing. |
| **Checkpoint** | Connected | None (headless) | Records raw reads, runs a dedup-only processor (no candidate / event awareness). Field app pulls processed events, then clears the raw buffer. Controllable from field app (park / shutdown). |
| **Finish** | Connected | `finish-display.html` | Full pipeline: raw → processed with candidate resolution + gun-time elapsed + heat matching. Renders live results. |

**Universal rule:** all roles dedup with a 1-minute per-tag cooldown — same tag is processed again after 60 s. Configurable via `Gate:DedupWindowSeconds`.

## Architecture Decisions

1. Role lives on a new dedicated entity `GateIdentity` — single-row table per NUC, written once via `PUT /gate/identity` from the field app.
2. Role is **decoupled from the config-package**. `ConfigPackageDto.Gates[].GateType` becomes informational; the gate validates the package's gateType matches its provisioned identity and rejects mismatches.
3. Role-conditional service registration at startup: Start gates skip `TcpReaderWorker`, un-provisioned NUCs skip both workers.
4. Role-conditional static-file routing: `GET /` serves the right HTML based on identity (or 204 for Checkpoint).
5. Identity changes require a service restart. Acceptable since this is a one-time provisioning step.

## Gate Service Changes

### New entity `GateIdentity`

`APTM.Gate.Infrastructure/Entities/GateIdentity.cs`

```
Id                    int  PK = 1 (single-row check constraint)
Role                  string  enum-validated: Start | Checkpoint | Finish
CheckpointSequence    int?    non-null iff Role == Checkpoint
DeviceCode            string  matches Gate:DeviceCode in appsettings
SetAt                 DateTimeOffset
SetBy                 string  token label from DeviceTokenAuthHandler
```

EF migration `AddGateIdentity` — additive, no seed data.

### New endpoints

```
GET  /gate/identity                       Auth required
PUT  /gate/identity?force=true            Auth required
POST /gate/park                           Auth required, Checkpoint/Finish only
POST /gate/shutdown                       Auth required, Checkpoint/Finish only
```

`PUT /gate/identity`:
- Body `{ role, checkpointSequence? }`. Validate enum + `checkpointSequence` non-null iff `role == Checkpoint`.
- UPSERT into `gate_identity` (Id=1). Idempotent for same payload.
- Mismatched role without `?force=true` → 409. With `?force=true`, purge `raw_tag_buffer` and active-event `processed_events` before swap.
- Returns `{ identity, restartRequired: true }` when role changed.
- Fires `NOTIFY identity_updated` so the cached `IGateIdentityProvider` refreshes.

`POST /gate/park`:
- Stops `TcpReaderWorker` and `BufferProcessorWorker` via cancellation. Drains in-flight buffer batch.
- Service stays alive — `/gate/health`, `/gate/sync/pull`, `/gate/identity` keep working.
- Reversible by `PUT /gate/status` (active).

`POST /gate/shutdown`:
- Same drain as park. Then `Environment.Exit(0)`.
- Operates with systemd unit `Restart=on-failure` (NOT `always`) so clean exit is permanent until power-cycle / SSH restart.

### Role-conditional DI

`Program.cs` reads `gate_identity` after `AddInfrastructureServices` and before `AddWorkerServices(role)`:

| Role | TcpReaderWorker | BufferProcessorWorker | IReaderStatusProvider |
|---|---|---|---|
| `Start` | not registered | registered | `NullReaderStatusProvider` (always disconnected) |
| `Checkpoint` | registered | registered | real |
| `Finish` | registered | registered | real |
| _un-provisioned_ | not registered | not registered | `NullReaderStatusProvider` |

Un-provisioned NUC: only `/gate/identity` and `/gate/health` work; everything else returns 503.

### Universal 2-minute dedup

`BufferProcessingService.ProcessBatchAsync` — replace the first-read-only logic (`:78-89, 102-111`):

```
For each row in batch:
  prev = MAX(read_time) from processed_events
         WHERE candidate_id = row.candidateId
           AND event_id     = activeEventId
           AND checkpoint_sequence = identity.checkpointSequence  // null for Start/Finish
  if prev is not null AND (row.readTime - prev) < window:
    mark DUPLICATE, skip insert
  else:
    insert ProcessedEvent (IsFirstRead = true if no prior, else false)
    update in-memory dict so intra-batch rows dedup correctly
```

Window via `Gate:DedupWindowSeconds` (default `120`). The "discard reads before gun fire" branch (`:134-142`) stays finish-only.

`IsFirstRead` keeps meaning: true for the first read in each 2-min window. `DisplayEndpoints.cs:77,99` continues to filter on it without changes.

### Display routing

New `GET /` (anonymous) reads identity:
- `Start` → `Results.File("wwwroot/start-display.html", "text/html")`
- `Finish` → `Results.File("wwwroot/finish-display.html", "text/html")`
- `Checkpoint` → `Results.NoContent()` (or a tiny "headless gate" stub page)
- un-provisioned → `Results.Problem(statusCode: 503, "gate not provisioned")`

`UseStaticFiles()` still serves direct paths for debugging. `display.js` does not need to know the role.

### Endpoint authorization

New `RequireReaderRoleFilter` reads from cached `IGateIdentityProvider`. Apply to:

| Endpoint | Filter |
|---|---|
| `/gate/reader/*` | 410 Gone on Start |
| `/gate/diagnostics` | 410 Gone on Start |
| `PUT /gate/status` | 410 Gone on Start |
| `/gate/park`, `/gate/shutdown` | 410 Gone on Start, 503 on un-provisioned |
| everything else | open to all roles |

### Strip role from config-package flow

`GateConfigService.ApplyConfigAsync` (`:178-179`):
- Remove the `GateRole = thisGate.GateType` and `CheckpointSequence = thisGate.CheckpointSequence` writes.
- Add validation: `if thisGate.GateType != identity.Role → ConfigResult.Fail("config role mismatch")`.

`GateConfig.GateRole` and `GateConfig.CheckpointSequence` columns: removed in the same migration as `AddGateIdentity` (fresh install — no consumers in the field).

## Field App Changes

### Retrofit + repository

`data/remote/api/GateApi.kt`:

```kotlin
@GET("/gate/identity") suspend fun getIdentity(): Response<GateIdentityDto>
@PUT("/gate/identity") suspend fun setIdentity(@Body req: SetGateIdentityRequest, @Query("force") force: Boolean = false): Response<SetGateIdentityResponse>
@POST("/gate/park")    suspend fun park(): Response<Unit>
@POST("/gate/shutdown") suspend fun shutdown(): Response<Unit>
```

DTOs in `data/remote/dto/`:

```kotlin
data class GateIdentityDto(val role: String, val checkpointSequence: Int?, val deviceCode: String, val setAt: String)
data class SetGateIdentityRequest(val role: String, val checkpointSequence: Int?)
data class SetGateIdentityResponse(val identity: GateIdentityDto, val restartRequired: Boolean)
```

`GateSyncRepository` adds `getIdentity`, `setIdentity`, `park`, `shutdown`. New use cases `PushGateIdentityUseCase`, `ParkGateUseCase`, `ShutdownGateUseCase`.

### Identity provisioning screen

Repurpose `GateRoleScreen` / `GateRoleViewModel`:
- Strip config-selection card (`:117-133`) and event-selection (`:171-191`).
- Strip `GateRoleViewModel.applyRole`'s JSON mutation (`:130-166`).
- Keep: role chips (Start / Checkpoint / Finish) + checkpoint-sequence input + Apply button.
- Apply button calls `PushGateIdentityUseCase`. On `restartRequired = true`, surface a banner: "Service must be restarted on the NUC for the new role to take effect."

### Config-push decoupling

`PushConfigToGateUseCase` no longer mutates `gates[].gateType`. Sends the package as-is. `GateConfigPushViewModel` surfaces 400 mismatch errors inline.

### Gate Dashboard

`GateDashboardScreen`:
- Fetch `getIdentity()` alongside `getStatus()` on entry.
- Render role badge prominently.
- Warn if `identity.role` ≠ last config's `gateType` ("Re-push corrected config").
- Add Park + Shutdown buttons (with confirmation dialogs). Hidden on un-provisioned and on Start gates if `/gate/park` and `/gate/shutdown` are filtered there.

## Rollout (fresh install)

1. Ship Gate Service: new entity + endpoints + DI + dedup + display routing + park/shutdown.
2. Ship Field App: identity API + provisioning UI + dashboard controls.
3. Image NUCs.
4. For each NUC: power on → field-app to it → set identity → restart service → push config → run race.
5. End-of-race: field-app to each Checkpoint NUC → Shutdown.

No migration, no feature flags, no backward compat path.

## Open Questions / Risks

- **Finish-gate dedup semantics.** Universal 2-min cooldown means a candidate can produce 2 finish events in a single heat if their tag re-reads >2 min later. Acceptable, or do we want finish-specific "first read of event wins"? — _decision pending_
- **Hard shutdown mechanism.** `Environment.Exit(0)` + systemd `Restart=on-failure` (cleanest, requires unit edit) vs. invoking `systemctl poweroff` via setuid wrapper (full NUC power-down). — _default: Environment.Exit_
- **Park vs. Shutdown UI.** One button with two confirmation paths, or two distinct buttons? — _default: two distinct buttons_
- **`?force=true` purge scope.** Currently spec'd to clear `raw_tag_buffer` + active-event `processed_events`. Confirm.
- **Start gate's `start-display.html`.** Verify it actually renders `activeHeat` from `DisplayEndpoints` (not yet inspected).
- **`EventCacheDao` consumers of `IsFirstRead`** in field app — verify nothing breaks under new semantics.

## Implementation Order

1. **Piece 1** — `GateIdentity` entity + DbContext + migration
2. **Piece 2** — `IGateIdentityService` + cached provider + `/gate/identity` endpoint
3. **Piece 3** — Role-conditional DI / un-provisioned mode at startup
4. **Piece 4** — Universal 2-min dedup in `BufferProcessingService`
5. **Piece 5** — Role-based static-file routing for `/`
6. **Piece 6** — `/gate/park` + `/gate/shutdown` endpoints
7. **Piece 7** — Strip role from config-package flow
8. **Piece 8** — Field app: Retrofit + DTOs + repository + use cases
9. **Piece 9** — Field app: repurpose `GateRoleScreen` as identity provisioning
10. **Piece 10** — Field app: dashboard role badge + park/shutdown buttons

Each piece should compile and ship independently where possible. Pieces 1-3 establish the foundation; 4-7 deliver the role behavior; 8-10 wire the field app.

---

# Phase 2 — Checkpoint as Pure Recorder

## Why Phase 2

Phase 1 designed Checkpoint as "Finish-with-1-minute-dedup". That assumed Checkpoint receives a config-package (so it has tag_assignments to resolve EPCs to candidates) and ran the same `BufferProcessingService.ProcessBatchAsync` path as Finish.

That assumption is wrong. Checkpoint NUCs sit at remote points along the route — the operator never visits them with a tablet to push config. The gate service has no way to know which test, which event, or which candidate owns which EPC. It just sees physical tag passes.

**The corrected model:**

- The reader runs in Real-Time Inventory mode and emits ~100–500 frames per tag-pass over 2–5 seconds (one frame per radio cycle while the tag is in range). All of those land verbatim in `raw_tag_buffer`.
- Checkpoint runs a **dedup-only** processor: it reads PENDING raw rows, applies the 1-minute cooldown by `(tag_epc, checkpoint_sequence)`, and writes minimal `ProcessedEvent` rows — only `tag_epc`, `read_time`, `checkpoint_sequence`. No candidate, no event, no heat, no duration.
- The field app pulls only processed events (the existing `/gate/events` and `/gate/sync/pull` already serve them). After a successful pull, the field app calls a new `/gate/raw/clear` to delete the bulky raw rows on the gate so storage doesn't grow without bound.
- Main system / field app does the candidate resolution and event inference downstream, using its own copy of the candidate registry and the known race start/end times.

## Pipeline by role (after Phase 2)

```
Start gate:      no reader, no buffer, no processor.

Checkpoint:      Reader  ───────►  raw_tag_buffer  (~hundreds of rows per pass)
                                         │
                                         ▼
                                  BufferProcessor (always-on)
                                         │
                                         ▼
                                  ProcessedEvent  (1 row per pass)
                                  ─ EventType = "checkpoint"
                                  ─ CandidateId = NULL
                                  ─ EventId = NULL
                                  ─ CheckpointSequence from identity
                                         │
                                         ▼
                                  Field app pulls + clears raw buffer

Finish gate:     Reader  ───────►  raw_tag_buffer
                                         │
                                         ▼
                                  BufferProcessor (full pipeline)
                                  ─ tag_assignments → CandidateId
                                  ─ race_start_times → HeatNumber, DurationSeconds
                                  ─ pre-gun reads discarded
                                         │
                                         ▼
                                  ProcessedEvent (enriched, displayed live)
```

## Schema change

`ProcessedEvent.CandidateId` becomes nullable so checkpoint rows can be written without a resolved candidate. This is the only schema change.

```diff
- public Guid CandidateId { get; set; }
+ public Guid? CandidateId { get; set; }
- public CandidateEntity Candidate { get; set; } = default!;
+ public CandidateEntity? Candidate { get; set; }
```

`ProcessedEventConfiguration` drops `IsRequired()` on `CandidateId`. FK behavior stays as `NoAction` (or `SetNull` if a candidate is deleted, which doesn't happen in practice). New EF migration `MakeProcessedEventCandidateNullable`.

## BufferProcessingService changes

Two branches inside `ProcessBatchAsync`:

```
if identity.Role == Checkpoint:
    # Dedup-only path — no config dependency
    for each PENDING raw row in batch:
        prev = SELECT MAX(read_time) FROM processed_events
                WHERE event_type = 'checkpoint'
                  AND tag_epc = row.tag_epc
                  AND checkpoint_sequence = identity.checkpoint_sequence
        if prev is not null AND (row.read_time - prev) < dedupWindow:
            row.status = 'DUPLICATE'; row.is_duplicate = true
        else:
            INSERT ProcessedEvent(
                candidate_id = NULL,
                tag_epc = row.tag_epc,
                event_type = 'checkpoint',
                event_id = NULL,
                read_time = row.read_time,
                checkpoint_sequence = identity.checkpoint_sequence,
                is_first_read = true,
                raw_buffer_id = row.id)
            row.status = 'PROCESSED'

else:  # Finish
    # Existing pipeline (universal 1-min dedup, candidate resolution, gun-time math)
    # plus: change "no config → mark UNRESOLVED" to "no config → leave PENDING".
```

The Checkpoint path takes no `tag_assignments`, no `gate_config`, no `race_start_times`, no candidate map. Pure dedup.

## Auto-activation for Checkpoint

In `Program.cs`, after identity is resolved at startup:

```csharp
if (identity?.Role == GateRole.Checkpoint)
    statusProvider.SetActive(true);
```

A freshly-imaged Checkpoint NUC starts processing reads the moment the reader connects, without any operator intervention. PUT `/gate/status idle` still works to pause if needed.

## New endpoints

**`POST /gate/buffer/process-now`** — synchronous flush. Loops `ProcessBatchAsync(100)` until 0 returned. Returns `{ processedRows, durationMs, remainingPending }`. Auth + `RequireReaderRole`. Used by field app right before pulling so any reads from the last 0–500 ms aren't left behind in the buffer.

**`POST /gate/raw/clear`** — `DELETE FROM raw_tag_buffer WHERE id <= upToId AND status != 'PENDING'`. Refuses to delete PENDING rows so unprocessed data is never lost. With `?force=true`, deletes everything up to id (used only when wiping a NUC for re-deployment). Updates `processed_events.raw_buffer_id = NULL` for affected rows first to keep the FK clean. Returns `{ deletedCount, remainingTotal }`.

## Field app changes

**Use cases:**
- `ProcessNowUseCase` — calls `/gate/buffer/process-now`.
- `ClearRawUseCase` — calls `/gate/raw/clear?upToId=N`.
- `PullAndClearUseCase` — composite: process-now → existing pull (returns processed events via `/gate/sync/pull`) → clear-raw with `upToId=highWaterMark`. One operator tap.

**Dashboard branches by role:**

For Checkpoint — minimal surface:
- Hide: Tags Processed, Candidates Present, Last Read, heat info (none of those concepts apply).
- Hide: Push Config button (checkpoint never receives config), Start Gate / Stop Gate (auto-active).
- Show: Buffer Pending count, Processed Events count, single "Pull & Clear" button.
- Keep: Park, Shutdown, role badge.

For Start / Finish — unchanged from Phase 1.

## Phase 2 piece order

11. `ProcessedEvent.CandidateId` nullable — entity, configuration, migration.
12. `BufferProcessingService` Checkpoint branch.
13. Auto-activate processor for Checkpoint at startup.
14. `POST /gate/buffer/process-now` endpoint.
15. `POST /gate/raw/clear` endpoint.
16. Stop marking UNRESOLVED on no-config (Finish only).
17. Field app — use cases (ProcessNow, ClearRaw, PullAndClear).
18. Field app — Checkpoint dashboard mode.

## Phase 2 manual step

After Piece 11, generate the migration:

```bash
dotnet ef migrations add MakeProcessedEventCandidateNullable \
  --project src/APTM.Gate.Infrastructure \
  --startup-project src/APTM.Gate.Api \
  --output-dir Persistence/Migrations
```

Combined with Phase 1's `AddGateIdentity` migration, both apply at startup via `PostgresInitService.MigrateAsync`.

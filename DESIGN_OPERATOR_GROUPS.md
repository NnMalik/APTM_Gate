# Operator Groups — Design Document

**Status:** Draft for review
**Author:** Engineering
**Spans:** APTM Main (.NET / SQL Server) · APTM Gate (.NET / PostgreSQL) · APTM HHT (Kotlin / Room) · APTM Field (Kotlin / Room)
**Estimated effort:** ~7 days for core (phases 0–7), +1 day for polish (phase 8)
**Last updated:** 2026-05-10

---

## 1. Problem statement

A real test runs with up to ~500 candidates and 5 trainers, each responsible for ~100 candidates. Today every HHT loads the full candidate roster from the test config and can scan, score, or assign-to-heat **any** candidate. UHF read range is several metres, so:

- A trainer's HHT silently picks up tags from neighbouring trainers' candidates.
- Wrongly-scanned attendance locks out the legitimate trainer (local dedup).
- Wrongly-scored ground activity creates dual-truth conflicts on Main.
- Multiple trainers numbering heats independently for the same race event collide on `heatNumber`, causing void/cancellation cross-contamination on the gate.

We need **server-side bifurcation**: Main defines candidate groups, devices are assigned to one or more groups, and the HHT silently ignores reads outside its group. Critically, the gate must remain group-aware so that **multiple parallel race starts** for the same event compute correct elapsed times per group.

This document is the agreed design before implementation begins.

---

## 2. Design decisions (the six forks)

These are the choices that shape every other decision in this doc. Once accepted, downstream specs follow.

| # | Question | Decision | Rationale |
|---|---|---|---|
| 1 | Mandatory or optional grouping? | **Optional per test instance.** If a test has *zero* operator groups, devices see all candidates (legacy behaviour). If a test has *any* group, every device used in the test **must** be assigned to at least one group; otherwise that device cannot push to Main / gate for that test. | Backward-compatible for small one-trainer tests; enforced for big multi-trainer ones. The "must be assigned once any group exists" rule prevents a forgotten device from accidentally scanning across the whole roster. |
| 2 | Heat numbering scheme | **Per-group with prefix.** Heats are stored as `(groupId, heatNumber)` unique on Main. Display layer renders as `A-1`, `A-2`, `B-1` etc. The DB-level identifier remains `heatId` (UUID); `heatNumber` is operator-facing. | Intuitive for operators ("my heat 3"); avoids cross-group cognitive load. |
| 3 | Operator override at HHT | **Allowed, but logged.** Trainer can deselect a server-assigned group or add an unassigned one. Both actions write a `operator_group_override_log` row that admins can audit. | Field operators need an out when assignments are wrong / batteries die / operator swaps. Logging keeps it traceable. |
| 4 | Out-of-scope scan policy | **Silent drop on the HHT** by default — no haptic, no overlay, no local DB write, no sync queue entry. Reads are still logged in `raw_tag_buffer` on the gate (when the gate is the scanner) for forensic purposes. Operator can opt into "warn on out-of-scope" via Settings. | Race-time efficiency. The gate's audit trail catches anything important. The opt-in warning is for trainers who want to know they're standing too close to another group. |
| 5 | Group definition lifecycle | **Frozen at test start (`TestInstance.Status = ACTIVE`)**. Admin-only mid-test edits go through a dedicated endpoint that explicitly re-classifies existing scans (sets `outOfScope = true` on records that violate the new group). | Mid-test schema changes are a known footgun; we make them rare and explicit. |
| 6 | Candidate in zero groups | **Warn at admin setup time, soft-allow at runtime.** A candidate not in any group is treated as in-scope for every device (legacy behaviour). Admin UI surfaces the count: *"3 candidates have no group — they will be visible to all trainers."* | Don't block test start over a roster oversight. Make it visible so admins can fix it before kick-off. |

---

## 3. Data model

### 3.1 Main (SQL Server) — canonical source of truth

Three new tables. All cascade-deletable when the parent test instance is deleted.

```sql
CREATE TABLE OperatorGroups (
  Id              uniqueidentifier PRIMARY KEY DEFAULT NEWID(),
  TestInstanceId  uniqueidentifier NOT NULL REFERENCES TestInstances(Id) ON DELETE CASCADE,
  Name            nvarchar(100)    NOT NULL,
  FilterSpec      nvarchar(max)    NULL,                  -- JSON; null when hand-picked
  CreatedAt       datetimeoffset   NOT NULL DEFAULT SYSDATETIMEOFFSET(),
  CreatedBy       uniqueidentifier NOT NULL REFERENCES Users(Id),
  IsLocked        bit              NOT NULL DEFAULT 0,    -- set when test starts (decision 5)

  CONSTRAINT UQ_OperatorGroups_TestName UNIQUE (TestInstanceId, Name)
);
CREATE INDEX IX_OperatorGroups_TestInstance ON OperatorGroups(TestInstanceId);

CREATE TABLE OperatorGroupCandidates (
  GroupId      uniqueidentifier NOT NULL REFERENCES OperatorGroups(Id) ON DELETE CASCADE,
  CandidateId  uniqueidentifier NOT NULL REFERENCES Candidates(Id),
  AddedAt      datetimeoffset   NOT NULL DEFAULT SYSDATETIMEOFFSET(),

  CONSTRAINT PK_OperatorGroupCandidates PRIMARY KEY (GroupId, CandidateId)
);
CREATE INDEX IX_OperatorGroupCandidates_Candidate ON OperatorGroupCandidates(CandidateId);

CREATE TABLE OperatorGroupAssignments (
  GroupId     uniqueidentifier NOT NULL REFERENCES OperatorGroups(Id) ON DELETE CASCADE,
  DeviceId    uniqueidentifier NOT NULL REFERENCES Devices(Id),
  AssignedAt  datetimeoffset   NOT NULL DEFAULT SYSDATETIMEOFFSET(),
  AssignedBy  uniqueidentifier NOT NULL REFERENCES Users(Id),

  CONSTRAINT PK_OperatorGroupAssignments PRIMARY KEY (GroupId, DeviceId)
);
CREATE INDEX IX_OperatorGroupAssignments_Device ON OperatorGroupAssignments(DeviceId);
```

`FilterSpec` JSON shape (used as a preset for re-applying when candidate roster changes):

```json
{
  "predicates": [
    { "column": "Company", "values": ["A", "B"] },
    { "column": "Rank",    "values": ["Officer", "NCO"] }
  ],
  "manualAdditions":   ["uuid-1", "uuid-2"],
  "manualExclusions":  ["uuid-3"]
}
```

Predicates AND together; values within a predicate OR together. `manualAdditions` are appended after the predicate result; `manualExclusions` are subtracted. Filter resolution is deterministic given the candidate roster.

### 3.2 Gate (PostgreSQL) — race-aware mirror

```sql
CREATE TABLE operator_group (
  group_id      uuid PRIMARY KEY,
  name          varchar(100) NOT NULL,
  -- denormalized — fast-path for finish-matching audit and per-group display
  candidate_ids uuid[]       NOT NULL DEFAULT '{}'
);

CREATE TABLE operator_group_candidate (
  group_id     uuid NOT NULL REFERENCES operator_group(group_id) ON DELETE CASCADE,
  candidate_id uuid NOT NULL,
  PRIMARY KEY (group_id, candidate_id)
);
CREATE INDEX idx_operator_group_candidate_candidate
  ON operator_group_candidate(candidate_id);

CREATE TABLE operator_group_assignment (
  group_id    uuid NOT NULL REFERENCES operator_group(group_id) ON DELETE CASCADE,
  device_code varchar(50) NOT NULL,
  PRIMARY KEY (group_id, device_code)
);
CREATE INDEX idx_operator_group_assignment_device
  ON operator_group_assignment(device_code);

-- existing race_start_times gets group context:
ALTER TABLE race_start_times
  ADD COLUMN group_id  uuid NULL REFERENCES operator_group(group_id);

-- existing processed_events gets group context:
ALTER TABLE processed_events
  ADD COLUMN group_id  uuid NULL;       -- denormalized from race_start_times.group_id
```

`processed_events.group_id` is set by `BufferProcessingService` at the moment a finish is matched to a heat. Useful for the per-group display ("Group A — 12 finished"). NULL is valid for legacy rows or `UNRESOLVED` finishes (no matched heat).

### 3.3 HHT (Room / SQLite)

```kotlin
@Entity(tableName = "operator_group")
data class OperatorGroupEntity(
    @PrimaryKey val groupId: String,
    val testInstanceId: String,
    val name: String,
    val candidateIdsJson: String,   // JSON array of UUIDs
    val isAssignedToThisDevice: Boolean
)

@Entity(tableName = "operator_group_selection")
data class OperatorGroupSelectionEntity(
    @PrimaryKey val testInstanceId: String,
    val selectedGroupIdsJson: String,           // JSON array
    val resolvedCandidateIdsJson: String,       // JSON Set<String>, cached union
    val updatedAt: Long
)
```

No need to mirror the assignments table — the HHT only cares about its own assignments, which are pre-resolved into `isAssignedToThisDevice` at config-import time.

---

## 4. API surface

### 4.1 Main (admin)

Net-new endpoints scoped to a test instance:

| Method | Path | Purpose |
|---|---|---|
| `GET`  | `/api/test-instances/{id}/operator-groups` | List groups with member count + assignments |
| `POST` | `/api/test-instances/{id}/operator-groups` | Create a group from `FilterSpec` or explicit candidate list |
| `PUT`  | `/api/test-instances/{id}/operator-groups/{gid}` | Rename / re-spec (only when `IsLocked = false`) |
| `DELETE` | `/api/test-instances/{id}/operator-groups/{gid}` | Delete (only when `IsLocked = false`) |
| `POST` | `/api/test-instances/{id}/operator-groups/{gid}/assignments` | Body `{deviceIds: []}` — replaces the assignment list |
| `POST` | `/api/test-instances/{id}/operator-groups/preview` | Body = `FilterSpec`. Returns matching candidate IDs + count without persisting. Used by the admin UI's live preview. |
| `POST` | `/api/test-instances/{id}/operator-groups/{gid}/edit-locked` | Mid-test admin override — re-classifies existing scans/readings against the new group definition. Audit-logged. |

Authorization: every endpoint requires JWT auth and `RequireSection("test-instances:edit")`.

### 4.2 Gate (read-only state for HHT polling)

| Method | Path | Purpose |
|---|---|---|
| `GET`  | `/gate/operator-groups` | List groups + assignments + per-group finish counts (live state for HHT overlap warning) |
| `POST` | `/gate/operator-groups/selection` | HHT registers its current selection (deviceCode + groupIds + override-log entry). Gate stores in a new `operator_group_selection` table for visibility. |

These are added to the existing gate API surface.

### 4.3 HHT — gateway, no public API (it's the consumer)

---

## 5. Config package extensions

`ConfigPackageDto` (in `APTM.Gate.Core/Models/ConfigPackageDto.cs`) gets two new top-level lists:

```csharp
public class ConfigPackageDto
{
    // ... existing fields ...
    public List<ConfigOperatorGroupDto> OperatorGroups { get; set; } = [];
    public List<ConfigOperatorGroupAssignmentDto> OperatorGroupAssignments { get; set; } = [];
    public List<ConfigCustomColumnDto> CustomColumns { get; set; } = [];   // schema for filter UI
}

public class ConfigCandidateDto
{
    // ... existing fields ...
    public Dictionary<string, string?> CustomData { get; set; } = new();   // column name → value
}

public class ConfigOperatorGroupDto
{
    public Guid GroupId { get; set; }
    public string Name { get; set; } = default!;
    public List<Guid> CandidateIds { get; set; } = [];
}

public class ConfigOperatorGroupAssignmentDto
{
    public Guid GroupId { get; set; }
    public Guid DeviceId { get; set; }
    public string DeviceCode { get; set; } = default!;
}

public class ConfigCustomColumnDto
{
    public int ColumnId { get; set; }
    public string Name { get; set; } = default!;            // "Company", "Rank", "Wing"
    public string DisplayLabel { get; set; } = default!;
    public string DataType { get; set; } = default!;        // "TEXT" | "INTEGER" | "DATE" | "BOOLEAN"
    public bool IsFilterable { get; set; }
}
```

`GetConfigPackageQuery` (Main) is extended to project these. `GateConfigService.ApplyConfigAsync` writes the new tables. HHT `ConfigRepository` deserializes them on import.

The new fields are **additive**. Old gates that pull a new config without these fields (because Main hasn't been upgraded yet) get empty lists and continue to work in legacy mode (decision 1).

---

## 6. User flows

### 6.1 Admin flow on Main (test setup)

```
1. Admin opens TestInstance setup page
2. New tab: "Operator Groups"
3. Choose mode:
   a) "Auto from attribute" — pick a column (e.g. Company). System creates one group
      per distinct value, names them "<Column>: <Value>", populates candidates.
   b) "Build with filters" — multi-step wizard: pick predicates, preview count, save.
   c) "Hand pick" — searchable candidate list with checkboxes.
4. Group list view: each row shows name, member count, assigned device(s).
5. "Assign devices" panel: drag a device into a group. Many-to-many.
6. "Validation" widget at top: warns if any candidate is in zero groups (decision 6).
7. When TestInstance.Status flips to ACTIVE, all groups in that test get IsLocked = true
   automatically. Edit becomes admin-override-only.
```

### 6.2 HHT operator flow (start of day)

```
1. Operator opens HHT, taps "Pull Config" on Sync screen.
2. Config arrives, includes OperatorGroups + Assignments.
3. App auto-navigates to OperatorGroupSelectionScreen (new):
   - Lists all groups for this test
   - Pre-checks groups assigned to this device
   - Shows resulting candidate count beneath each option
   - Bottom sheet shows total resolved roster size
4. Operator may override (decision 3) — warned with: "You are deviating from your
   admin-assigned groups. This will be logged. Continue?"
5. "Save" → resolves candidate IDs, persists, navigates to Home.
6. Home screen banner: "Group A + B (200 candidates). Tap to change."
7. Subsequent app launches go straight to Home; selection persists until a new config
   is imported (which clears the selection and re-prompts).
```

### 6.3 Race day — parallel start gates

Concrete trace, expanding on §2 of the previous spec:

```
T-2h (admin)         Creates Groups A..E at Main, assigns HHT-01..HHT-05 (one-to-one).
T-1h (test setup)    TestInstance status flips to ACTIVE; all groups lock.
T-30m (sync)         Each HHT pulls config (via field tablet), pre-selects its group.
T-0   (race start)
  09:00:00  HHT-01 at Group A start gate fires gun for "100m sprint", roster [a1..a5]
            local heat number A-1 (per-group)
            POST /sync/push race_start { heatId=H1, groupId=A, heatNumber=1,
                                          candidates=[a1..a5], gunStartTime=... }
            gate inserts race_start_times row { heatId=H1, group_id=A,
                                                 candidate_ids=[a1..a5], ... }
  09:00:30  HHT-02 fires for Group B, similar payload
            gate row { heatId=H2, group_id=B, candidate_ids=[b1..b5] }
  09:00:45  finish gate UHF reader picks up tag for a3
            BufferProcessingService:
              raceStarts = active heats where candidate_ids @> [a3]
              → matches H1 only (a3 ∈ [a1..a5], a3 ∉ [b1..b5])
              elapsed = 09:00:45 − 09:00:00 = 45s
              processed_events row { candidate=a3, heat_id=H1, group_id=A, duration=45 }
  09:00:50  same for b1, matches H2 (Group B), elapsed = 20s
  09:01:30  HHT-02 cancels Group B's heat (false start)
            POST /sync/push race_cancel { heatId=H2 }
            void query: WHERE heat_id = H2  -- (NOT WHERE heat_number = 1)
            ✓ Group A's finishes are not touched
  09:02:00  finish gate reads a stray tag from a Group C candidate who's still walking
            around (Group C race hasn't started yet)
            no active heat's candidate_ids contains this tag
            with the new code: processed_events row { status='UNRESOLVED',
                                                      heat_id=NULL, group_id=NULL }
            surfaced in admin UI for manual review (NOT silently charged to H1 or H2)
```

The bug fixes in phase 7 are what make this trace work as written.

### 6.4 End-of-day cleanup

Existing flow (`/gate/race-data/clear`) continues to apply. The new tables (`operator_group`, `operator_group_candidate`, `operator_group_assignment`) are **configuration**, not per-race data — they are **not** wiped by the End Test flow. They're rewritten by the next config push.

---

## 7. Phased delivery plan

Each phase is independently shippable — `main` should be deployable after every phase.

### Phase 0 — Scaffolding (½ day)

**Main:**
- EF migration adds `OperatorGroups`, `OperatorGroupCandidates`, `OperatorGroupAssignments`.
- Domain entities + EF configurations.
- No API, no UI yet.

**Gate:**
- Migration adds `operator_group`, `operator_group_candidate`, `operator_group_assignment`, `race_start_times.group_id`, `processed_events.group_id`.
- Entities + Fluent configs.
- DbContext sets.

**HHT:**
- Room migration `7→8`: adds `operator_group`, `operator_group_selection` tables. `candidates` table gets `customDataJson` column (default `{}`).
- Entities + DAOs.
- No UI yet.

**Field app:**
- No changes (config-pass-through already round-trips JSON).

**Acceptance**: All builds green. Migrations apply cleanly on existing dev DBs.

### Phase 1 — Custom data through the config package (½ day)

**Main:**
- `ConfigCandidateDto.CustomData` populated by `GetConfigPackageQuery` (joins `CandidateCustomData` + `ReportColumn`).
- `ConfigPackageDto.CustomColumns` populated.

**Gate:**
- `GateConfigService.ApplyConfigAsync` ignores the new fields gracefully (legacy gates may receive new payloads).

**HHT:**
- `ConfigRepository.importConfig` stores `customDataJson` per candidate.
- Stores `CustomColumns` schema in a new tiny entity `CustomColumnEntity`.

**Acceptance**: Pull config on HHT, query Room: each candidate row has populated `customDataJson` if Main has custom data.

### Phase 2 — Main admin: groups + assignments (1.5 days)

**Main:**
- `GET/POST/PUT/DELETE /api/test-instances/{id}/operator-groups` endpoints.
- `POST /api/test-instances/{id}/operator-groups/preview` endpoint (filter resolution without persisting).
- Group lock-on-test-start: `TestInstanceService.StartAsync` sets `IsLocked = true` on all groups.
- Admin Razor / web UI: a new tab on the test setup page (existing pattern).

**HHT, Gate:**
- No changes.

**Acceptance**: Admin can create groups, see them, assign devices, see "X candidates not in any group" warning. Cannot edit groups after test starts (without override flag).

### Phase 3 — Config package extension for groups (1 day)

**Main:**
- `GetConfigPackageQuery` projects `OperatorGroups`, `OperatorGroupAssignments`, `CustomColumns`.
- DTO additions.

**Gate:**
- `GateConfigService.ApplyConfigAsync` writes the new tables (TRUNCATE + insert pattern, same as existing config tables).
- `processed_events.group_id` not yet populated (Phase 6).

**HHT:**
- `ConfigRepository.importConfig` writes `OperatorGroupEntity` rows, computes `isAssignedToThisDevice` from `OperatorGroupAssignments WHERE deviceCode == myDeviceCode`.

**Acceptance**: Push a config from Main with groups + assignments. Verify gate has `operator_group` rows; HHT has `operator_group` rows with correct `isAssignedToThisDevice` flag.

### Phase 4 — HHT operator selection screen (1 day)

**HHT:**
- New screen `OperatorGroupSelectionScreen` + `OperatorGroupSelectionViewModel`.
- New domain repo `OperatorGroupRepository` exposing `selectedGroupIds`, `resolvedCandidateIds`, `isInScope(candidateId)`.
- Insert into nav graph between `test_selection` and `home`. Skip if a selection already exists for the active test.
- Home screen banner showing active group + Edit shortcut.

**Acceptance**: Pull config; HHT auto-navigates to selection screen with assigned groups pre-checked. Save → home banner shows the right roster size.

### Phase 5 — HHT scope filter wiring (1.5 days)

**HHT:**
- `TagClassifier` adds `OutOfScope(candidate)` outcome.
- All scan ViewModels handle `OutOfScope`: silent drop by default, opt-in haptic via Settings.
- All candidate-list queries (attendance roster, heat builder candidate picker, ground activity scan) gated by `OperatorGroupRepository.scopedCandidateIds`.
- Counters in UI become group-relative.
- Sync queue: only attendance/race/ground scans for in-scope candidates are enqueued (defence in depth).
- Existing `isOutOfBatch = false` hardcode in `AttendanceRepository.kt:78` removed; field deprecated.

**Acceptance**: Test with two trainers, disjoint groups. Trainer-1 walks past Trainer-2's candidate — no scan recorded on HHT-01, no entry in HHT-01's local DB, no sync queue entry, no haptic. Trainer-2 still scans normally.

### Phase 6 — Gate race awareness (1 day)

**Gate:**
- `SyncHubService.PushAsync` accepts `groupId` in race_start payload, persists on `race_start_times.group_id`.
- `BufferProcessingService.ProcessTagAsync` populates `processed_events.group_id` from the matched `race_start_times.group_id`.
- Display SSE includes per-group counters (extend existing display data DTO).
- New endpoint `GET /gate/operator-groups` for live state queries from HHTs.
- New endpoint `POST /gate/operator-groups/selection` for HHTs to register their active selection.

**HHT:**
- `RaceRepository.fireGun` / `pushRaceStart` includes `groupId` in payload.
- Optional: `OperatorGroupSelectionScreen` queries gate for other devices' selections to warn on overlap.

**Acceptance**: Run the §6.3 trace end-to-end on a staging test. Verify each finish has correct `group_id` on `processed_events` and correct elapsed time.

### Phase 7 — Critical race-data fixes (½ day, **required for safety**)

These are independent of grouping but must ship in this release.

**Gate:**
- `BufferProcessingService.cs:295`: drop the `?? raceStarts[0]` fallback. Unrostered finishes become `processed_events { status='UNRESOLVED', heat_id=NULL, group_id=NULL }`.
- `BufferProcessingService.cs:411`: heat-completion check switches from `WHERE heat_number = X` to `WHERE heat_id = Y`.
- `SyncHubService.cs:107`: race_cancel void query → `WHERE heat_id = ?`.
- `SyncHubService.cs:139`: heat_candidate_remove void query → `WHERE heat_id = ?`.
- `SyncHubService.cs:21–205`: wrap in try/catch `DbUpdateException`, return `SyncPushResult.Duplicate(clientRecordId)` on unique-index violation instead of bubbling 500.
- `BufferProcessingService.cs:59–63, 149–153`: claim PENDING rows with `FOR UPDATE SKIP LOCKED`.

**Main:**
- `AttendanceService.ProcessScanBatchAsync` and `GroundActivityService.ProcessReadingsAsync`: wrap in serializable transactions and catch `DbUpdateException` from unique-index races.

**Acceptance**: Stress test: 5 simulated HHTs concurrently push attendance batches for overlapping candidate sets. Zero 500s, zero stuck queue rows.

### Phase 8 — Coordination + polish (1 day)

**Gate:**
- HHT polling integration: `OperatorGroupSelectionScreen` shows "*N of these candidates are already claimed by HHT-XX*" warnings using `GET /gate/operator-groups`.

**Main:**
- Audit warning rows: when an `AttendanceScan.DeviceId` doesn't match any `OperatorGroupAssignment.DeviceId` for the candidate's group, write a `scan_outside_scope` flag for admin review.
- Mid-test edit endpoint with re-classification semantics.

**HHT:**
- Settings toggle: "Warn on out-of-scope scans".

**Acceptance**: Operator UX feedback round-trip. Stage with real trainers if possible.

---

## 8. Testing strategy

### 8.1 Unit tests

- **Filter resolver** (Main): given a `FilterSpec` and a candidate roster, produces the expected ID set. Covers AND/OR semantics, empty values, nonexistent column, manualAdditions/Exclusions.
- **`OperatorGroupRepository.isInScope`** (HHT): correct true/false for in-group, out-of-group, no-group-selected, candidate-with-no-group cases.
- **`TagClassifier`** (HHT): all four outcomes (`Invalid | OutOfBatch | OutOfScope | Known`) covered.
- **`SyncHubService.PushAsync` dedup** (Gate): concurrent same-clientRecordId returns `Duplicate` not 500.
- **`BufferProcessingService` heat matching** (Gate): unrostered candidate → `UNRESOLVED`, not `raceStarts[0]`.

### 8.2 Integration tests

- **Round-trip config**: create groups on Main → push to gate via test harness → push to HHT → verify Room + PG state match.
- **Race trace**: fire two parallel guns, simulate finish reads in interleaved order, verify per-group elapsed times.
- **Cancel scoping**: cancel Group A's heat 1, verify Group B's heat 1 finishes are untouched.

### 8.3 Manual / staging

- Two-trainer test on staging: Group A (50 candidates) + Group B (50 candidates), parallel attendance + race + ground activity. Verify no cross-pollution, verify race timing, verify HHT-A's "edit group" UI surfaces Group B as "claimed by HHT-02" if Phase 8 is live.

### 8.4 Stress

- 5 simulated HHTs (`tools/simulator/aptm_simulate.py` extended) firing concurrently. Goal: 30 minutes of mixed traffic, zero 500s on Main, zero stuck `sync_queue` rows.

---

## 9. Migration & rollout

### 9.1 Schema migration

- All new tables / columns are **additive**. No destructive changes.
- Existing tests in production continue to work — `OperatorGroups` empty for them, decision 1's "no group = legacy" path keeps current behaviour.
- Run order: Phase 0 migrations on Main + Gate + HHT in parallel during a maintenance window. Devices that haven't upgraded HHT continue to receive new config packages unchanged (extra fields are ignored by JSON deserializer).

### 9.2 Backward-compat matrix

| Main version | Gate version | HHT version | Behaviour |
|---|---|---|---|
| Old | Old | Old | Current behaviour, no groups |
| **New** | Old | Old | New admin UI works but groups don't propagate. Gate / HHT see no change. |
| **New** | **New** | Old | Groups propagate to gate (used for race awareness in phase 6). HHT shows all candidates as today. |
| **New** | **New** | **New** | Full feature. |
| New | Old | New | HHT enforces scope locally. Gate is unaware → race timing falls back to today's broken multi-heat behaviour. **Avoid this combination** — upgrade gate first, HHT second. |

Documented upgrade order: **Main → Gate → HHT.**

### 9.3 Data migration

- One-time SQL on Main if existing tests want retroactive groups: a script that, given a `TestInstanceId` and a column name, creates one group per distinct value and populates `OperatorGroupCandidates`. Optional, run on demand.
- No HHT data migration — clear local config and re-pull.

### 9.4 Feature flag

- Server-side `Features:OperatorGroups` boolean in `appsettings.json`, defaulting to `true` once tested. Lets us disable the admin UI surface in case of late-breaking bugs without rolling back the schema.

---

## 10. Edge cases (and how the design handles each)

| Case | Behaviour |
|---|---|
| Candidate added to roster after groups created | Joins `OperatorGroupCandidates` only if `FilterSpec` matches. Hand-picked groups don't auto-include. Surfaced in admin UI with "X new candidates not in any group". |
| Device assigned to two groups, the groups overlap on candidates | Allowed. HHT shows union; no double-counting because `resolvedCandidateIds` is a Set. |
| Operator deviates and selects a group not assigned to them | Allowed (decision 3); writes `operator_group_override_log` row on next push. |
| Mid-test config refresh (admin pushes new config) | HHT clears `operator_group_selection`, re-prompts on next launch. |
| Test instance has no groups at all | Decision 1: legacy "all candidates" mode. No selection screen, no scope filter. |
| Candidate is in zero groups | Decision 6: visible to all devices for that test. Admin warned at setup. |
| HHT loses connectivity mid-selection | Selection is local-only until "Save"; nothing is lost. Gate-side coordination warning becomes stale (worst case operator picks an already-claimed group, gets the warning post-facto on next pull). |
| `processed_events.group_id` ends up NULL for a finish | Either an `UNRESOLVED` row (no roster matched) or a legacy heat with no group context. UI distinguishes via `status` column. |

---

## 11. Open issues / out-of-scope

These are deliberately **not** addressed in this design and are tracked for follow-up:

- **Real-time HHT-to-HHT visibility during race operations** beyond the polling in §6.2. SignalR group per test would be the natural fit but adds a hard dependency on Main being reachable mid-race. Polling the gate every few seconds is the pragmatic alternative.
- **Group ownership transfer mid-test** (e.g. trainer's HHT battery dies, hand off to spare). Possible follow-up flow on the field tablet.
- **Per-group reports / exports** on Main. Existing report engine works on the full roster; per-group filtering is straightforward to add but not in this design.
- **Scoring matrix per group** — different fitness standards for different groups. Not in scope; current scoring matrix is per `CandidateType + Gender + AgeBracket`.

---

## 12. Sign-off checklist

Before kick-off, the following stakeholders should review:

- [ ] Backend tech lead — DB design, migration safety
- [ ] Mobile tech lead — Room migration + scope-filter approach
- [ ] Gate engineer — race timing logic + bug fixes
- [ ] Operations lead — admin UX for group creation, test-day workflow
- [ ] QA lead — testing strategy

Once all six decisions in §2 are confirmed and at least three of the above review the doc, implementation starts at Phase 0.

---

**Appendix A — file-level change list**

For grep-friendly targeting during implementation. Not exhaustive but covers the load-bearing changes.

| Component | File | Change |
|---|---|---|
| Main | `src/APTM.Domain/Entities/OperatorGroup.cs` | new |
| Main | `src/APTM.Domain/Entities/OperatorGroupCandidate.cs` | new |
| Main | `src/APTM.Domain/Entities/OperatorGroupAssignment.cs` | new |
| Main | `src/APTM.Infrastructure/Persistence/Migrations/*_AddOperatorGroups.cs` | new |
| Main | `src/APTM.Application/Commands/OperatorGroups/*.cs` | new (Create / Update / Delete / Assign / PreviewFilter) |
| Main | `src/APTM.Application/Queries/Sync/GetConfigPackageQuery.cs` | extended to project new fields |
| Main | `src/APTM.Api/Controllers/OperatorGroupsController.cs` | new |
| Main | `src/APTM.Infrastructure/Services/AttendanceService.cs` | wrap in transaction, catch DbUpdateException |
| Main | `src/APTM.Infrastructure/Services/GroundActivityService.cs` | wrap in transaction, catch DbUpdateException |
| Gate | `src/APTM.Gate.Core/Models/ConfigPackageDto.cs` | new DTO fields |
| Gate | `src/APTM.Gate.Infrastructure/Entities/OperatorGroup*.cs` | new |
| Gate | `src/APTM.Gate.Infrastructure/Persistence/Migrations/*_AddOperatorGroups.cs` | new |
| Gate | `src/APTM.Gate.Infrastructure/Services/GateConfigService.cs` | write new tables |
| Gate | `src/APTM.Gate.Infrastructure/Services/SyncHubService.cs` | accept groupId, switch heat queries to heat_id, catch DbUpdateException |
| Gate | `src/APTM.Gate.Infrastructure/Services/BufferProcessingService.cs` | drop fallback, switch to heat_id, FOR UPDATE SKIP LOCKED |
| Gate | `src/APTM.Gate.Api/Endpoints/OperatorGroupEndpoints.cs` | new |
| HHT | `app/src/main/java/com/aptm/hht/data/local/entity/OperatorGroupEntity.kt` | new |
| HHT | `app/src/main/java/com/aptm/hht/data/local/entity/OperatorGroupSelectionEntity.kt` | new |
| HHT | `app/src/main/java/com/aptm/hht/data/local/HhtDatabase.kt` | version 7→8 + migration |
| HHT | `app/src/main/java/com/aptm/hht/data/repository/OperatorGroupRepository.kt` | new |
| HHT | `app/src/main/java/com/aptm/hht/domain/usecase/tag/TagClassifier.kt` | add `OutOfScope` outcome |
| HHT | `app/src/main/java/com/aptm/hht/data/repository/AttendanceRepository.kt` | scope filter, drop `isOutOfBatch = false` hardcode |
| HHT | `app/src/main/java/com/aptm/hht/data/repository/RaceRepository.kt` | include groupId in race_start payload |
| HHT | `app/src/main/java/com/aptm/hht/data/repository/GroundActivityRepository.kt` | scope filter |
| HHT | `app/src/main/java/com/aptm/hht/ui/operator-group/OperatorGroupSelectionScreen.kt` | new |
| HHT | `app/src/main/java/com/aptm/hht/ui/navigation/AppNavigation.kt` | new route |
| Field | (no changes) | — |

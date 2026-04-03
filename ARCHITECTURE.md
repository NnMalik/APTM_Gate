# APTM — Architecture & Database Structure

> **Army Physical Test Management System**  
> A .NET 10 ASP.NET Core Web API for managing physical fitness tests, UHF RFID tag tracking, candidate management, race timing, attendance, and result generation.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Repository Structure](#2-repository-structure)
3. [Application Architecture](#3-application-architecture)
4. [Domain Layer — Entities](#4-domain-layer--entities)
5. [Database Structure](#5-database-structure)
6. [EF Core Configurations](#6-ef-core-configurations)
7. [Migration History](#7-migration-history)
8. [API Layer — Controllers & Endpoints](#8-api-layer--controllers--endpoints)
9. [Application Layer — CQRS](#9-application-layer--cqrs)
10. [Background Workers](#10-background-workers)
11. [SignalR Hubs](#11-signalr-hubs)
12. [Interfaces (Application Layer)](#12-interfaces-application-layer)
13. [Infrastructure Services](#13-infrastructure-services)
14. [Dependency Injection](#14-dependency-injection)
15. [Configuration](#15-configuration)
16. [Domain Enums](#16-domain-enums)
17. [Key Architecture Patterns](#17-key-architecture-patterns)
18. [Summary Statistics](#18-summary-statistics)

---

## 1. Executive Summary

APTM is a comprehensive backend API for managing army physical fitness testing events. Its core capabilities include:

| Capability | Description |
|---|---|
| **Test Management** | Create and run structured physical tests with multiple scored events |
| **Candidate Management** | CRUD for candidates, batches, custom data, soft-delete |
| **UHF RFID Tracking** | Real-time tag ingestion via TCP (fixed readers) and REST (handhelds) |
| **Race Timing** | Heat management, gun start, finish reads, checkpoint reads |
| **Attendance** | Session-based attendance with tag scanning and manual overrides |
| **Result Generation** | Immutable, audited results with conflict detection and data-loss guards |
| **Real-Time Dashboard** | SignalR push notifications for live headcount and scan events |
| **Multi-Device Sync** | REST-based sync for handheld devices, TCP for fixed UHF readers |

---

## 2. Repository Structure

```
APTM_Claude/
├── APTM.slnx                          ← Solution file
├── CLAUDE.md                          ← Developer notes
├── openapi.json                       ← OpenAPI spec
├── seed_bpet_ppt.sql                  ← Seed data SQL
└── src/
    ├── APTM.Api/                      ← Executable — controllers, hubs, middleware
    │   ├── Controllers/               ← 28 API controllers
    │   ├── Hubs/                      ← 2 SignalR hubs
    │   ├── Middleware/                ← Auth, error handling
    │   ├── Services/                  ← CurrentUserService, SignalR notifier
    │   └── Program.cs / appsettings
    ├── APTM.Application/              ← Class library — CQRS, interfaces, validators
    │   ├── Commands/                  ← 74+ MediatR commands
    │   ├── Queries/                   ← 64+ MediatR queries
    │   ├── Interfaces/                ← 21 service interfaces
    │   ├── Behaviours/                ← Validation + Logging pipeline behaviors
    │   └── DependencyInjection.cs
    ├── APTM.Domain/                   ← Class library — entities, enums (zero deps)
    │   ├── Entities/                  ← 47 entity classes + BaseEntity + IAuditableEntity
    │   └── Enums/                     ← 25 enum types
    ├── APTM.Infrastructure/           ← Class library — persistence, services
    │   ├── Persistence/
    │   │   ├── AptmDbContext.cs       ← 47 DbSets
    │   │   ├── Configurations/        ← 47 Fluent API configs
    │   │   ├── Migrations/            ← 22 EF Core migrations
    │   │   └── Interceptors/          ← AuditInterceptor
    │   ├── Services/                  ← 19 service implementations
    │   └── DependencyInjection.cs
    └── APTM.Workers/                  ← Class library — BackgroundService workers
        ├── ReaderWorkerManager.cs
        ├── BufferProcessorWorker.cs
        ├── SyncIngestWorker.cs
        ├── ResultGenerationWorker.cs
        ├── CleanupWorker.cs
        └── DependencyInjection.cs
```

---

## 3. Application Architecture

### Dependency Graph

```
Domain (no external deps)
    ↑
Application ──→ Domain
    ↑
Infrastructure ──→ Domain, Application
    ↑
Workers ──→ Domain, Application, Infrastructure
    ↑
Api ──→ Application, Domain, Infrastructure, Workers
```

### Architectural Pattern: Clean Architecture

```
┌────────────────────────────────────────────────────────────┐
│  API Layer (APTM.Api)                                      │
│  Controllers · SignalR Hubs · Middleware · Startup         │
└───────────────────────────┬────────────────────────────────┘
                            │ MediatR (IRequest/IRequestHandler)
┌───────────────────────────▼────────────────────────────────┐
│  Application Layer (APTM.Application)                      │
│  Commands · Queries · Validators · Interfaces · Behaviors  │
└──────────┬────────────────────────────────┬────────────────┘
           │ Implements interfaces           │ Reads/writes
┌──────────▼────────────────┐  ┌────────────▼───────────────┐
│  Infrastructure Layer      │  │  Workers Layer             │
│  EF Core · Services        │  │  BackgroundService hosts   │
│  Migrations · Interceptors │  │  TCP readers · Sync        │
└──────────┬────────────────┘  └────────────┬───────────────┘
           │                                │
┌──────────▼────────────────────────────────▼───────────────┐
│  Domain Layer (APTM.Domain)                               │
│  Entities · Enums · Value Objects · BaseEntity            │
└───────────────────────────────────────────────────────────┘
```

### Request Pipeline

```
HTTP Request
    → JWT / DeviceToken Authentication Middleware
    → Authorization (Role/Section permissions)
    → Controller Action
        → MediatR.Send(Command | Query)
            → ValidationBehaviour (FluentValidation)
            → LoggingBehaviour
            → Handler
                → IAptmDbContext (EF Core)
                → Business Services (via interfaces)
                → AuditInterceptor (auto-audit on SaveChanges)
    → Response
```

---

## 4. Domain Layer — Entities

All entities inherit from `BaseEntity<TKey>` which provides the primary key property.

### 4.1 Authentication & Authorization (3 entities)

#### `User`
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `RoleId` | `int` | FK → Role |
| `SiteId` | `Guid?` | FK → Site (nullable) |
| `Username` | `string` | Unique, max 100 |
| `PasswordHash` | `string` | Bcrypt hash |
| `FirstName` | `string` | max 100 |
| `LastName` | `string` | max 100 |
| `ServiceNumber` | `string?` | Optional |
| `IsActive` | `bool` | |
| `CreatedAt` | `DateTime` | |
| `LastLoginAt` | `DateTime?` | |
| **Nav** | `Role` | |
| **Nav** | `Site?` | |

#### `Role`
| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `Name` | `string` | Unique, max 100 |
| `Description` | `string?` | |
| `IsSystem` | `bool` | Prevents deletion |
| **Nav** | `ICollection<User>` | |
| **Nav** | `ICollection<SectionPermission>` | |

#### `SectionPermission`
| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `RoleId` | `int` | FK → Role |
| `SectionName` | `string` | Route section name |
| `CanRead` | `bool` | |
| `CanWrite` | `bool` | |
| `CanExecute` | `bool` | |
| **Nav** | `Role` | |

---

### 4.2 Site & Device Management (7 entities)

#### `Site`
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `Name` | `string` | max 100 |
| `Code` | `string` | Unique, max 20 |
| `Address` | `string?` | |
| `IsActive` | `bool` | |
| `TagRangeConfigured` | `bool` | |
| `CreatedAt` | `DateTime` | |
| `CreatedBy` | `Guid` | FK → User |
| **Nav** | `User CreatedByUser` | |
| **Nav** | `SiteTagRange? TagRange` | |
| **Nav** | `ICollection<User>` | |

#### `SiteTagRange`
| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `SiteId` | `Guid` | FK → Site (unique) |
| `TagPrefix` | `string` | EPC prefix for site |
| `RangeStart` | `int` | |
| `RangeEnd` | `int` | |
| **Nav** | `Site` | |

#### `Device`
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `DeviceCode` | `string` | Unique identifier |
| `DeviceTypeId` | `int` | FK → DeviceType |
| `ApiToken` | `string` | Hashed token |
| `ConnectionMethod` | `ConnectionMethod` | TCP or REST |
| `RequiresSyncBeforeReuse` | `bool` | |
| `IsActive` | `bool` | |
| `LastSeenAt` | `DateTime?` | |
| `LastClockOffsetMs` | `int?` | Clock sync offset |
| `RegisteredAt` | `DateTime` | |
| `RegisteredBy` | `Guid` | FK → User |
| **Nav** | `DeviceType` | |
| **Nav** | `User RegisteredByUser` | |

#### `DeviceType`
| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `Name` | `string` | e.g. "UHF Handheld" |
| `Description` | `string?` | |
| **Nav** | `ICollection<Device>` | |

#### `DeviceIncident`
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `DeviceId` | `Guid` | FK → Device |
| `TestInstanceId` | `Guid` | FK → TestInstance |
| `TestInstanceDeviceId` | `Guid?` | FK → TestInstanceDevice |
| `IncidentType` | `IncidentType` | |
| `Description` | `string?` | |
| `ReportedAt` | `DateTime` | |
| `ReportedBy` | `Guid` | FK → User |
| **Nav** | `Device`, `TestInstance`, `User` | |

---

### 4.3 Candidate Management (4 entities)

#### `Candidate`
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `ServiceNumber` | `string` | Unique, max 50 |
| `Name` | `string` | max 200 |
| `Gender` | `string` | "MALE" or "FEMALE" |
| `CandidateTypeId` | `int` | FK → CandidateType |
| `DateOfBirth` | `DateOnly` | For age-based scoring |
| `SiteId` | `Guid` | FK → Site |
| `IsDeleted` | `bool` | Soft-delete flag |
| `ImportBatchId` | `Guid?` | Correlation to Excel import |
| **Nav** | `CandidateType` | |
| **Nav** | `Site` | |
| **Nav** | `ICollection<CandidateCustomData>` | |
| **Nav** | `ICollection<BatchCandidate>` | |
| **Nav** | `ICollection<TagAssignment>` | |

> **Soft Delete:** Global query filter `WHERE IsDeleted = 0` applied in EF configuration.

#### `CandidateType`
| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `Name` | `string` | Unique, max 100 |
| `Description` | `string?` | |
| `IsActive` | `bool` | |

#### `CandidateCustomData`
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `CandidateId` | `Guid` | FK → Candidate |
| `Key` | `string` | max 100 |
| `Value` | `string` | max 500 |

#### `Batch`
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `Name` | `string` | max 200 |
| `Description` | `string?` | |
| `SiteId` | `Guid` | FK → Site |
| `DataSnapshotVersion` | `int` | Increments on changes |
| `Status` | `BatchStatus` | DRAFT, ACTIVE, LOCKED, ARCHIVED |
| **Nav** | `Site` | |
| **Nav** | `ICollection<BatchCandidate>` | |
| **Nav** | `ICollection<TagAssignment>` | |

#### `BatchCandidate` (Junction)
| Property | Type | Notes |
|---|---|---|
| `Id` | `long` | BIGINT PK (performance) |
| `BatchId` | `Guid` | FK → Batch |
| `CandidateId` | `Guid` | FK → Candidate |
| **Nav** | `Batch`, `Candidate` | |

#### `TagAssignment`
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `BatchId` | `Guid` | FK → Batch |
| `CandidateId` | `Guid` | FK → Candidate |
| `TagEPC` | `string` | UHF RFID EPC code |
| `AssignedAt` | `DateTime` | |
| `AssignedBy` | `Guid` | FK → User |

---

### 4.4 Test Configuration (5 entities)

#### `TestType`
| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `Name` | `string` | Unique, max 100 |
| `Description` | `string?` | |
| `IsActive` | `bool` | |
| `CreatedAt` | `DateTime` | |
| `CreatedBy` | `Guid` | FK → User |
| **Nav** | `ICollection<TestTypeEvent>` | |
| **Nav** | `ICollection<ReportTemplate>` | |

#### `Event`
| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `Name` | `string` | Unique, max 100 |
| `Description` | `string?` | |
| `EventType` | `EventType` | RACE, STATUS, POINT_BASED |
| `MeasurementUnit` | `MeasurementUnit` | |
| `IsActive` | `bool` | |
| **Nav** | `ICollection<TestTypeEvent>` | |

#### `TestTypeEvent` (Ordered assignment of Event to TestType)
| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `TestTypeId` | `int` | FK → TestType |
| `EventId` | `int` | FK → Event |
| `Sequence` | `int` | Execution order |
| `IsRequired` | `bool` | |
| `ScoringTypeId` | `int` | FK → ScoringType |
| **Nav** | `TestType`, `Event`, `ScoringType` | |
| **Nav** | `ICollection<ScoringMatrix>` | |

#### `TestInstance` (Scheduled run of a TestType for a Batch at a Site)
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `SiteId` | `Guid` | FK → Site |
| `TestTypeId` | `int` | FK → TestType |
| `BatchId` | `Guid` | FK → Batch |
| `Name` | `string` | max 200 |
| `Status` | `TestInstanceStatus` | DRAFT, ACTIVE, COMPLETED, CANCELLED |
| `ScheduledDate` | `DateOnly` | |
| `AttendanceSessionId` | `Guid?` | FK → AttendanceSession |
| `IsArchived` | `bool` | |
| `ArchivedAt` | `DateTime?` | |
| `ArchivedBy` | `Guid?` | FK → User |
| `CreatedAt` | `DateTime` | |
| `CreatedBy` | `Guid` | FK → User |
| `LatestGenerationRunId` | `Guid?` | Correlation key for latest results |
| **Nav** | `Site`, `TestType`, `Batch`, `AttendanceSession` | |
| **Nav** | `ICollection<TestInstanceEvent>` | |
| **Nav** | `ICollection<TestInstanceDevice>` | |
| **Nav** | `ICollection<CandidateResult>` | |
| **Nav** | `ICollection<UHFReaderConfig>` | |

---

### 4.5 Test Instance Execution (3 entities)

#### `TestInstanceEvent`
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `TestInstanceId` | `Guid` | FK → TestInstance |
| `TestTypeEventId` | `int` | FK → TestTypeEvent |
| `Status` | `TestInstanceEventStatus` | PENDING, IN_PROGRESS, COMPLETED, CANCELLED |
| `CompletedAt` | `DateTime?` | |
| **Nav** | `TestInstance`, `TestTypeEvent` | |
| **Nav** | `ICollection<RaceSession>` | |

#### `TestInstanceDevice` (Per-device sync tracking)
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `TestInstanceId` | `Guid` | FK → TestInstance |
| `DeviceId` | `Guid` | FK → Device |
| `SyncStatus` | `SyncStatus` | |
| `DataSnapshotVersion` | `int` | Matches Batch version at pull time |
| `TotalRecordsSynced` | `int` | |
| `DeviceTotalLocalRecords` | `int?` | Reported by device |
| `IsDataRecoverable` | `bool` | |
| `PulledAt` | `DateTime` | |
| `PulledBy` | `Guid` | FK → User |

---

### 4.6 Race Event Execution (4 entities)

#### `RaceSession` (Single heat)
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `TestInstanceEventId` | `Guid` | FK → TestInstanceEvent |
| `HeatNumber` | `int` | |
| `GunStartTime` | `DateTime?` | Set when gun fires |
| `Status` | `RaceSessionStatus` | PENDING, RUNNING, FINISHED |
| `StartedBy` | `Guid?` | FK → User |
| **Nav** | `TestInstanceEvent`, `User?` | |
| **Nav** | `ICollection<RaceSessionCandidate>` | |
| **Nav** | `ICollection<RaceFinishRead>` | |
| **Nav** | `ICollection<RaceCheckpointRead>` | |

#### `RaceSessionCandidate`
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `RaceSessionId` | `Guid` | FK → RaceSession |
| `CandidateId` | `Guid` | FK → Candidate |
| `AttendanceStatus` | `RaceAttendanceStatus` | PRESENT, ABSENT, DNS |
| `AttendanceSource` | `RaceAttendanceSource` | HANDHELD_SYNC, UHF_FIXED |

#### `RaceFinishRead` (Finish line tag read)
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `RaceSessionId` | `Guid` | FK → RaceSession |
| `CandidateId` | `Guid` | FK → Candidate |
| `ReadTime` | `DateTime` | Absolute timestamp |
| `DurationSeconds` | `decimal` | Calculated from GunStartTime |
| `IsDuplicate` | `bool` | First Read Rule applied |
| `ReadBy` | `Guid` | FK → User / Device |

#### `RaceCheckpointRead` (Intermediate checkpoint)
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `RaceSessionId` | `Guid` | FK → RaceSession |
| `CandidateId` | `Guid` | FK → Candidate |
| `CheckpointSequence` | `int` | Route order |
| `ReadTime` | `DateTime` | |
| `DurationSeconds` | `decimal` | |
| `IsDuplicate` | `bool` | |
| `ReadBy` | `Guid` | FK → User / Device |

---

### 4.7 Attendance Management (3 entities)

#### `AttendanceSession`
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `TestInstanceId` | `Guid` | FK → TestInstance |
| `Status` | `AttendanceSessionStatus` | OPEN, CLOSED, REOPENED |
| `OpenedAt` | `DateTime` | |
| `OpenedBy` | `Guid` | FK → User |
| `TotalInBatch` | `int` | Snapshot at session open |
| `TotalPresent` | `int` | Running counter |
| `TotalAbsent` | `int` | Running counter |
| `TotalOutOfBatch` | `int` | Running counter |
| **Nav** | `TestInstance`, `User` | |
| **Nav** | `ICollection<AttendanceScan>` | |
| **Nav** | `ICollection<AttendanceCandidateStatus>` | |

#### `AttendanceScan` (Individual tag scan event)
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `AttendanceSessionId` | `Guid` | FK → AttendanceSession |
| `TagEPC` | `string` | Raw EPC |
| `CandidateId` | `Guid?` | Resolved from TagAssignment (nullable = unknown) |
| `IsOutOfBatch` | `bool` | Candidate exists but not in batch |
| `ClientRecordId` | `string?` | Idempotency key from device |
| `ScannedAt` | `DateTime` | |
| `ScannedBy` | `Guid` | FK → User |

#### `AttendanceCandidateStatus`
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `AttendanceSessionId` | `Guid` | FK → AttendanceSession |
| `CandidateId` | `Guid` | FK → Candidate |
| `Status` | `AttendanceCandidateStatusType` | NOT_SCANNED, PRESENT, ABSENT |
| `SetBy` | `AttendanceStatusSetBy` | AUTO (scan) or MANUAL |
| `SetAt` | `DateTime` | |
| `SetByUserId` | `Guid` | FK → User |

---

### 4.8 Scoring Configuration (3 entities)

#### `ScoringType` (e.g. "GRADE", "PASS_FAIL")
| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `Name` | `string` | Unique, max 100 |
| `Description` | `string?` | |
| **Nav** | `ICollection<ScoringStatus>` | |

#### `ScoringStatus` (Discrete outcome per ScoringType)
| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `ScoringTypeId` | `int` | FK → ScoringType |
| `StatusCode` | `string` | Unique, max 30 (e.g. "PASS", "FAIL", "GOLD") |
| `StatusLabel` | `string` | max 100 |
| `Sequence` | `int` | Display order |
| `IsPassingStatus` | `bool` | |
| `IsActive` | `bool` | |
| **Nav** | `ScoringType` | |

#### `ScoringMatrix` (Range-to-Status mapping)
| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `TestTypeEventId` | `int` | FK → TestTypeEvent |
| `CandidateTypeId` | `int` | FK → CandidateType |
| `Gender` | `string` | "M", "F", or "O" |
| `AgeMin` | `int?` | Inclusive lower bound |
| `AgeMax` | `int?` | Inclusive upper bound (null = "and above") |
| `MinValue` | `decimal` | Score range minimum |
| `MaxValue` | `decimal` | Score range maximum |
| `ScoringStatusId` | `int` | FK → ScoringStatus |
| `Points` | `decimal?` | Null for non-point-based events |
| `IsActive` | `bool` | |

---

### 4.9 Ground Activity (2 entities)

#### `GroundActivityReading` (Trainer-recorded result)
| Property | Type | Notes |
|---|---|---|
| `Id` | `long` | BIGINT PK |
| `TestInstanceEventId` | `Guid` | FK → TestInstanceEvent |
| `CandidateId` | `Guid` | FK → Candidate |
| `ScoringStatusId` | `int` | FK → ScoringStatus |
| `ClientRecordId` | `string?` | Idempotency key |
| `RecordedAt` | `DateTime` | |
| `RecordedBy` | `Guid` | FK → User |
| `ConflictStatus` | `ConflictStatus` | NONE, CONFLICT, RESOLVED_KEPT, RESOLVED_DISCARDED |

#### `GroundActivityConflict` (Cross-device conflict)
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `TestInstanceEventId` | `Guid` | FK → TestInstanceEvent |
| `CandidateId` | `Guid` | FK → Candidate |
| `ReadingA_Id` | `long` | FK → GroundActivityReading |
| `ReadingB_Id` | `long` | FK → GroundActivityReading |
| `Status` | `GroundActivityConflictStatus` | OPEN, RESOLVED |
| `ChosenReadingId` | `long?` | FK → GroundActivityReading |
| `ResolvedBy` | `Guid?` | FK → User |
| `ResolvedAt` | `DateTime?` | |

---

### 4.10 Candidate Result Stub (1 entity)

#### `CandidateResult`
A lightweight stub record created per-candidate per-test-instance. The `IsValid` flag is the primary output — it is set to `false` by the Data Loss Guard when a device assigned to this candidate's test instance is declared lost with `IsDataRecoverable = false`. Full score/timing population is handled by the Results pipeline in section 4.11.

| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `TestInstanceId` | `Guid` | FK → TestInstance (cascade delete) |
| `CandidateId` | `Guid` | FK → Candidate (restrict) |
| `IsValid` | `bool` | Default `true`; set `false` on data loss |
| `InvalidReason` | `string?` | e.g. `"DEVICE_LOST"`, max 500 |
| **Nav** | `TestInstance`, `Candidate` | |
| **Index** | `(TestInstanceId, CandidateId)` | Unique — one stub per candidate per test |

---

### 4.11 Results & Audit (4 entities)


#### `Result` (Immutable — INSERT-ONLY)
| Property | Type | Notes |
|---|---|---|
| `Id` | `long` | BIGINT PK |
| `TestInstanceId` | `Guid` | FK → TestInstance |
| `CandidateId` | `Guid` | FK → Candidate |
| `GenerationRunId` | `Guid` | Correlation key (no FK, just GUID) |
| `OverallScoringStatusId` | `int?` | FK → ScoringStatus |
| `TotalPoints` | `decimal?` | Sum of event points |
| `IsValid` | `bool` | False if data loss guard triggered |
| `HasDataLoss` | `bool` | |
| `HasOutOfBatchInclusions` | `bool` | |
| `HasLateSync` | `bool` | |
| `GeneratedAt` | `DateTime` | |
| `GeneratedBy` | `Guid` | FK → User |
| **Nav** | `TestInstance`, `Candidate`, `ScoringStatus?`, `User` | |
| **Nav** | `ICollection<ResultEventDetail>` | |
| **Nav** | `ICollection<ResultInclusionDecision>` | |

#### `ResultEventDetail` (Per-event outcome within a Result)
| Property | Type | Notes |
|---|---|---|
| `Id` | `long` | BIGINT PK |
| `ResultId` | `long` | FK → Result |
| `TestTypeEventId` | `int` | FK → TestTypeEvent |
| `Status` | `EventStatus` | COMPLETED, DNS, DNF, DATA_LOSS, EXCLUDED |
| `Score` | `decimal?` | Raw score value |
| `ScoringStatusId` | `int?` | FK → ScoringStatus |

#### `ResultInclusionDecision` (Append-only admin decision)
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `ResultId` | `long` | FK → Result |
| `DecisionType` | `InclusionDecisionType` | INCLUDE, EXCLUDE, DATA_LOSS_ACKNOWLEDGED |
| `Reason` | `string?` | |
| `DecidedAt` | `DateTime` | |
| `DecidedBy` | `Guid` | FK → User |

#### `AuditLog` (Immutable audit trail)
| Property | Type | Notes |
|---|---|---|
| `Id` | `long` | BIGINT PK |
| `EntityType` | `string` | e.g. "Candidate" |
| `EntityId` | `string` | String-form PK of target entity |
| `Action` | `AuditAction` | CREATE, UPDATE, DELETE, CONFLICT_RESOLVED, RESULT_GENERATED |
| `UserId` | `Guid?` | FK → User |
| `SiteId` | `Guid?` | Context site |
| `OldValues` | `string?` | JSON snapshot before |
| `NewValues` | `string?` | JSON snapshot after |
| `Metadata` | `string?` | Additional JSON context |
| `OccurredAt` | `DateTime` | |

---

### 4.12 UHF RFID (4 entities)

#### `UHFReaderConfig` (Fixed reader assignment to a gate)
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `TestInstanceId` | `Guid` | FK → TestInstance |
| `DeviceId` | `Guid` | FK → Device |
| `GateType` | `GateType` | StartAttendance, Checkpoint, Finish |
| `CheckpointSequence` | `int?` | For GateType = Checkpoint |
| `IsActive` | `bool` | |

#### `RawUHFTagBuffer` (High-throughput ingestion buffer)
| Property | Type | Notes |
|---|---|---|
| `Id` | `long` | BIGINT PK |
| `DeviceId` | `Guid` | FK → Device |
| `TagEPC` | `string` | Raw EPC |
| `ReadTime` | `DateTime` | |
| `Status` | `TagBufferStatus` | PENDING, PROCESSED, UNRESOLVED, DUPLICATE |
| `IsDuplicate` | `bool` | First Read Rule pre-check |

#### `CheckpointRoute`
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `TestInstanceId` | `Guid` | FK → TestInstance |
| `Name` | `string` | max 100 |
| **Nav** | `ICollection<CheckpointRouteItem>` | |

#### `CheckpointRouteItem`
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `CheckpointRouteId` | `Guid` | FK → CheckpointRoute |
| `Sequence` | `int` | Order in route |
| `Name` | `string` | Checkpoint label |

---

### 4.13 Templates & Reports (4 entities)

#### `ReportTemplate`
| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `Name` | `string` | max 150 |
| `Type` | `TemplateType` | REGISTRATION, TAG_MAPPING, RESULT |
| `SiteId` | `Guid` | FK → Site |
| `TestTypeId` | `int?` | FK → TestType (nullable) |
| `IsActive` | `bool` | |
| **Nav** | `ICollection<TemplateColumn>` | |

#### `TemplateColumn`
| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `ReportTemplateId` | `int` | FK → ReportTemplate |
| `ReportColumnId` | `int` | FK → ReportColumn |
| `Sequence` | `int` | Column order |
| `CustomHeader` | `string?` | Override for column header |

#### `ReportColumn`
| Property | Type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `Name` | `string` | Unique, max 100 |
| `DataType` | `ColumnDataType` | STRING, NUMBER, DATE, BOOL |
| `Description` | `string?` | |

---

### 4.14 Data Import & Sync (3 entities)

#### `ExcelImportLog`
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `BatchId` | `Guid` | FK → Batch |
| `FileName` | `string` | |
| `Status` | `ImportStatus` | COMPLETED, PARTIAL, FAILED |
| `TotalRows` | `int` | |
| `SuccessfulRows` | `int` | |
| `FailedRows` | `int` | |
| `ErrorFile` | `byte[]?` | Excel error report |
| `ImportedAt` | `DateTime` | |
| `ImportedBy` | `Guid` | FK → User |

#### `SyncBatch`
| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `DeviceId` | `Guid` | FK → Device |
| `TestInstanceId` | `Guid` | FK → TestInstance |
| `Payload` | `string` | JSON sync data |
| `ReceivedAt` | `DateTime` | |
| `ProcessedAt` | `DateTime?` | |

#### `BufferProcessingLog`
| Property | Type | Notes |
|---|---|---|
| `Id` | `long` | BIGINT PK |
| `RunAt` | `DateTime` | |
| `ProcessedCount` | `int` | |
| `UnresolvedCount` | `int` | |
| `DurationMs` | `int` | |

---

## 5. Database Structure

### 5.1 Entity Relationship Overview

```
Roles ──< Users ──< Sites
                 ──< Devices
                 
Sites ──< SiteTagRanges
      ──< Candidates ──< CandidateCustomData
      ──< Batches ──< BatchCandidates >── Candidates
                  ──< TagAssignments >── Candidates
                  
TestTypes ──< TestTypeEvents >── Events
                             ──< ScoringType ──< ScoringStatuses
                             ──< ScoringMatrix >── CandidateTypes

Sites ──< TestInstances ──< TestInstanceEvents ──< RaceSessions ──< RaceSessionCandidates
                        ──< TestInstanceDevices                  ──< RaceFinishReads
                        ──< UHFReaderConfigs >── Devices        ──< RaceCheckpointReads
                        ──< AttendanceSessions ──< AttendanceScans
                                               ──< AttendanceCandidateStatuses
                        ──< GroundActivityReadings ──< GroundActivityConflicts
                        
TestInstances ──< Results ──< ResultEventDetails
                          ──< ResultInclusionDecisions
                          
AuditLogs (append-only)
RawUHFTagBuffer (high-throughput)
```

### 5.2 All 47 Database Tables

| # | Table | PK Type | Notes |
|---|---|---|---|
| 1 | `Roles` | INT | System roles |
| 2 | `Users` | GUID | Bcrypt passwords |
| 3 | `SectionPermissions` | INT | RBAC per section |
| 4 | `Sites` | GUID | Physical locations |
| 5 | `SiteTagRanges` | INT | RFID EPC ranges |
| 6 | `DeviceTypes` | INT | Device classifications |
| 7 | `Devices` | GUID | TCP/REST readers |
| 8 | `DeviceIncidents` | GUID | Lost/damaged reports |
| 9 | `Candidates` | GUID | Soft-delete enabled |
| 10 | `CandidateTypes` | INT | e.g. Regulars, NSmen |
| 11 | `CandidateCustomData` | GUID | Key-value pairs |
| 12 | `Batches` | GUID | Versioned candidate groups |
| 13 | `BatchCandidates` | BIGINT | Junction, high-volume |
| 14 | `TagAssignments` | GUID | EPC ↔ Candidate mapping |
| 15 | `ExcelImportLogs` | GUID | Import audit trail |
| 16 | `TestTypes` | INT | Named test configurations |
| 17 | `Events` | INT | Scoreable activities |
| 18 | `TestTypeEvents` | INT | Ordered event assignments |
| 19 | `ScoringTypes` | INT | Grade flavors |
| 20 | `ScoringStatuses` | INT | Pass/Fail/Gold outcomes |
| 21 | `ScoringMatrix` | INT | Range → Status lookup |
| 22 | `TestInstances` | GUID | Scheduled test runs |
| 23 | `TestInstanceEvents` | GUID | Event execution tracking |
| 24 | `TestInstanceDevices` | GUID | Per-device sync state |
| 25 | `AttendanceSessions` | GUID | Session-per-test |
| 26 | `AttendanceScans` | GUID | Individual tag reads |
| 27 | `AttendanceCandidateStatuses` | GUID | Final per-candidate status |
| 28 | `UHFReaderConfigs` | GUID | Fixed reader ↔ gate |
| 29 | `CheckpointRoutes` | GUID | Named route configs |
| 30 | `CheckpointRouteItems` | GUID | Ordered checkpoints |
| 31 | `RawUHFTagBuffers` | BIGINT | High-throughput read buffer |
| 32 | `RaceSessions` | GUID | Heat execution |
| 33 | `RaceSessionCandidates` | GUID | Heat enrollment |
| 34 | `RaceFinishReads` | GUID | Finish line reads |
| 35 | `RaceCheckpointReads` | GUID | Intermediate checkpoint reads |
| 36 | `GroundActivityReadings` | BIGINT | Trainer-recorded results |
| 37 | `GroundActivityConflicts` | GUID | Cross-device conflicts |
| 38 | `CandidateResults` | GUID | Data-loss guard stubs (one per candidate per test) |
| 39 | `Results` | BIGINT | Immutable results (INSERT-ONLY) |
| 40 | `ResultEventDetails` | BIGINT | Per-event outcomes |
| 41 | `ResultInclusionDecisions` | GUID | Append-only admin decisions |
| 42 | `AuditLogs` | BIGINT | Immutable audit trail |
| 43 | `ReportColumns` | INT | Column definitions |
| 44 | `ReportTemplates` | INT | Named export templates |
| 45 | `TemplateColumns` | INT | Column-to-template mapping |
| 46 | `SyncBatches` | GUID | REST device sync queue |
| 47 | `BufferProcessingLogs` | BIGINT | Worker run statistics |

### 5.3 Key Database Design Decisions

| Decision | Reason |
|---|---|
| **BIGINT PKs** for high-volume tables | `BatchCandidates`, `GroundActivityReadings`, `RawUHFTagBuffer`, `Results`, `AuditLogs` — avoids GUID index fragmentation at scale |
| **Soft Delete on Candidates** | Preserves referential integrity for historical results |
| **INSERT-ONLY Results table** | Prevents tampering; new generation run replaces via `LatestGenerationRunId` |
| **Idempotency keys** (`ClientRecordId`) | Prevents duplicate scans from retry/network issues |
| **DataSnapshotVersion on Batch** | Ensures device sync is based on the correct candidate roster |
| **No FK on `GenerationRunId`** | Decouples result generation runs from entity lifecycle |
| **Global query filter on Candidate** | Transparent soft-delete — all queries auto-exclude deleted records |

---

## 6. EF Core Configurations

All 47 configurations implement `IEntityTypeConfiguration<T>` and are auto-discovered in `OnModelCreating` via:

```csharp
modelBuilder.ApplyConfigurationsFromAssembly(typeof(AptmDbContext).Assembly);
```

**Complete list of configuration classes:**

| Configuration Class | Entity |
|---|---|
| `AttendanceCandidateStatusConfiguration` | AttendanceCandidateStatus |
| `AttendanceScanConfiguration` | AttendanceScan |
| `AttendanceSessionConfiguration` | AttendanceSession |
| `AuditLogConfiguration` | AuditLog |
| `BatchCandidateConfiguration` | BatchCandidate |
| `BatchConfiguration` | Batch |
| `BufferProcessingLogConfiguration` | BufferProcessingLog |
| `CandidateConfiguration` | Candidate (with soft-delete query filter) |
| `CandidateCustomDataConfiguration` | CandidateCustomData |
| `CandidateResultConfiguration` | CandidateResult (alias) |
| `CandidateTypeConfiguration` | CandidateType |
| `CheckpointRouteConfiguration` | CheckpointRoute |
| `CheckpointRouteItemConfiguration` | CheckpointRouteItem |
| `DeviceConfiguration` | Device |
| `DeviceIncidentConfiguration` | DeviceIncident |
| `DeviceTypeConfiguration` | DeviceType |
| `EventConfiguration` | Event |
| `ExcelImportLogConfiguration` | ExcelImportLog |
| `GroundActivityConflictConfiguration` | GroundActivityConflict |
| `GroundActivityReadingConfiguration` | GroundActivityReading |
| `RaceCheckpointReadConfiguration` | RaceCheckpointRead |
| `RaceFinishReadConfiguration` | RaceFinishRead |
| `RaceSessionCandidateConfiguration` | RaceSessionCandidate |
| `RaceSessionConfiguration` | RaceSession |
| `RawUHFTagBufferConfiguration` | RawUHFTagBuffer |
| `ReportColumnConfiguration` | ReportColumn |
| `ReportTemplateConfiguration` | ReportTemplate |
| `ResultConfiguration` | Result |
| `ResultEventDetailConfiguration` | ResultEventDetail |
| `ResultInclusionDecisionConfiguration` | ResultInclusionDecision |
| `RoleConfiguration` | Role |
| `ScoringMatrixConfiguration` | ScoringMatrix |
| `ScoringStatusConfiguration` | ScoringStatus |
| `ScoringTypeConfiguration` | ScoringType |
| `SectionPermissionConfiguration` | SectionPermission |
| `SiteConfiguration` | Site |
| `SiteTagRangeConfiguration` | SiteTagRange |
| `SyncBatchConfiguration` | SyncBatch |
| `TagAssignmentConfiguration` | TagAssignment |
| `TemplateColumnConfiguration` | TemplateColumn |
| `TestInstanceConfiguration` | TestInstance |
| `TestInstanceDeviceConfiguration` | TestInstanceDevice |
| `TestInstanceEventConfiguration` | TestInstanceEvent |
| `TestTypeConfiguration` | TestType |
| `TestTypeEventConfiguration` | TestTypeEvent |
| `UHFReaderConfigConfiguration` | UHFReaderConfig |
| `UserConfiguration` | User |

---

## 7. Migration History

22 sequential EF Core migrations applied at startup via `AdminSeederService`.

| # | Migration | Description |
|---|---|---|
| 1 | `20260224053550_InitialCreate` | Base schema (Users, Roles) |
| 2 | `20260224055258_AddDeviceManagement` | Device & DeviceType tables |
| 3 | `20260224055841_AddSiteManagement` | Site table |
| 4 | `20260224061502_AddDeviceRegistrationFields` | Device token, connection fields |
| 5 | `20260224062554_AddReportTemplates` | ReportTemplate, TemplateColumn, ReportColumn |
| 6 | `20260224064246_AddCandidateAndBatchManagement` | Candidates, Batches, BatchCandidates |
| 7 | `20260224071027_AddTestConfiguration` | TestTypes, Events, TestTypeEvents |
| 8 | `20260224072925_AddTestInstanceAndDeviceTracking` | TestInstances, TestInstanceEvents, TestInstanceDevices |
| 9 | `20260224075321_AddAttendanceManagement` | AttendanceSessions, Scans, CandidateStatuses |
| 10 | `20260224083533_AddRaceEventExecution` | RaceSessions, RaceSessionCandidates, FinishReads |
| 11 | `20260224085307_AddHighThroughputTagBuffer` | RawUHFTagBuffer (BIGINT PK) |
| 12 | `20260224091456_AddGroundActivityExecution` | GroundActivityReadings, Conflicts |
| 13 | `20260224093943_AddResultGeneration` | Results, ResultEventDetails, ResultInclusionDecisions |
| 14 | `20260224100818_AddAuditTrail` | AuditLogs (BIGINT PK) |
| 15 | `20260225142853_MergeCandidateNameAndRemoveAutoType` | Schema refactor: merged Name fields |
| 16 | `20260225152721_AddTemplateType` | TemplateType enum column |
| 17 | `20260225181710_RemoveUserEmail` | Removed Email from Users |
| 18 | `20260226110426_RefactorBatchEventArchitecture` | Batch-event link refactor |
| 19 | `20260226114452_AddBatchName` | Added Name to Batch |
| 20 | `20260226132827_AddPointBasedScoring` | Points column on ScoringMatrix |
| 21 | `20260301061818_AddBatchDescription` | Added Description to Batch |
| 22 | `20260302090255_AddCheckpointRoutes` | CheckpointRoutes, CheckpointRouteItems |

---

## 8. API Layer — Controllers & Endpoints

Base path: `/api`  
Authentication: JWT Bearer or Device Token

### `AuthController` — `/api/auth`
| Method | Route | Description |
|---|---|---|
| `POST` | `/login` | Authenticate user → JWT token |
| `GET` | `/me` | Get current user profile |
| `GET` | `/navigation` | Get accessible navigation sections |
| `POST` | `/change-password` | Change own password |
| `POST` | `/users` | Create user (admin) |
| `PUT` | `/users/{id}` | Update user |
| `POST` | `/users/{id}/reset-password` | Admin password reset |
| `GET` | `/permissions/{roleId}` | Get section permissions |
| `PUT` | `/permissions/{roleId}` | Upsert section permissions |

### `SitesController` — `/api/sites`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List all sites |
| `GET` | `/{id}` | Get site by ID |
| `POST` | `` | Create site |
| `PUT` | `/{id}` | Update site |

### `SiteTagRangesController` — `/api/sitetagranges`
| Method | Route | Description |
|---|---|---|
| `GET` | `/{siteId}` | Get tag range for site |
| `PUT` | `/{siteId}` | Upsert tag range |
| `DELETE` | `/{siteId}` | Delete tag range |

### `DevicesController` — `/api/devices`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List devices |
| `GET` | `/{id}` | Get device |
| `POST` | `` | Create device |
| `PUT` | `/{id}` | Update device |
| `DELETE` | `/{id}` | Delete device |
| `POST` | `/{id}/register` | Register device (get API token) |

### `DeviceTypesController` — `/api/devicetypes`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List device types |
| `GET` | `/{id}` | Get device type |
| `POST` | `` | Create device type |

### `DeviceIncidentsController` — `/api/deviceincidents`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List incidents |
| `GET` | `/{id}` | Get incident |
| `POST` | `` | Report incident |
| `POST` | `/{id}/declare-lost` | Declare device lost |

### `CandidatesController` — `/api/candidates`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List candidates (filterable) |
| `GET` | `/{id}` | Get candidate |
| `POST` | `` | Create candidate |
| `PUT` | `/{id}` | Update candidate |
| `DELETE` | `/{id}` | Soft-delete candidate |

### `CandidateTypesController` — `/api/candidatetypes`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List types |
| `GET` | `/{id}` | Get type |
| `POST` | `` | Create type |
| `PUT` | `/{id}` | Update type |

### `BatchesController` — `/api/batches`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List batches |
| `GET` | `/{id}` | Get batch |
| `POST` | `` | Create batch |
| `PUT` | `/{id}` | Update batch |
| `POST` | `/{id}/candidates` | Add candidates to batch |
| `DELETE` | `/{id}/candidates/{candidateId}` | Remove candidate from batch |
| `GET` | `/{id}/tags` | Get tag assignments |
| `POST` | `/{id}/tags` | Assign RFID tags |
| `POST` | `/{id}/import` | Import candidates from Excel |

### `TestTypesController` — `/api/testtypes`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List test types |
| `GET` | `/{id}` | Get test type |
| `POST` | `` | Create test type |
| `PUT` | `/{id}` | Update test type |

### `EventsController` — `/api/events`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List events |
| `GET` | `/{id}` | Get event |
| `POST` | `` | Create event |
| `PUT` | `/{id}` | Update event |

### `ScoringTypesController` — `/api/scoringtypes`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List scoring types |
| `GET` | `/{id}` | Get scoring type (with statuses) |
| `POST` | `` | Create scoring type |
| `PUT` | `/{id}` | Update scoring type |
| `POST` | `/{id}/statuses` | Add scoring status |
| `PUT` | `/{id}/statuses/{statusId}` | Update scoring status |

### `ScoringMatrixController` — `/api/scoringmatrix`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List matrix entries (filter by TestTypeEventId) |
| `GET` | `/{id}` | Get entry |
| `POST` | `` | Create entry |
| `PUT` | `/{id}` | Update entry |
| `DELETE` | `/{id}` | Delete entry |

### `TestInstancesController` — `/api/testinstances`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List test instances |
| `GET` | `/{id}` | Get test instance |
| `POST` | `` | Create test instance |
| `PUT` | `/{id}` | Update test instance |
| `PATCH` | `/{id}/status` | Transition status (DRAFT→ACTIVE etc.) |
| `PATCH` | `/{id}/archive` | Archive test instance |
| `GET` | `/{id}/events` | List test instance events |
| `PATCH` | `/{id}/events/{eventId}/status` | Transition event status |
| `GET` | `/{id}/devices` | List assigned devices & sync status |

### `AttendanceController` — `/api/attendance`
| Method | Route | Description |
|---|---|---|
| `POST` | `/open-session` | Open attendance session for test instance |
| `GET` | `/session/{id}` | Get session with headcount |
| `GET` | `/session/{id}/scans` | List scans (paginated) |
| `GET` | `/session/{id}/headcount` | Real-time headcount |
| `POST` | `/session/{id}/scans` | Submit batch of tag scans |
| `POST` | `/session/{id}/resolve-oob` | Resolve out-of-batch candidate |
| `POST` | `/session/{id}/set-status` | Manually set candidate status |
| `PATCH` | `/session/{id}/close` | Close attendance session |

### `RaceSessionsController` — `/api/racesessions`
| Method | Route | Description |
|---|---|---|
| `GET` | `/heats` | List heats for a TestInstanceEvent |
| `GET` | `/heats/{id}` | Get heat details |
| `POST` | `/heats` | Create new heat |
| `POST` | `/heats/{id}/fire-gun` | Fire gun start (records GunStartTime) |
| `PATCH` | `/heats/{id}/status` | Transition heat status |
| `POST` | `/heats/{id}/manual-finish` | Add manual finish time entry |
| `GET` | `/heats/{id}/results` | Get finish reads for heat |
| `POST` | `/heats/{id}/set-attendance` | Set candidate attendance for heat |

### `GroundActivityController` — `/api/groundactivity`
| Method | Route | Description |
|---|---|---|
| `GET` | `/readings` | List readings for TestInstanceEvent |
| `GET` | `/conflicts` | List unresolved conflicts |
| `POST` | `/conflicts/{id}/resolve` | Resolve a conflict |

### `ResultsController` — `/api/results`
| Method | Route | Description |
|---|---|---|
| `POST` | `/generate` | Generate results for a test instance |
| `GET` | `/{id}` | Get specific result |
| `GET` | `/testinstance/{testInstanceId}` | List results for test instance |
| `GET` | `/testinstance/{testInstanceId}/enriched` | Enriched results with event details |
| `GET` | `/{id}/history` | Result history (all generation runs) |
| `POST` | `/{id}/inclusion-decision` | Append inclusion/exclusion decision |

### `UHFReaderConfigsController` — `/api/uhfreaderconfigs`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List configs for TestInstance |
| `GET` | `/{id}` | Get config |
| `POST` | `` | Create config |
| `PUT` | `/{id}` | Update config |
| `DELETE` | `/{id}` | Deactivate config |

### `CheckpointRoutesController` — `/api/checkpointroutes`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List routes |
| `GET` | `/{id}` | Get route |
| `POST` | `` | Create route |
| `PUT` | `/{id}` | Update route |
| `DELETE` | `/{id}` | Delete route |
| `POST` | `/{id}/apply` | Apply route to TestInstanceEvent |

### `UhfIngestController` — `/api/uhfingest`
| Method | Route | Description |
|---|---|---|
| `POST` | `/tags` | Ingest batch of raw UHF tag reads (Device Token auth) |

### `SyncController` — `/api/sync`
| Method | Route | Description |
|---|---|---|
| `POST` | `/batch` | Submit sync batch from handheld device |
| `GET` | `/status/{testInstanceId}` | Get sync status for all devices |

### `ColumnsController` — `/api/columns`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List report columns |
| `GET` | `/{id}` | Get column |
| `POST` | `` | Create column |
| `PUT` | `/{id}` | Update column |
| `DELETE` | `/{id}` | Delete column |

### `TemplatesController` — `/api/templates`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List templates |
| `GET` | `/{id}` | Get template |
| `GET` | `/{id}/headers` | Get resolved column headers |
| `POST` | `` | Create template |
| `PUT` | `/{id}` | Update template |

### `RolesController` — `/api/roles`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List roles |

### `UsersController` — `/api/users`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | List users |
| `GET` | `/{id}` | Get user |

### `AuditLogsController` — `/api/auditlogs`
| Method | Route | Description |
|---|---|---|
| `GET` | `` | Query audit logs (filterable) |

---

## 9. Application Layer — CQRS

All commands and queries are handled by MediatR. Pipeline behaviors apply validation (FluentValidation) and logging to every request.

### 9.1 Commands (74+)

| Domain | Commands |
|---|---|
| **Auth** | `LoginCommand`, `ChangePasswordCommand`, `CreateUserCommand`, `UpdateUserCommand`, `ResetPasswordCommand`, `ValidateDeviceTokenCommand`, `UpsertSectionPermissionsCommand` |
| **Sites** | `CreateSiteCommand`, `UpdateSiteCommand`, `UpsertSiteTagRangeCommand`, `DeleteSiteTagRangeCommand` |
| **Events** | `CreateEventCommand`, `UpdateEventCommand` |
| **TestTypes** | `CreateTestTypeCommand`, `UpdateTestTypeCommand`, `AddTestTypeEventCommand`, `RemoveTestTypeEventCommand` |
| **TestInstances** | `CreateTestInstanceCommand`, `UpdateTestInstanceCommand`, `TransitionTestInstanceStatusCommand`, `ArchiveTestInstanceCommand`, `TransitionEventStatusCommand` |
| **Batches** | `CreateBatchCommand`, `UpdateBatchCommand`, `UpdateBatchStatusCommand`, `AddBatchCandidatesCommand`, `RemoveBatchCandidateCommand` |
| **Candidates** | `CreateCandidateCommand`, `UpdateCandidateCommand`, `DeleteCandidateCommand`, `ImportCandidatesCommand` |
| **CandidateTypes** | `CreateCandidateTypeCommand`, `UpdateCandidateTypeCommand` |
| **Tags** | `AssignBatchTagsCommand`, `ImportBatchTagsCommand` |
| **Scoring** | `CreateScoringTypeCommand`, `UpdateScoringTypeCommand`, `AddScoringStatusCommand`, `UpdateScoringStatusCommand`, `CreateScoringMatrixCommand`, `UpdateScoringMatrixCommand`, `DeleteScoringMatrixCommand` |
| **Devices** | `CreateDeviceCommand`, `UpdateDeviceCommand`, `DeleteDeviceCommand`, `RegisterDeviceCommand` |
| **DeviceTypes** | `CreateDeviceTypeCommand` |
| **DeviceIncidents** | `CreateDeviceIncidentCommand`, `DeclareLostCommand` |
| **RaceSessions** | `CreateHeatCommand`, `FireGunStartCommand`, `TransitionHeatStatusCommand`, `AddManualFinishCommand`, `SetRaceAttendanceCommand` |
| **Attendance** | `OpenAttendanceSessionCommand`, `SubmitScanBatchCommand`, `SetCandidateAttendanceStatusCommand`, `TransitionAttendanceSessionCommand`, `ResolveOutOfBatchScanCommand` |
| **UHFReaders** | `CreateUHFReaderConfigCommand`, `UpdateUHFReaderConfigCommand`, `DeactivateUHFReaderConfigCommand` |
| **CheckpointRoutes** | `CreateCheckpointRouteCommand`, `UpdateCheckpointRouteCommand`, `DeleteCheckpointRouteCommand`, `ApplyCheckpointRouteCommand` |
| **GroundActivity** | `IngestReadingBatchCommand`, `ResolveGroundConflictCommand` |
| **Results** | `GenerateResultsCommand`, `AddInclusionDecisionCommand` |
| **Sync** | `SubmitSyncBatchCommand` |
| **Reports** | `CreateColumnCommand`, `UpdateColumnCommand`, `DeleteColumnCommand`, `CreateTemplateCommand`, `UpdateTemplateCommand` |

### 9.2 Queries (64+)

| Domain | Queries |
|---|---|
| **Auth** | `GetMeQuery`, `GetNavigationQuery`, `GetSectionPermissionsQuery` |
| **Sites** | `GetSitesListQuery`, `GetSiteByIdQuery`, `GetSiteTagRangeQuery` |
| **Events** | `GetEventsListQuery`, `GetEventByIdQuery` |
| **TestTypes** | `GetTestTypesListQuery`, `GetTestTypeByIdQuery` |
| **TestInstances** | `GetTestInstancesListQuery`, `GetTestInstanceByIdQuery`, `GetTestInstanceEventsQuery`, `GetTestInstanceDevicesQuery` |
| **Batches** | `GetBatchesListQuery`, `GetBatchByIdQuery`, `GetBatchTagsQuery` |
| **Candidates** | `GetCandidatesListQuery`, `GetCandidateByIdQuery` |
| **CandidateTypes** | `GetCandidateTypesListQuery`, `GetCandidateTypeByIdQuery` |
| **Scoring** | `GetScoringTypesListQuery`, `GetScoringTypeByIdQuery`, `GetScoringMatrixListQuery`, `GetScoringMatrixByIdQuery`, `GetScoreQuery` |
| **Devices** | `GetDevicesListQuery`, `GetDeviceByIdQuery`, `GetDeviceTypesListQuery`, `GetDeviceTypeByIdQuery` |
| **DeviceIncidents** | `GetDeviceIncidentsListQuery`, `GetDeviceIncidentByIdQuery` |
| **RaceSessions** | `GetHeatsListQuery`, `GetHeatByIdQuery`, `GetHeatResultsQuery` |
| **Attendance** | `GetAttendanceSessionByIdQuery`, `GetAttendanceScansQuery`, `GetHeadcountQuery` |
| **Results** | `GetResultsListQuery`, `GetResultByIdQuery`, `GetEnrichedResultsListQuery`, `GetResultHistoryQuery` |
| **GroundActivity** | `GetGroundReadingsQuery`, `GetGroundConflictsQuery` |
| **UHFReaders** | `GetUHFReaderConfigsListQuery`, `GetUHFReaderConfigByIdQuery` |
| **CheckpointRoutes** | `GetCheckpointRoutesListQuery`, `GetCheckpointRouteByIdQuery` |
| **Reports** | `GetTemplatesListQuery`, `GetTemplateByIdQuery`, `GetTemplateHeadersQuery`, `GetColumnsListQuery`, `GetColumnByIdQuery` |
| **Roles/Users** | `GetRolesListQuery`, `GetUsersListQuery`, `GetUserByIdQuery` |
| **Audit** | `GetAuditLogsQuery`, `GetImportLogsQuery` |
| **Sync** | `GetSyncStatusQuery` |

---

## 10. Background Workers

All workers are `BackgroundService` implementations registered as `IHostedService` in the Workers class library.

### `ReaderWorkerManager`
- **Purpose:** Manages TCP connections to fixed UHF readers
- **Behavior:** Spawns one `TcpReaderWorker` per active UHF device (`ConnectionMethod = TCP`)
- **On reconnect:** Monitors `DeviceConnectionLog` for retry scheduling

### `BufferProcessorWorker` (Signal-driven)
```
                     ┌──────────────────────────────┐
                     │ IDLE: Await gun-fire signal   │
                     │  IBufferWorkerSignal.WaitAsync│
                     └─────────────┬────────────────┘
                                   │ Signal received (FireGunStart)
                     ┌─────────────▼────────────────┐
                     │ ACTIVE: Poll every 500ms      │
                     │  Process RawUHFTagBuffer rows │
                     │  Resolve EPC → CandidateId    │
                     │  Route to FinishReads /       │
                     │    CheckpointReads            │
                     │  Apply First Read Rule        │
                     └─────────────┬────────────────┘
                                   │ 2 consecutive empty polls
                     ┌─────────────▼────────────────┐
                     │ Return to IDLE                │
                     └──────────────────────────────┘
```

### `SyncIngestWorker`
- **Purpose:** Processes pending `SyncBatch` rows from REST handheld devices
- **Actions:** Deserializes JSON payload, inserts reads into appropriate tables, updates `TestInstanceDevice.SyncStatus`
- **Late sync detection:** Marks `Result.HasLateSync = true` if event already completed

### `ResultGenerationWorker` (every 2 minutes)
- **Data Loss Guard:** Checks for `DECLARED_LOST` devices; marks corresponding results `IsValid = false`, `HasDataLoss = true`
- **Conflict Gate:** Warns (and optionally blocks) if unresolved `GroundActivityConflicts` exist before generation

### `CleanupWorker` (periodic)
- Purges old `AuditLog` entries beyond retention window
- Archives completed `TestInstance` records
- Cleans up processed `SyncBatch` records

---

## 11. SignalR Hubs

### `LiveDashboardHub` — `/hubs/live-dashboard`

**Client → Server Methods:**

| Method | Parameters | Description |
|---|---|---|
| `JoinTestInstance` | `testInstanceId: string` | Subscribe to real-time updates for a test instance |
| `LeaveTestInstance` | `testInstanceId: string` | Unsubscribe |

**Server → Client Events (via `ILiveDashboardNotifier`):**

| Event | Payload | Trigger |
|---|---|---|
| `HeadcountUpdate` | `{ TotalInBatch, TotalPresent, TotalAbsent, TotalOutOfBatch, TotalNotScanned }` | Every scan, every manual status change |
| `ScanEvent` | `{ TagEPC, IsOutOfBatch, ScannedAt, CandidateId?, DeviceCode }` | Each tag scan submitted |
| `GroundActivityConflict` | `{ ConflictId, TestInstanceEventId, CandidateId, ReadingA_Label, ReadingB_Label, DetectedAt }` | On conflict detection |

### `TestHub` — `/hubs/test`
- General test coordination hub (minimal implementation, reserved for future expansion)

---

## 12. Interfaces (Application Layer)

All interfaces are defined in `APTM.Application/Interfaces/` and implemented in `APTM.Infrastructure`.

### Data Access
| Interface | Purpose |
|---|---|
| `IAptmDbContext` | Exposes all 47 `DbSet<T>` collections + `SaveChangesAsync` |

### Authentication
| Interface | Purpose |
|---|---|
| `ICurrentUserService` | Access current user claims: `UserId`, `Username`, `RoleId`, `SiteId` |
| `ITokenService` | JWT generation and device token validation |
| `IPasswordHasher` | Bcrypt hash and verify |

### Business Logic
| Interface | Purpose |
|---|---|
| `IRaceService` | Heat lifecycle: create, fire gun, transition status, manual finish, set attendance |
| `IAttendanceService` | Session management, scan processing, headcount, OOB resolution |
| `IResultGenerationService` | Generate immutable results with conflict gate & data-loss guard |
| `IScoringService` | Scoring matrix lookup by candidate demographics and score value |
| `IGroundActivityService` | Record trainer results, detect cross-device conflicts |
| `ISyncService` | Process REST device sync batches |
| `ITestInstanceDeviceService` | Track per-device sync state |
| `IUhfIngestService` | High-throughput buffer write for raw tag reads |

### Data Import / Export
| Interface | Purpose |
|---|---|
| `ICandidateImportService` | Excel candidate import with error audit |
| `ITagAssignmentService` | Bulk RFID tag assignment and Excel import |
| `IExcelExporter` | Build `.xlsx` reports from templates |
| `IPdfExporter` | Build `.pdf` reports from templates |
| `IReportTemplateQueryService` | Flat data extraction for report templates |

### Infrastructure
| Interface | Purpose |
|---|---|
| `IAuditService` | Write manual audit log entries |
| `IBufferProcessingService` | Process batches of raw UHF tag buffer records |
| `IBufferWorkerSignal` | Signal mechanism to wake `BufferProcessorWorker` |
| `ILiveDashboardNotifier` | Push SignalR events to connected clients |

---

## 13. Infrastructure Services

| Service | Interface | Scope |
|---|---|---|
| `JwtTokenService` | `ITokenService` | Scoped |
| `BcryptPasswordHasher` | `IPasswordHasher` | Scoped |
| `AuditService` | `IAuditService` | Scoped |
| `AuditInterceptor` | *(EF interceptor)* | Singleton |
| `BufferProcessingService` | `IBufferProcessingService` | Scoped |
| `BufferWorkerSignal` | `IBufferWorkerSignal` | Singleton |
| `CandidateImportService` | `ICandidateImportService` | Scoped |
| `ExcelExporter` | `IExcelExporter` | Scoped |
| `PdfExporter` | `IPdfExporter` | Scoped |
| `RaceService` | `IRaceService` | Scoped |
| `AttendanceService` | `IAttendanceService` | Scoped |
| `ScoringService` | `IScoringService` | Scoped |
| `TagAssignmentService` | `ITagAssignmentService` | Scoped |
| `TestInstanceDeviceService` | `ITestInstanceDeviceService` | Scoped |
| `GroundActivityService` | `IGroundActivityService` | Scoped |
| `ResultGenerationService` | `IResultGenerationService` | Scoped |
| `SyncService` | `ISyncService` | Scoped |
| `ReportTemplateQueryService` | `IReportTemplateQueryService` | Scoped |
| `UhfIngestService` | `IUhfIngestService` | Scoped |
| `AdminSeederService` | *(IHostedService)* | Hosted |

---

## 14. Dependency Injection

### Application Layer (`APTM.Application/DependencyInjection.cs`)
```csharp
services.AddMediatR(assembly);                         // All command/query handlers
services.AddValidatorsFromAssembly(assembly);          // FluentValidation validators
services.AddTransient<IPipelineBehavior<,>, LoggingBehaviour<,>>();
services.AddTransient<IPipelineBehavior<,>, ValidationBehaviour<,>>();
```

### Infrastructure Layer (`APTM.Infrastructure/DependencyInjection.cs`)
```csharp
services.Configure<JwtSettings>(config.GetSection("Jwt"));
services.AddScoped<ITokenService, JwtTokenService>();
services.AddSingleton<AuditInterceptor>();
services.AddDbContext<AptmDbContext>(options => ...); // With AuditInterceptor
services.AddScoped<IAptmDbContext>(p => p.GetRequiredService<AptmDbContext>());
services.AddSingleton<IBufferWorkerSignal, BufferWorkerSignal>();
services.AddScoped<IPasswordHasher, BcryptPasswordHasher>();
services.AddScoped<IReportTemplateQueryService, ReportTemplateQueryService>();
services.AddScoped<IExcelExporter, ExcelExporter>();
services.AddScoped<IPdfExporter, PdfExporter>();
services.AddScoped<ICandidateImportService, CandidateImportService>();
services.AddScoped<ITagAssignmentService, TagAssignmentService>();
services.AddScoped<IScoringService, ScoringService>();
services.AddScoped<ITestInstanceDeviceService, TestInstanceDeviceService>();
services.AddScoped<ISyncService, SyncService>();
services.AddScoped<IAttendanceService, AttendanceService>();
services.AddScoped<IRaceService, RaceService>();
services.AddScoped<IUhfIngestService, UhfIngestService>();
services.AddScoped<IBufferProcessingService, BufferProcessingService>();
services.AddScoped<IGroundActivityService, GroundActivityService>();
services.AddScoped<IResultGenerationService, ResultGenerationService>();
services.AddScoped<IAuditService, AuditService>();
services.AddHostedService<AdminSeederService>();
```

### Workers Layer (`APTM.Workers/DependencyInjection.cs`)
```csharp
services.AddHostedService<ReaderWorkerManager>();
services.AddHostedService<BufferProcessorWorker>();
services.AddHostedService<SyncIngestWorker>();
services.AddHostedService<ResultGenerationWorker>();
services.AddHostedService<CleanupWorker>();
```

### API Layer (`Program.cs`)
```csharp
services.AddAuthentication()
    .AddJwtBearer(...)
    .AddScheme<DeviceTokenAuthOptions, DeviceTokenAuthHandler>(...);
services.AddAuthorization(policies: DefaultPolicy + SectionPolicies);
services.AddSignalR();
services.AddCors(origins: localhost:5173, localhost:3000);
services.AddOpenApi();
services.AddScoped<ICurrentUserService, CurrentUserService>();
services.AddScoped<ILiveDashboardNotifier, SignalRLiveDashboardNotifier>();

app.MapControllers();
app.MapHub<TestHub>("/hubs/test");
app.MapHub<LiveDashboardHub>("/hubs/live-dashboard");
```

---

## 15. Configuration

### `appsettings.json`
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=DESKTOP-RH5LN2V\\SQLEXPRESS;Database=APTM_V2;Trusted_Connection=True;TrustServerCertificate=True;"
  },
  "Jwt": {
    "SecretKey": "<min 32-char secret>",
    "Issuer": "APTM-Api",
    "Audience": "APTM-Web",
    "ExpiryMinutes": 480
  },
  "DefaultSite": {
    "Name": "Main Site",
    "Code": "MAIN"
  },
  "Cors": {
    "Origins": ["http://localhost:5173", "http://localhost:3000"]
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

| Setting | Value | Notes |
|---|---|---|
| **Database** | SQL Server Express (`APTM_V2`) | Windows Authentication, self-signed cert |
| **JWT expiry** | 480 minutes (8 hours) | |
| **CORS origins** | `localhost:5173`, `localhost:3000` | Vite & CRA dev servers |
| **Admin seeded at startup** | Yes (`AdminSeederService`) | Default site also created |

---

## 16. Domain Enums

| Category | Enum | Values |
|---|---|---|
| **Auth** | `Permission` | Read, Write, Execute |
| **Batch** | `BatchStatus` | DRAFT, ACTIVE, LOCKED, ARCHIVED |
| **Test Instance** | `TestInstanceStatus` | DRAFT, ACTIVE, COMPLETED, CANCELLED |
| **Test Instance Event** | `TestInstanceEventStatus` | PENDING, IN_PROGRESS, COMPLETED, CANCELLED |
| **Event** | `EventType` | RACE, STATUS, POINT_BASED |
| **Event** | `EventStatus` | COMPLETED, DNS, DNF, DATA_LOSS, EXCLUDED |
| **Event** | `MeasurementUnit` | *(units: seconds, reps, metres, etc.)* |
| **Race** | `RaceSessionStatus` | PENDING, RUNNING, FINISHED |
| **Race** | `RaceAttendanceStatus` | PRESENT, ABSENT, DNS |
| **Race** | `RaceAttendanceSource` | HANDHELD_SYNC, UHF_FIXED |
| **Attendance** | `AttendanceCandidateStatusType` | NOT_SCANNED, PRESENT, ABSENT |
| **Attendance** | `AttendanceSessionStatus` | OPEN, CLOSED, REOPENED |
| **Attendance** | `AttendanceStatusSetBy` | AUTO, MANUAL |
| **Device** | `ConnectionMethod` | TCP, REST |
| **Device** | `SyncStatus` | PENDING, SYNCED, PARTIAL, FAILED |
| **Device** | `IncidentType` | LOST, DAMAGED, MALFUNCTIONING |
| **Device** | `DeviceConnectionEventType` | CONNECTED, DISCONNECTED, RECONNECTED |
| **UHF** | `TagBufferStatus` | PENDING, PROCESSED, UNRESOLVED, DUPLICATE |
| **UHF** | `GateType` | StartAttendance, Checkpoint, Finish |
| **Ground Activity** | `ConflictStatus` | NONE, CONFLICT, RESOLVED_KEPT, RESOLVED_DISCARDED |
| **Ground Activity** | `GroundActivityConflictStatus` | OPEN, RESOLVED |
| **Results** | `InclusionDecisionType` | INCLUDE, EXCLUDE, DATA_LOSS_ACKNOWLEDGED |
| **Audit** | `AuditAction` | CREATE, UPDATE, DELETE, CONFLICT_RESOLVED, RESULT_GENERATED |
| **Import** | `ImportStatus` | COMPLETED, PARTIAL, FAILED |
| **Templates** | `TemplateType` | REGISTRATION, TAG_MAPPING, RESULT |
| **Templates** | `ColumnDataType` | STRING, NUMBER, DATE, BOOL |

---

## 17. Key Architecture Patterns

### 1. Clean Architecture
Strict layering — outer layers depend on inner layers only. Domain has zero external dependencies.

### 2. CQRS with MediatR
All business operations go through `IRequest`/`IRequestHandler`. Two pipeline behaviors apply to every request:
- **`ValidationBehaviour`:** Runs FluentValidation; returns 400 before the handler if invalid
- **`LoggingBehaviour`:** Logs request name, duration, and outcome

### 3. Signal-Driven Buffer Processing
The `BufferProcessorWorker` idles until a gun-fire event signals it via `IBufferWorkerSignal`. This avoids wasteful polling when no race is active.

### 4. Immutable Results
The `Results` table is INSERT-ONLY. Re-running generation creates new rows with a new `GenerationRunId`. The `TestInstance.LatestGenerationRunId` stamps which run is canonical. Old runs are preserved for auditing via `GET /{id}/history`.

### 5. First Read Rule
For race timing, only the first finish/checkpoint read per candidate per heat is marked as valid (`IsDuplicate = false`). Subsequent reads are stored but marked as duplicates.

### 6. Idempotency Keys
Handheld devices assign a `ClientRecordId` to each scan/reading. The server rejects or de-dupes submissions with the same key, ensuring network retries are safe.

### 7. Versioned Batch Snapshots
`Batch.DataSnapshotVersion` increments whenever candidates are added or removed. `TestInstanceDevice.DataSnapshotVersion` captures the version at device sync time. A mismatch triggers a warning — the device may have operated on a stale roster.

### 8. Soft Delete with Global Query Filter
`Candidate.IsDeleted` is filtered at the EF Core model level (`HasQueryFilter`). All queries automatically exclude deleted candidates without requiring explicit `WHERE IsDeleted = 0` clauses.

### 9. Audit Interceptor
`AuditInterceptor` hooks into `SaveChangesAsync`. For tracked entities, it captures before/after JSON snapshots and writes to `AuditLogs`. Business-event auditing (result generation, conflict resolution) uses `IAuditService` directly.

### 10. Dual Auth Schemes
- **JWT Bearer:** Standard user authentication (8-hour token)
- **Device Token:** Hashed API token scheme for handheld/fixed devices (`DeviceTokenAuthHandler`)

---

## 18. Summary Statistics

| Metric | Count |
|---|---|
| Total C# Source Files | 482 |
| Domain Entity Classes | 47 |
| Database Tables (DbSets) | 47 |
| EF Core Configurations | 47 |
| Database Migrations | 22 |
| API Controllers | 28 |
| HTTP Endpoints | 80+ |
| CQRS Commands | 74+ |
| CQRS Queries | 64+ |
| Application Interfaces | 21 |
| Infrastructure Services | 19 |
| Background Workers | 5 |
| SignalR Hubs | 2 |
| Domain Enums | 25+ |
| MediatR Pipeline Behaviors | 2 |

---

*Generated by automated analysis of the APTM_Claude repository. Last updated: 2026-03-13.*

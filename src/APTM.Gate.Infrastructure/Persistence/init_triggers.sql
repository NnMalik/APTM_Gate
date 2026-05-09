-- ============================================================================
-- APTM Gate Service — PostgreSQL LISTEN/NOTIFY Triggers
-- Matches spec Section 3.2 exactly.
-- Applied at startup by PostgresInitService (CREATE OR REPLACE = idempotent).
-- ============================================================================

-- 1. tag_event: Fires on processed_events INSERT (first reads only, not voided).
--    Reads denormalized name + jacket_number directly from the row (no JOIN needed).
--    The `voided = false` guard means rows that are immediately voided (rare —
--    can happen if a race_cancel arrives in the same SaveChangesAsync window as
--    the finish) don't leak to the SSE display. Voiding existing rows happens
--    via UPDATE which doesn't fire this AFTER-INSERT trigger anyway.
CREATE OR REPLACE FUNCTION notify_tag_event() RETURNS trigger AS $$
BEGIN
    PERFORM pg_notify('tag_event', json_build_object(
        'id', NEW.id,
        'candidate_id', NEW.candidate_id,
        'tag_epc', NEW.tag_epc,
        'event_type', NEW.event_type,
        'read_time', NEW.read_time,
        'duration_seconds', NEW.duration_seconds,
        'is_first_read', NEW.is_first_read,
        'jacket_number', NEW.jacket_number,
        'name', NEW.candidate_name,
        'heat_number', NEW.heat_number
    )::text);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_notify_tag_event ON processed_events;
CREATE TRIGGER trg_notify_tag_event
    AFTER INSERT ON processed_events
    FOR EACH ROW
    WHEN (NEW.is_first_read = true AND NEW.voided = false)
    EXECUTE FUNCTION notify_tag_event();

-- 2. race_start: Fires on race_start_times INSERT.
--    Includes candidate_ids array in the payload.
CREATE OR REPLACE FUNCTION notify_race_start() RETURNS trigger AS $$
BEGIN
    PERFORM pg_notify('race_start', json_build_object(
        'heat_id', NEW.heat_id,
        'heat_number', NEW.heat_number,
        'gun_start_time', to_char(NEW.gun_start_time AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'),
        'original_gun_start_time', to_char(NEW.original_gun_start_time AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'),
        'candidate_ids', NEW.candidate_ids
    )::text);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_notify_race_start ON race_start_times;
CREATE TRIGGER trg_notify_race_start
    AFTER INSERT ON race_start_times
    FOR EACH ROW
    EXECUTE FUNCTION notify_race_start();

-- 3. sync_data: Fires on received_sync_data INSERT.
--    Uses source_device_code (not source_device_id).
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

DROP TRIGGER IF EXISTS trg_notify_sync_data ON received_sync_data;
CREATE TRIGGER trg_notify_sync_data
    AFTER INSERT ON received_sync_data
    FOR EACH ROW
    EXECUTE FUNCTION notify_sync_data();

-- 4. config_updated: Fired manually via NOTIFY in GateConfigService.
--    No trigger needed — the service calls pg_notify('config_updated', ...) directly.

-- 5. heat_complete: Fires on heat_completions INSERT.
--    Includes heat_id, heat_number, completed_at, last_candidate_id, closure_reason
--    so the display can freeze the timer at the right moment.
CREATE OR REPLACE FUNCTION notify_heat_complete() RETURNS trigger AS $$
BEGIN
    PERFORM pg_notify('heat_complete', json_build_object(
        'heat_id', NEW.heat_id,
        'heat_number', NEW.heat_number,
        'expected_count', NEW.expected_count,
        'finished_count', NEW.finished_count,
        'last_candidate_id', NEW.last_candidate_id,
        'completed_at', to_char(NEW.completed_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'),
        'closure_reason', NEW.closure_reason,
        'source_device_code', NEW.source_device_code
    )::text);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_notify_heat_complete ON heat_completions;
CREATE TRIGGER trg_notify_heat_complete
    AFTER INSERT ON heat_completions
    FOR EACH ROW
    EXECUTE FUNCTION notify_heat_complete();

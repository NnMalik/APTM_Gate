-- ============================================================================
-- APTM Gate Service — PostgreSQL LISTEN/NOTIFY Triggers
-- Matches spec Section 3.2 exactly.
-- Applied at startup by PostgresInitService (CREATE OR REPLACE = idempotent).
-- ============================================================================

-- 1. tag_event: Fires on processed_events INSERT (first reads only).
--    Joins candidates to include name + jacket_number in the payload.
CREATE OR REPLACE FUNCTION notify_tag_event() RETURNS trigger AS $$
DECLARE
    payload JSON;
BEGIN
    SELECT json_build_object(
        'id', NEW.id,
        'candidate_id', NEW.candidate_id,
        'tag_epc', NEW.tag_epc,
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

DROP TRIGGER IF EXISTS trg_notify_tag_event ON processed_events;
CREATE TRIGGER trg_notify_tag_event
    AFTER INSERT ON processed_events
    FOR EACH ROW
    WHEN (NEW.is_first_read = true)
    EXECUTE FUNCTION notify_tag_event();

-- 2. race_start: Fires on race_start_times INSERT.
--    Includes candidate_ids array in the payload.
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

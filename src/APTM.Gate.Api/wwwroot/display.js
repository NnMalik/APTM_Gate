/* ============================================================================
   APTM Gate Display — Shared JavaScript
   Common SSE connection, clock, feed, and data-fetching logic.
   Each HTML page sets `window.DISPLAY_ROLE` before loading this script.
   ============================================================================ */

const Display = (() => {
    // ── State ───────────────────────────────────────────────────────────────
    const state = {
        gateRole: 'unconfigured',
        gunStartTime: null,
        timerInterval: null,
        feedCount: 0,
        sseSource: null,
    };

    const MAX_FEED = 200;

    // ── Clock ───────────────────────────────────────────────────────────────
    function startClock() {
        const el = document.getElementById('clock');
        if (!el) return;
        const tick = () => {
            el.textContent = new Date().toLocaleTimeString('en-IN', { hour12: false });
        };
        tick();
        setInterval(tick, 1000);
    }

    // ── Fetch initial display data ──────────────────────────────────────────
    async function loadDisplayData() {
        try {
            const res = await fetch('/gate/display-data');
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const data = await res.json();
            applyDisplayData(data);
        } catch (e) {
            console.error('Failed to load display data:', e);
        }
    }

    function applyDisplayData(data) {
        // Gate role
        state.gateRole = (data.gateRole || 'unconfigured').toLowerCase();
        const badgeEl = document.getElementById('gateBadge');
        if (badgeEl) {
            badgeEl.textContent = state.gateRole.toUpperCase();
            badgeEl.className = 'gate-badge ' + state.gateRole;
        }

        // Test info
        setText('testName', data.testInstanceName || '--');
        setText('testDate', data.scheduledDate || '--');
        setText('eventName', data.activeEventName || '--');

        // Reader connection status
        const readerEl = document.getElementById('readerStatus');
        if (readerEl) {
            readerEl.textContent = data.readerConnected ? 'Reader: Connected' : 'Reader: Disconnected';
            readerEl.className = 'reader-status ' + (data.readerConnected ? 'connected' : 'disconnected');
        }

        // Processing status
        const procEl = document.getElementById('processingStatus');
        if (procEl) {
            procEl.textContent = data.isProcessingActive ? 'Processing: Active' : 'Processing: Idle';
            procEl.className = 'processing-status ' + (data.isProcessingActive ? 'active' : 'idle');
        }

        // Stats
        setText('statTotal', data.totalCandidates ?? '--');
        if (data.attendance) {
            setText('statPresent', data.attendance.totalPresent ?? 0);
            setText('statAbsent', data.attendance.totalAbsent ?? 0);
            setText('statNotScanned', data.attendance.totalNotScanned ?? 0);
        }

        // Finish reads (finish display)
        if (data.finishReads && data.finishReads.length > 0 && typeof onFinishReadsLoaded === 'function') {
            onFinishReadsLoaded(data.finishReads);
        }

        // Start/attendance reads (start display)
        if (data.startReads && data.startReads.length > 0 && typeof onStartReadsLoaded === 'function') {
            onStartReadsLoaded(data.startReads);
        }

        // Active heat — or clear if no heat
        if (data.activeHeat) {
            applyHeat(data.activeHeat);
        } else {
            clearHeat();
        }

        // Derive event status
        updateEventStatus(data);
    }

    function applyHeat(heat) {
        state.gunStartTime = heat.gunStartTime ? new Date(heat.gunStartTime) : null;
        setText('heatNumber', heat.heatNumber ?? '--');
        setText('statHeat', heat.heatNumber ?? '--');

        const timerCard = document.getElementById('heatCard');
        if (timerCard) timerCard.style.display = 'block';

        if (state.gunStartTime) {
            setText('gunStartTime', state.gunStartTime.toLocaleTimeString('en-IN', { hour12: false, fractionalSecondDigits: 3 }));
            setLiveIndicator(true);
            startHeatTimer();
        }

        // Heat candidates (start display)
        if (heat.candidates && typeof onHeatCandidatesLoaded === 'function') {
            onHeatCandidatesLoaded(heat.candidates);
        }
    }

    function clearHeat() {
        // Stop timer and clear gun start state
        if (state.timerInterval) {
            clearInterval(state.timerInterval);
            state.timerInterval = null;
        }
        state.gunStartTime = null;

        const timerCard = document.getElementById('heatCard');
        if (timerCard) timerCard.style.display = 'none';

        const timerEl = document.getElementById('heatTimer');
        if (timerEl) timerEl.textContent = '00:00.000';

        setText('heatNumber', '--');
        setText('statHeat', '--');
        setText('gunStartTime', '--');
        setLiveIndicator(false);
    }

    function updateEventStatus(data) {
        const el = document.getElementById('eventStatus');
        if (!el) return;

        let status, color, bgColor;
        if (!data.activeHeat || !data.activeHeat.gunStartTime) {
            status = 'Not Started';
            color = '#9CA3AF';
            bgColor = 'rgba(156, 163, 175, 0.15)';
        } else if (data.isProcessingActive) {
            status = 'In Progress';
            color = '#22C55E';
            bgColor = 'rgba(34, 197, 94, 0.15)';
        } else if (data.finishReads && data.finishReads.length > 0) {
            status = 'Completed';
            color = '#3B82F6';
            bgColor = 'rgba(59, 130, 246, 0.15)';
        } else {
            status = 'Idle';
            color = '#F59E0B';
            bgColor = 'rgba(245, 158, 11, 0.15)';
        }

        el.textContent = status;
        el.style.color = color;
        el.style.background = bgColor;
        el.style.display = 'inline-block';
    }

    // ── Heat Timer ──────────────────────────────────────────────────────────
    function startHeatTimer() {
        if (state.timerInterval) clearInterval(state.timerInterval);
        const el = document.getElementById('heatTimer');
        if (!el) return;

        state.timerInterval = setInterval(() => {
            if (!state.gunStartTime) return;
            const elapsed = (Date.now() - state.gunStartTime.getTime()) / 1000;
            el.textContent = elapsed < 0 ? '00:00.000' : formatElapsed(elapsed);
        }, 50);
    }

    function formatElapsed(seconds) {
        const m = Math.floor(seconds / 60);
        const s = (seconds % 60).toFixed(3);
        return `${String(m).padStart(2, '0')}:${s.padStart(6, '0')}`;
    }

    function formatDuration(seconds) {
        if (seconds == null) return '--';
        const m = Math.floor(seconds / 60);
        const s = (seconds % 60).toFixed(3);
        return `${String(m).padStart(2, '0')}:${s.padStart(6, '0')}`;
    }

    // ── SSE Connection ──────────────────────────────────────────────────────
    function connectSSE() {
        if (state.sseSource) { state.sseSource.close(); }
        const src = new EventSource('/gate/display-stream');
        state.sseSource = src;

        src.onopen = () => setConnectionStatus(true);
        src.onerror = () => {
            setConnectionStatus(false);
            // EventSource auto-reconnects; on reconnect we re-fetch state
        };

        src.addEventListener('tag_event', (e) => {
            const d = JSON.parse(e.data);
            addFeed('tag', `${d.name || d.candidate_id?.substring(0, 8)} (#${d.jacket_number ?? '?'}) — ${d.event_type}`);
            if (typeof onTagEvent === 'function') onTagEvent(d);
        });

        src.addEventListener('race_start', (e) => {
            const d = JSON.parse(e.data);
            state.gunStartTime = new Date(d.gun_start_time);
            setText('heatNumber', d.heat_number ?? '--');
            setText('gunStartTime', state.gunStartTime.toLocaleTimeString('en-IN', { hour12: false, fractionalSecondDigits: 3 }));

            const timerCard = document.getElementById('heatCard');
            if (timerCard) timerCard.style.display = 'block';
            setLiveIndicator(true);
            startHeatTimer();
            addFeed('start', `Group ${d.heat_number} — Gun fired`);

            if (typeof onRaceStart === 'function') onRaceStart(d);
        });

        src.addEventListener('sync_data', (e) => {
            const d = JSON.parse(e.data);
            addFeed('sync', `${d.data_type} from ${d.source_device_code}`);
            if (typeof onSyncData === 'function') onSyncData(d);
        });

        src.addEventListener('config_updated', (e) => {
            addFeed('config', 'Configuration updated — reloading...');
            setTimeout(loadDisplayData, 500);
        });
    }

    // ── Feed ────────────────────────────────────────────────────────────────
    function addFeed(type, message) {
        const feed = document.getElementById('activityFeed');
        if (!feed) return;

        // Remove empty state
        const empty = feed.querySelector('.empty-state');
        if (empty) empty.remove();

        const item = document.createElement('div');
        item.className = 'feed-item';
        const now = new Date().toLocaleTimeString('en-IN', { hour12: false });
        item.innerHTML = `<span class="feed-time">${now}</span>` +
            `<span class="feed-type ${type}">${type}</span> ` +
            `<span>${escapeHtml(message)}</span>`;

        feed.insertBefore(item, feed.firstChild);
        state.feedCount++;
        setText('feedCount', state.feedCount);

        while (feed.children.length > MAX_FEED) feed.removeChild(feed.lastChild);
    }

    // ── Toast Notifications ──────────────────────────────────────────────────
    const MAX_TOASTS = 3;
    function showToast(type, message) {
        const container = document.getElementById('toastContainer');
        if (!container) return;

        const toast = document.createElement('div');
        toast.className = `toast ${type}`;
        const now = new Date().toLocaleTimeString('en-IN', { hour12: false });
        toast.innerHTML = `<span class="toast-time">${now}</span>` +
            `<span class="toast-type ${type}">${type}</span> ` +
            `<span>${escapeHtml(message)}</span>`;

        container.appendChild(toast);

        // Remove oldest if over limit
        while (container.children.length > MAX_TOASTS) {
            container.removeChild(container.firstChild);
        }

        // Auto-dismiss after 5s
        setTimeout(() => {
            toast.classList.add('fade-out');
            setTimeout(() => toast.remove(), 400);
        }, 5000);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    function setText(id, value) {
        const el = document.getElementById(id);
        if (el) el.textContent = value;
    }

    function setConnectionStatus(connected) {
        const dot = document.getElementById('sseDot');
        const label = document.getElementById('sseLabel');
        if (dot) dot.classList.toggle('connected', connected);
        if (label) label.textContent = connected ? 'Connected' : 'Reconnecting...';

        // On reconnect, re-fetch full state
        if (connected) loadDisplayData();
    }

    function setLiveIndicator(active) {
        const el = document.getElementById('liveIndicator');
        if (el) el.classList.toggle('active', active);
    }

    function escapeHtml(str) {
        const d = document.createElement('div');
        d.textContent = str;
        return d.innerHTML;
    }

    // ── Init ────────────────────────────────────────────────────────────────
    function init() {
        startClock();
        loadDisplayData();
        connectSSE();
    }

    // Public API
    return { init, state, formatDuration, formatElapsed, addFeed, showToast, setText, loadDisplayData };
})();

// Auto-init on DOMContentLoaded
document.addEventListener('DOMContentLoaded', Display.init);

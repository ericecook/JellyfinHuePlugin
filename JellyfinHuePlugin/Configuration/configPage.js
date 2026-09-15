// Hue Lighting Control plugin page. The dashboard imports this module for the page's data-controller
// ("__plugin/HueLightingControl.js") and runs the default export once for every view it creates.
// Baseline: modern syntax (let/const, arrow functions, template literals, spread); it runs wherever the
// dashboard's own dynamic import runs.
// Sections: constants and helpers; the save queue; HueConfigPage (state, queue operations, bridges list,
// bridge modal, profiles list, profile editor, rooms and scenes, device picker, modals and lifecycle).

// ---------------------------------------------------------------------------------------------------------
// Constants and helpers
// ---------------------------------------------------------------------------------------------------------

const PLUGIN_ID = '2a5f5b3e-8c9d-4f1a-9b7e-6d3c4e5f6a7b';

// Play, Pause and Stop. Element ids and profile fields are built from the name (stateIds, stateFields);
// Play also has Turn Off, Pause also has the grace period.
const STATES = [
    { name: 'Play', turnOff: true, gracePeriod: false },
    { name: 'Pause', turnOff: false, gracePeriod: true },
    { name: 'Stop', turnOff: false, gracePeriod: false }
];

const SLIDERS = {
    brightness: { min: 0, max: 100, step: null, describe: value => brightnessText(value) },
    transition: { min: 0, max: 150, step: 1, describe: value => transitionText(value) }
};

// LightControlProfile's initializers in PluginConfiguration.cs, except the name
const PROFILE_DEFAULTS = {
    Name: 'New Profile',
    Enabled: true,
    BridgeId: '',
    EnableForMovies: true,
    EnableForTvShows: false,
    TargetClientName: '',
    TargetDeviceIds: [],
    TargetIpAddress: '',
    TargetGroupId: '0',
    PlaySceneId: '',
    PauseSceneId: '',
    StopSceneId: '',
    TurnOffLightsOnPlay: false,
    PlayBrightness: 8,
    PauseBrightness: 39,
    StopBrightness: 100,
    EnablePlayTransition: false,
    PlayTransitionDuration: 4,
    EnablePauseTransition: false,
    PauseTransitionDuration: 4,
    EnableStopTransition: false,
    StopTransitionDuration: 4,
    PauseGracePeriodSeconds: 0,
    EnableOutroLights: false
};

// Each template holds only what differs from PROFILE_DEFAULTS
const TEMPLATES = [
    {
        id: 'movie-theater',
        label: 'Movie Theater — Complete darkness with outro detection',
        values: { Name: 'Movie Theater', EnableForMovies: true, EnableForTvShows: false, TurnOffLightsOnPlay: true, PauseBrightness: 6, PauseGracePeriodSeconds: 60, StopBrightness: 24, EnableOutroLights: true }
    },
    {
        id: 'tv-viewing',
        label: 'TV Viewing — Moderate dimming, no outro',
        values: { Name: 'TV Viewing', EnableForMovies: false, EnableForTvShows: true, PlayBrightness: 24, PauseBrightness: 47, StopBrightness: 79, EnableOutroLights: false }
    },
    {
        id: 'bedroom-casual',
        label: 'Bedroom Casual — Gentle dimming for comfort',
        values: { Name: 'Bedroom Casual', EnableForMovies: true, EnableForTvShows: true, PlayBrightness: 31, PauseBrightness: 59, StopBrightness: 79 }
    },
    {
        id: 'gaming',
        label: 'Gaming Setup — Minimal dim, fast response',
        values: { Name: 'Gaming Setup', EnableForMovies: true, EnableForTvShows: true, PlayBrightness: 20, EnableOutroLights: false }
    }
];

const DRAG_TYPE = 'application/x-hue-profile';

function stateIds(state) {
    const name = state.name;
    const lower = name.toLowerCase();
    return {
        error: `#profileTest${name}Error`,
        scene: `#profile${name}SceneId`,
        turnOffContainer: `#${lower}TurnOffContainer`,
        turnOff: `#profileTurnOffLightsOn${name}`,
        brightnessContainer: `#${lower}BrightnessContainer`,
        brightnessSlider: `#profile${name}BrightnessSlider`,
        brightness: `#profile${name}Brightness`,
        brightnessDesc: `#profile${name}BrightnessDesc`,
        transitionEnabled: `#profileEnable${name}Transition`,
        transitionContainer: `#${lower}TransitionContainer`,
        transitionSlider: `#profile${name}TransitionSlider`,
        transition: `#profile${name}TransitionDuration`,
        transitionDesc: `#profile${name}TransitionDesc`,
        graceEnabled: `#profileEnable${name}GracePeriod`,
        graceContainer: `#${lower}GracePeriodContainer`,
        grace: `#profile${name}GracePeriod`
    };
}

function stateFields(state) {
    return {
        scene: `${state.name}SceneId`,
        brightness: `${state.name}Brightness`,
        transitionEnabled: `Enable${state.name}Transition`,
        transition: `${state.name}TransitionDuration`
    };
}

function escapeHtml(value) {
    return String(value == null ? '' : value)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

// A short, truthful reason for a failed request: the HTTP status when the server answered, the message for
// a network or script error
function describeError(error) {
    if (error && typeof error.status === 'number') {
        return `HTTP ${error.status}${error.statusText ? ' ' + error.statusText : ''}`;
    }
    if (error && error.message) return error.message;
    return 'unknown error';
}

const deepCopy = value => JSON.parse(JSON.stringify(value));

const indexOfId = (list, id) => list.findIndex(item => item && item.Id === id);

function generateGuid() {
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
        const r = Math.random() * 16 | 0;
        return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
    });
}

function brightnessText(value) {
    let word = 'Very Bright';
    if (value === 0) word = 'Minimum';
    else if (value < 20) word = 'Very Dim';
    else if (value < 40) word = 'Dim';
    else if (value < 60) word = 'Medium';
    else if (value < 80) word = 'Bright';
    return `${word} (${value}%)`;
}

function transitionText(value) {
    const seconds = (value / 10.0).toFixed(1);
    if (value === 0) return 'Instant (0.0s)';
    if (value < 10) return `Very Fast (${seconds}s)`;
    if (value < 30) return `Fast (${seconds}s)`;
    if (value < 70) return `Medium (${seconds}s)`;
    if (value < 100) return `Slow (${seconds}s)`;
    return `Very Slow (${seconds}s)`;
}

// The card's one-line facts about a profile
function profileSummary(profile, bridges) {
    const items = [];
    const media = [];
    if (profile.EnableForMovies !== false) media.push('Movies');
    if (profile.EnableForTvShows) media.push('TV Shows');
    items.push('Triggers on: ' + (media.length > 0 ? media.join(' and ') : 'Movies'));

    if (profile.TargetClientName) items.push('Client: ' + profile.TargetClientName);
    if (profile.TargetIpAddress) items.push('IP filter: ' + profile.TargetIpAddress);
    if (profile.TargetDeviceIds && profile.TargetDeviceIds.length > 0) {
        items.push(`Device filter: ${profile.TargetDeviceIds.length} device(s)`);
    }

    if (profile.TurnOffLightsOnPlay) {
        items.push('Lights off during playback');
    } else if (profile.PlaySceneId) {
        items.push('Activates scene on play');
    } else {
        const brightness = profile.PlayBrightness !== undefined ? profile.PlayBrightness : PROFILE_DEFAULTS.PlayBrightness;
        items.push(`Dims to ${brightness}% on play`);
    }

    if (profile.BridgeId && bridges.length > 1) {
        const bridge = bridges.find(b => b.Id === profile.BridgeId);
        items.push('Bridge: ' + (bridge ? `${bridge.Name} (${bridge.IpAddress})` : 'Unknown'));
    }

    if (profile.EnableOutroLights) items.push('Outro detection enabled');
    return items;
}

// Addresses compare the way the server's BridgeChanges compares them: trimmed, ignoring case
const normalizeAddress = address => String(address || '').trim().toLowerCase();

// Add a bridge to the given list, or update the entry that already has this Id or address so a re-added
// bridge never becomes a duplicate. Keeps the existing Id so profiles that reference it stay linked, and
// keeps the existing name unless the user typed one ('Bridge' is the modal's pre-filled default). Returns
// the stored entry.
function upsertBridge(bridges, bridge) {
    const existing = bridges.find(b => b.Id === bridge.Id || normalizeAddress(b.IpAddress) === normalizeAddress(bridge.IpAddress));
    if (!existing) {
        bridges.push(bridge);
        return bridge;
    }
    if (bridge.Name && bridge.Name !== 'Bridge') existing.Name = bridge.Name;
    existing.IpAddress = bridge.IpAddress;
    existing.Username = bridge.Username;
    if (bridge.HardwareId !== undefined) existing.HardwareId = bridge.HardwareId;
    return existing;
}

// An editor number as saved: clamped to min..max, and a blank or non-numeric field read as min
// (validateEditorNumbers stops Save and Test before that can matter)
function numberFrom(input, min, max) {
    const value = parseInt(input.value, 10);
    return isNaN(value) ? min : Math.max(min, Math.min(max, value));
}

const isBlankNumber = input => isNaN(parseInt(input.value, 10));

function formatRelativeTime(isoDate) {
    if (!isoDate) return 'never';
    const then = new Date(isoDate).getTime();
    if (isNaN(then)) return 'unknown';
    if (then < Date.UTC(2000, 0, 1)) return 'never'; // DateTime.MinValue or similar sentinel
    const minutes = Math.round((Date.now() - then) / 60000);
    if (minutes < 1) return 'just now';
    if (minutes < 60) return minutes + 'm ago';
    const hours = Math.round(minutes / 60);
    if (hours < 48) return hours + 'h ago';
    const days = Math.round(hours / 24);
    if (days < 30) return days + 'd ago';
    return new Date(isoDate).toLocaleDateString();
}

// Short display label for a device: "Name · App", optionally with the IP of its active session
function describeKnownDevice(device, includeIp) {
    let label = device.name;
    if (device.app) label += ' · ' + device.app;
    if (includeIp && device.ip) label += ' · ' + device.ip;
    return label;
}

// Map DeviceId -> remote IP for sessions currently connected to the server.
// RemoteEndPoint is the bare address (no port). Keys are client-controlled, hence the null prototype.
function sessionIpsByDevice(sessions) {
    const map = Object.create(null);
    (sessions || []).forEach(session => {
        if (!session || !session.DeviceId || !session.RemoteEndPoint) return;
        map[String(session.DeviceId).toLowerCase()] = String(session.RemoteEndPoint);
    });
    return map;
}

// The server matches device IDs case-insensitively; mirror that here
const sameDeviceId = (a, b) => String(a).toLowerCase() === String(b).toLowerCase();

// Select savedValue, keeping it even when the bridge offers no such option. A room or scene deleted on the
// bridge matches nothing, and the browser then reports the select as empty - which the next save would write
// back, silently retargeting the profile at All Lights or no scene. Carry the stored value in an option of
// its own, flagged so it is obvious it needs attention.
function setSelectValuePreservingUnknown(select, savedValue, label) {
    if (!savedValue) return;
    select.value = savedValue;
    if (select.value === savedValue) return;
    select.appendChild(new Option(label || `Unresolved (${savedValue}) — re-select`, savedValue));
    select.value = savedValue;
}

function resetSelect(select, value, text) {
    select.innerHTML = '';
    select.appendChild(new Option(text, value));
}

// Rooms and zones, keyed by grouped_light id (the value a profile stores), under "All Lights"
function fillGroupSelect(select, groups) {
    // Keep what the select holds: the saved id, or what the user picked while the list loaded
    const chosen = select.value;
    resetSelect(select, '0', 'All Lights');
    [['room', 'Rooms'], ['zone', 'Zones']].forEach(([type, label]) => {
        const items = Object.keys(groups)
            .filter(id => groups[id].Type === type)
            .map(id => ({ id, name: groups[id].Name }))
            .sort((a, b) => a.name.localeCompare(b.name));
        if (items.length === 0) return;
        const optgroup = document.createElement('optgroup');
        optgroup.label = label;
        items.forEach(item => optgroup.appendChild(new Option(item.name, item.id)));
        select.appendChild(optgroup);
    });
    setSelectValuePreservingUnknown(select, chosen);
}

function fillSceneSelect(select, scenes) {
    const chosen = select.value;
    resetSelect(select, '', 'None (Use Brightness)');
    Object.keys(scenes).forEach(id => {
        select.appendChild(new Option(`${scenes[id].Name} (Group: ${scenes[id].GroupName})`, id));
    });
    // An id this bridge does not offer stays as a flagged option
    setSelectValuePreservingUnknown(select, chosen);
}

// One Play, Pause or Stop section of the Lights tab
function renderStateSection(state) {
    const ids = stateIds(state);
    const id = selector => selector.slice(1);
    const sliderRow = (kind, slider, input, desc, value) => {
        const spec = SLIDERS[kind];
        const step = spec.step ? ` step="${spec.step}"` : '';
        return `<div class="hue-slider-row">
                <input type="range" id="${id(slider)}" min="${spec.min}" max="${spec.max}" value="${value}" />
                <input type="number" id="${id(input)}" is="emby-input" class="hue-number" min="${spec.min}" max="${spec.max}" value="${value}"${step} />
            </div>
            <div id="${id(desc)}" class="fieldDescription">${spec.describe(value)}</div>`;
    };
    const fields = stateFields(state);
    const turnOff = !state.turnOff ? '' : `
        <div class="checkboxContainer checkboxContainer-withDescription" id="${id(ids.turnOffContainer)}">
            <label>
                <input type="checkbox" is="emby-checkbox" id="${id(ids.turnOff)}" />
                <span>Turn Off Lights</span>
            </label>
            <div class="fieldDescription">Completely turn off lights during playback</div>
        </div>`;
    const grace = !state.gracePeriod ? '' : `
        <div class="checkboxContainer hue-check-tight">
            <label>
                <input type="checkbox" is="emby-checkbox" id="${id(ids.graceEnabled)}" />
                <span>Grace Period</span>
            </label>
        </div>
        <div id="${id(ids.graceContainer)}" class="inputContainer hue-subfield" style="display:none;">
            <label class="inputLabel" for="${id(ids.grace)}">Seconds</label>
            <input type="number" id="${id(ids.grace)}" is="emby-input" class="hue-number" min="1" max="600" value="30" />
            <div class="fieldDescription">Skip pause lighting during the first N seconds of playback</div>
        </div>`;
    return `<div class="hue-state-section" data-state="${state.name}">
        <div class="hue-state-header">
            <h3>${state.name}</h3>
            <button is="emby-button" type="button" class="raised hue-test-button" data-test-action="${state.name}">Test</button>
        </div>
        <div class="hue-test-error" id="${id(ids.error)}" style="display:none;"></div>
        <div class="selectContainer">
            <select id="${id(ids.scene)}" is="emby-select" label="Scene">
                <option value="">None (Use Brightness)</option>
            </select>
        </div>${turnOff}
        <div id="${id(ids.brightnessContainer)}" class="inputContainer">
            <label class="inputLabel" for="${id(ids.brightness)}">Brightness</label>
            ${sliderRow('brightness', ids.brightnessSlider, ids.brightness, ids.brightnessDesc, PROFILE_DEFAULTS[fields.brightness])}
        </div>
        <div class="checkboxContainer hue-check-tight">
            <label>
                <input type="checkbox" is="emby-checkbox" id="${id(ids.transitionEnabled)}" />
                <span>Custom Transition Duration</span>
            </label>
        </div>
        <div id="${id(ids.transitionContainer)}" class="inputContainer hue-subfield" style="display:none;">
            ${sliderRow('transition', ids.transitionSlider, ids.transition, ids.transitionDesc, PROFILE_DEFAULTS[fields.transition])}
        </div>${grace}
    </div>`;
}

// Test button feedback. 'busy' disables the button and shows a spinner; 'ok' and 'fail' show an icon and
// restore idleHtml after 2 s; 'idle' restores it at once. Any call cancels a pending restore.
function showTestState(button, state, idleHtml) {
    if (!button) return;
    if (button.hueTestTimer) {
        clearTimeout(button.hueTestTimer);
        button.hueTestTimer = null;
    }
    if (state === 'busy') {
        button.disabled = true;
        button.innerHTML = '<span class="hue-spinner"></span>';
        return;
    }
    button.disabled = false;
    if (state === 'idle') {
        button.innerHTML = idleHtml;
        return;
    }
    button.innerHTML = state === 'ok'
        ? '<span class="material-icons hue-test-icon hue-test-ok">check</span>'
        : '<span class="material-icons hue-test-icon hue-test-fail">error_outline</span>';
    button.hueTestTimer = setTimeout(() => {
        button.hueTestTimer = null;
        button.innerHTML = idleHtml;
    }, 2000);
}

// Runs one light action for a profile (saved or not). Resolves to { Success, Error }; never rejects.
function runTest(action, profile) {
    const unreachable = { Success: false, Error: 'Could not reach the server; check the server logs' };
    return ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl('api/hueplugin/test'),
        data: JSON.stringify({ Action: action, Profile: profile }),
        contentType: 'application/json',
        dataType: 'json'
    }).then(result => result || unreachable).catch(error => {
        console.error('Test failed:', error);
        return unreachable;
    });
}

// ---------------------------------------------------------------------------------------------------------
// The save queue: one per browser tab, shared by every instance of this page
// ---------------------------------------------------------------------------------------------------------

// Every configuration read and write runs here, one after another: first loads, saves, reloads and pairing
// attempts. A page Jellyfin re-creates while an earlier instance's save is still out loads what that save
// stored.
let queue = Promise.resolve();

function enqueue(step) {
    const run = queue.then(step);
    queue = run.catch(() => {});
    return run;
}

// ---------------------------------------------------------------------------------------------------------
// The page
// ---------------------------------------------------------------------------------------------------------

export default function HueConfigPage(view) {
    // ---- Elements and state ----

    const $ = selector => view.querySelector(selector);
    // Captured before they move to body (see "Modals and lifecycle"); looked up inside from then on
    const overlays = {
        bridgeModal: $('#bridgeModal'),
        profileEditorModal: $('#profileEditorModal')
    };
    const editor = selector => overlays.profileEditorModal.querySelector(selector);
    const bridgeField = selector => overlays.bridgeModal.querySelector(selector);

    const state = {
        // False until the configuration GET succeeds; commit() saves nothing before that
        loaded: false,
        // Set by teardown(); isGone() also covers a view Jellyfin removed without viewdestroy
        destroyed: false,
        bridges: [],
        profiles: [],
        // Latest check per bridge Id: { state: 'checking'|'ok'|'failed', text, result }
        bridgeStatus: {},
        // Bumped by every renderBridges; a check answered for an earlier render is ignored
        bridgeVerifyGeneration: 0,
        // The open editor: { profileId, isNew, base }; null while it is closed. Async results compare against it.
        editorSession: null,
        // Bumped by every loadEditorTargets and when the editor closes; an older rooms-and-scenes answer is ignored
        editorTargetsGeneration: 0,
        profileDeviceIds: [],
        knownDevices: [],
        knownDevicesLoading: false,
        // Id of the profile card being dragged; null when no card drag is in progress
        draggedProfileId: null,
        // The open bridge modal: { mode: 'add'|'edit', bridgeId, ended, countdown }; every async result checks it
        // is still the live one
        bridgeSession: null
    };

    const isGone = () => state.destroyed || !view.isConnected;

    // ---- Queue operations ----

    // Errors that must stay until read: a Jellyfin dialog, not a toast. The dialog puts the message through
    // innerHTML, so it is escaped here; the title is set as text.
    function showError(title, detail) {
        Dashboard.alert({ title, message: escapeHtml(detail) });
    }

    // A modal's save button while its commit is out: disabled and reading "Saving…"
    function setSaving(button, saving, idleText) {
        button.disabled = saving;
        button.querySelector('span').textContent = saving ? 'Saving…' : idleText;
    }

    // Profiles need a bridge with a key, and nothing is editable before the configuration has loaded
    function updateConfigSectionVisibility() {
        const hasKey = state.bridges.some(b => b.Username && b.Username.length > 0);
        $('#configSection').style.display = state.loaded && hasKey ? 'block' : 'none';
    }

    // An empty page after a failed load would look like a fresh install, and a save from it would overwrite
    // everything, so the failure replaces the bridge list and offers Retry
    function renderLoadFailure(error) {
        const container = $('#bridgesList');
        container.innerHTML = '<div class="hue-test-error hue-load-error"></div>' +
            '<button is="emby-button" type="button" class="raised" id="retryLoadButton"><span>Retry</span></button>';
        container.querySelector('.hue-test-error').textContent = `Could not load the plugin configuration: ${describeError(error)}.`;
        container.querySelector('#retryLoadButton').addEventListener('click', load);
    }

    function load() {
        state.loaded = false;
        $('#addBridgeButton').disabled = true;
        $('#bridgesList').innerHTML = '<div class="fieldDescription">Loading…</div>';
        updateConfigSectionVisibility();
        Dashboard.showLoadingMsg();
        enqueue(() => {
            if (isGone()) return null;
            return ApiClient.getPluginConfiguration(PLUGIN_ID).then(config => {
                if (isGone()) return;
                state.bridges = config.Bridges || [];
                state.profiles = config.Profiles || [];
                renderBridges();
                renderProfiles();
                state.loaded = true;
                updateConfigSectionVisibility();
                $('#addBridgeButton').disabled = false;
            });
        }).catch(error => {
            console.error('Error fetching configuration:', error);
            state.loaded = false;
            if (!isGone()) renderLoadFailure(error);
        }).then(() => Dashboard.hideLoadingMsg());
    }

    // The one way the page saves. mutate(draft) changes draft.Bridges / draft.Profiles in place, addressing
    // entries by Id, and returns false (after telling the user why) to save nothing. The copy becomes what the
    // page shows only once the server has stored it. Resolves true when stored; never rejects. A page that is
    // gone by the time its turn comes saves nothing.
    function commit(mutate) {
        if (!state.loaded) {
            showError('Not saved', 'The configuration has not finished loading.');
            return Promise.resolve(false);
        }
        return enqueue(() => {
            if (isGone()) return false;
            // Checked again when the commit runs: a Retry may have started loading the configuration again
            if (!state.loaded) throw new Error('the configuration was being reloaded when this change was to be saved');
            const draft = { Bridges: deepCopy(state.bridges), Profiles: deepCopy(state.profiles) };
            if (mutate(draft) === false) return false;
            return ApiClient.getPluginConfiguration(PLUGIN_ID).then(config => {
                config.Bridges = draft.Bridges;
                config.Profiles = draft.Profiles;
                return ApiClient.updatePluginConfiguration(PLUGIN_ID, config);
            }).then(() => {
                adoptLists(draft.Bridges, draft.Profiles);
                return true;
            });
        }).catch(error => {
            console.error('Saving the configuration failed', error);
            if (!isGone()) showError('Not saved', 'The configuration could not be saved: ' + describeError(error));
            return false;
        });
    }

    // Stored lists become the page's confirmed state; the bridge cards (and their checks) are redrawn only
    // when the bridge list itself changed
    function adoptLists(bridges, profiles) {
        if (isGone()) return;
        const bridgesChanged = JSON.stringify(bridges) !== JSON.stringify(state.bridges);
        state.bridges = bridges;
        state.profiles = profiles;
        renderProfiles();
        if (bridgesChanged) renderBridges();
        updateConfigSectionVisibility();
    }

    // One pairing attempt, run on the queue: POST authenticate and, when the bridge paired, reload the stored lists
    // in the same step. The server stores the pairing itself, so no save of this page can land in between and
    // overwrite it. Resolves { result, adopted }; adopted is false when the reload failed (the user has been told).
    // Rejects only when the authenticate request itself failed.
    function pairOnce(request) {
        return enqueue(() => {
            if (isGone() || !state.loaded) return { result: { Success: false }, adopted: false };
            return ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('api/hueplugin/authenticate'),
                data: JSON.stringify(request),
                contentType: 'application/json',
                dataType: 'json'
            }).then(result => {
                if (!result || !result.Success || isGone()) return { result: result || { Success: false }, adopted: false };
                return ApiClient.getPluginConfiguration(PLUGIN_ID).then(config => {
                    adoptLists(config.Bridges || [], config.Profiles || []);
                    return { result, adopted: true };
                }, error => {
                    console.error('Reloading the configuration failed', error);
                    showError('Could not refresh', `The bridge list could not be reloaded: ${describeError(error)}. Reload the page.`);
                    return { result, adopted: false };
                });
            });
        });
    }

    // ---- Bridges list ----

    const bridgeCard = bridgeId =>
        Array.from($('#bridgesList').querySelectorAll('.bridge-card')).find(card => card.dataset.bridgeId === bridgeId) || null;

    // Bridge cards, then a check of every bridge that has a key. Results live in bridgeStatus by bridge Id; a
    // result from an earlier render is ignored (see updateBridgeStatus).
    function renderBridges() {
        const generation = ++state.bridgeVerifyGeneration;
        state.bridgeStatus = {};
        const container = $('#bridgesList');
        if (state.bridges.length === 0) {
            container.innerHTML = '<div class="fieldDescription">No bridges configured. Click "Add Bridge" to discover and pair a Hue bridge.</div>';
            return;
        }

        container.innerHTML = '<div class="hue-card-stack">' + state.bridges.map(bridge => {
            const hasKey = !!bridge.Username;
            return `<div class="hue-card bridge-card" data-bridge-id="${escapeHtml(bridge.Id)}">
                <div class="hue-card-header">
                    <div class="hue-card-main">
                        <h3 class="hue-card-title">${escapeHtml(bridge.Name || 'Bridge')}</h3>
                        <div class="hue-card-subtitle">${escapeHtml(bridge.IpAddress)}</div>
                        <div class="bridge-info"></div>
                        <div class="bridge-status ${hasKey ? 'bridge-status--checking' : 'bridge-status--nokey'}">
                            <span class="bridge-status-dot"></span><span class="bridge-status-text">${hasKey ? 'Checking...' : 'Not authenticated'}</span>
                        </div>
                    </div>
                    <div class="hue-card-actions">
                        <button is="emby-button" type="button" class="raised" data-action="edit-bridge"><span>Edit</span></button>
                        <button is="emby-button" type="button" class="raised hue-button-quiet" data-action="delete-bridge"><span>Delete</span></button>
                    </div>
                </div>
            </div>`;
        }).join('') + '</div>';

        state.bridges.forEach(bridge => {
            if (!bridge.Username) return;
            const bridgeId = bridge.Id;
            state.bridgeStatus[bridgeId] = { state: 'checking', text: 'Checking...', result: null };
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('api/hueplugin/verifyconnection'),
                data: JSON.stringify({ BridgeIp: bridge.IpAddress, Username: bridge.Username }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(result => {
                updateBridgeStatus(generation, bridgeId, result.Success, result.Success ? 'Connected' : (result.Error || 'Auth failed'), result);
            }).catch(() => {
                updateBridgeStatus(generation, bridgeId, false, 'Connection error');
            });
        });
    }

    function updateBridgeStatus(generation, bridgeId, success, text, result) {
        // Sent before the latest render: the list, this bridge's address or its key may have changed since
        if (isGone() || generation !== state.bridgeVerifyGeneration) return;
        state.bridgeStatus[bridgeId] = { state: success ? 'ok' : 'failed', text, result: result || null };
        const card = bridgeCard(bridgeId);
        if (!card) return;
        const status = card.querySelector('.bridge-status');
        status.classList.remove('bridge-status--checking', 'bridge-status--nokey', 'bridge-status--ok', 'bridge-status--failed');
        status.classList.add(success ? 'bridge-status--ok' : 'bridge-status--failed');
        status.querySelector('.bridge-status-text').textContent = text;
        // Facts from /api/0/config, present whenever the bridge answered at all
        if (result && result.HardwareId) {
            card.querySelector('.bridge-info').textContent = `${result.HardwareId} · ${result.ModelId || '?'} · software ${result.SoftwareVersion || '?'}`;
        }
    }

    // Profiles that use this bridge now, or would after the delete: the server sends a profile with no bridge
    // chosen to the only bridge, so with two bridges those would start using the other one
    const profilesUsingBridge = (profiles, bridges, bridgeId) =>
        profiles.filter(p => p.BridgeId === bridgeId || (!p.BridgeId && bridges.length <= 2));

    function deleteBridge(bridgeId) {
        const bridge = state.bridges[indexOfId(state.bridges, bridgeId)];
        if (!bridge) return;
        const affected = profilesUsingBridge(state.profiles, state.bridges, bridgeId);
        if (affected.length > 0) {
            Dashboard.alert(`Cannot delete this bridge. The following profiles still use it: ${affected.map(p => `"${p.Name}"`).join(', ')}. Reassign or delete those profiles first.`);
            return;
        }
        if (!confirm(`Delete bridge "${bridge.Name}"?`)) return;

        // One save; the server drops the removed bridge's cached rooms and pin on it
        commit(draft => {
            const i = indexOfId(draft.Bridges, bridgeId);
            if (i < 0) {
                showError('Not saved', 'This bridge was already removed.');
                return false;
            }
            // Checked again on the lists being saved: a profile save that was still out may have moved a profile here
            const nowUsing = profilesUsingBridge(draft.Profiles, draft.Bridges, bridgeId);
            if (nowUsing.length > 0) {
                showError('Not deleted', `These profiles now use this bridge: ${nowUsing.map(p => `"${p.Name}"`).join(', ')}. Reassign or delete them first.`);
                return false;
            }
            draft.Bridges.splice(i, 1);
        });
    }

    // ---- Bridge modal ----

    // Open the bridge modal as a new session: 'add', or 'edit' for the bridge with bridgeId. Results from an
    // earlier session (a pairing retry, a verification, a discovery still out) see that their session is over and
    // change nothing here.
    function openBridgeModal(mode, bridgeId) {
        const bridge = mode === 'edit' ? state.bridges[indexOfId(state.bridges, bridgeId)] : null;
        if (mode === 'edit' && !bridge) return;
        endBridgeSession();
        state.bridgeSession = { mode, bridgeId: bridge ? bridge.Id : null, ended: false, countdown: null };

        bridgeField('#bridgeModalTitle').textContent = bridge ? 'Edit Bridge' : 'Add Hue Bridge';
        bridgeField('#bridgeName').value = bridge ? (bridge.Name || '') : 'Bridge';
        bridgeField('#bridgeIp').value = bridge ? (bridge.IpAddress || '') : '';
        bridgeField('#bridgeKey').value = bridge ? (bridge.Username || '') : '';
        const select = bridgeField('#bridgeDiscoverySelect');
        select.innerHTML = '';
        select.onchange = null;
        bridgeField('#bridgeDiscoverySelector').style.display = 'none';
        showPairingProgress(false);
        bridgeField('#authenticateBridge span').textContent = 'Authenticate';
        bridgeField('#saveBridge span').textContent = 'Save';
        setBridgeBusy(false);
        openModal('bridgeModal');
    }

    function endBridgeSession() {
        const session = state.bridgeSession;
        if (!session) return;
        session.ended = true;
        if (session.countdown) {
            clearInterval(session.countdown);
            session.countdown = null;
        }
    }

    const isLiveBridgeSession = session => !!session && session === state.bridgeSession && !session.ended;

    // While pairing, verifying or saving only Cancel stays usable, so a retry can never use values that differ
    // from the screen
    function setBridgeBusy(busy) {
        ['#bridgeName', '#bridgeIp', '#bridgeKey', '#bridgeDiscoverySelect', '#discoverBridge', '#authenticateBridge', '#saveBridge']
            .forEach(selector => { bridgeField(selector).disabled = busy; });
    }

    function showPairingProgress(visible) {
        bridgeField('#bridgeAuthProgress').style.display = visible ? 'block' : 'none';
        if (!visible) {
            bridgeField('#bridgeAuthProgressText').textContent = '';
            bridgeField('#bridgeAuthCountdown').textContent = '';
        }
    }

    // Another bridge than bridgeId already at this address
    const addressTakenByAnother = (bridges, address, bridgeId) =>
        bridges.some(b => b.Id !== bridgeId && normalizeAddress(b.IpAddress) === normalizeAddress(address));

    // In Edit, an address that differs from the stored one and belongs to another bridge; the user is told
    function refusedAddress(session, address) {
        if (session.mode !== 'edit') return false;
        const stored = state.bridges[indexOfId(state.bridges, session.bridgeId)];
        const changed = !stored || normalizeAddress(address) !== normalizeAddress(stored.IpAddress);
        if (!changed || !addressTakenByAnother(state.bridges, address, session.bridgeId)) return false;
        Dashboard.alert('Another bridge already uses this address.');
        return true;
    }

    function discoverBridge() {
        const session = state.bridgeSession;
        Dashboard.showLoadingMsg();

        ApiClient.getJSON(ApiClient.getUrl('api/hueplugin/discover')).then(bridges => {
            Dashboard.hideLoadingMsg();
            // Cancelled or reopened while discovery ran: the results belong to that closed session
            if (!isLiveBridgeSession(session)) return;
            const ipInput = bridgeField('#bridgeIp');
            // Pairing or verification started meanwhile and works from the address it read; do not change it under them
            if (ipInput.disabled) return;

            if (bridges && bridges.length > 1) {
                const select = bridgeField('#bridgeDiscoverySelect');
                select.innerHTML = '';
                bridges.forEach(b => select.appendChild(new Option(`${b.InternalIpAddress} (${b.Id || 'unknown'})`, b.InternalIpAddress)));
                select.onchange = () => { ipInput.value = select.value; };
                bridgeField('#bridgeDiscoverySelector').style.display = 'block';
                ipInput.value = bridges[0].InternalIpAddress;
                Dashboard.alert(`Found ${bridges.length} bridges. Select one.`);
            } else if (bridges && bridges.length === 1) {
                bridgeField('#bridgeDiscoverySelector').style.display = 'none';
                ipInput.value = bridges[0].InternalIpAddress;
                Dashboard.alert('Bridge found at ' + bridges[0].InternalIpAddress);
            } else {
                Dashboard.alert('No Hue bridges found. You can enter the IP address manually.');
            }
        }).catch(error => {
            Dashboard.hideLoadingMsg();
            console.error('Bridge discovery failed:', error);
            if (!isLiveBridgeSession(session)) return;
            Dashboard.alert('Failed to discover bridges. Check your network connection.');
        });
    }

    // Save: Add checks the key and merges into an entry with the same Id or address; Edit checks the bridge only
    // when its address or key changed, so a rename saves while the bridge is offline
    function saveBridge() {
        const session = state.bridgeSession;
        const name = bridgeField('#bridgeName').value.trim() || 'Bridge';
        const ip = bridgeField('#bridgeIp').value.trim();
        const key = bridgeField('#bridgeKey').value.trim();
        if (!ip) {
            Dashboard.alert("Enter the bridge's IP address.");
            return;
        }
        if (!key) {
            Dashboard.alert('Authenticate with the bridge or paste an API key.');
            return;
        }

        let stored = null;
        if (session.mode === 'edit') {
            stored = state.bridges[indexOfId(state.bridges, session.bridgeId)];
            if (!stored) {
                showError('Not saved', 'This bridge was removed.');
                return;
            }
            if (refusedAddress(session, ip)) return;
        }

        const buttonText = bridgeField('#saveBridge span');
        const live = () => isLiveBridgeSession(session);
        const idle = () => {
            setBridgeBusy(false);
            buttonText.textContent = 'Save';
        };

        const store = hardwareId => {
            buttonText.textContent = 'Saving…';
            let storedName = name;
            commit(draft => {
                if (session.mode === 'add') {
                    storedName = upsertBridge(draft.Bridges, { Id: generateGuid(), Name: name, IpAddress: ip, Username: key, HardwareId: hardwareId || '' }).Name;
                    return;
                }
                const i = indexOfId(draft.Bridges, session.bridgeId);
                if (i < 0) {
                    showError('Not saved', 'This bridge was removed.');
                    return false;
                }
                // Checked again on the lists being saved: a bridge added meanwhile may have this address
                if (normalizeAddress(ip) !== normalizeAddress(draft.Bridges[i].IpAddress) && addressTakenByAnother(draft.Bridges, ip, session.bridgeId)) {
                    showError('Not saved', 'Another bridge already uses this address.');
                    return false;
                }
                Object.assign(draft.Bridges[i], { Name: name, IpAddress: ip, Username: key });
            }).then(saved => {
                // A save already sent completes even if the modal was cancelled meanwhile
                if (!live()) {
                    if (saved && session.mode === 'add') Dashboard.alert(`Bridge "${storedName}" added!`);
                    return;
                }
                idle();
                if (!saved) return;
                closeModal('bridgeModal');
                if (session.mode === 'add') Dashboard.alert(`Bridge "${storedName}" added!`);
            });
        };

        setBridgeBusy(true);
        if (stored && normalizeAddress(ip) === normalizeAddress(stored.IpAddress) && key === stored.Username) {
            store();
            return;
        }

        buttonText.textContent = 'Verifying...';
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('api/hueplugin/verifyconnection'),
            data: JSON.stringify({ BridgeIp: ip, Username: key }),
            contentType: 'application/json',
            dataType: 'json'
        }).then(result => {
            // Cancelled while verifying: nothing has been stored, so nothing is saved
            if (!live()) return;
            if (!result.Success) {
                idle();
                Dashboard.alert('Connection failed: ' + (result.Error || 'Unknown error'));
                return;
            }
            store(result.HardwareId);
        }).catch(error => {
            console.error('Verify connection failed:', error);
            if (!live()) return;
            idle();
            Dashboard.alert('Could not verify bridge connection. Check the IP address and try again.');
        });
    }

    // Pair with the bridge at the typed address: up to three attempts, three seconds apart, while the user presses
    // the link button. Edit sends the bridge's Id, so the server gives that entry the new key, address and pin.
    // Everything is tied to the session that started it.
    function authenticateBridge() {
        const session = state.bridgeSession;
        const ip = bridgeField('#bridgeIp').value.trim();
        const name = bridgeField('#bridgeName').value.trim() || 'Bridge';
        if (!ip) {
            Dashboard.alert("Enter the bridge's IP address.");
            return;
        }
        if (refusedAddress(session, ip)) return;

        const request = { BridgeIp: ip, BridgeName: name };
        if (session.mode === 'edit') request.BridgeId = session.bridgeId;
        const buttonText = bridgeField('#authenticateBridge span');
        const progressText = bridgeField('#bridgeAuthProgressText');
        const countdown = bridgeField('#bridgeAuthCountdown');
        const maxAttempts = 3;
        let attempt = 0;

        const live = () => isLiveBridgeSession(session);
        const idle = () => {
            setBridgeBusy(false);
            buttonText.textContent = 'Authenticate';
        };

        setBridgeBusy(true);
        showPairingProgress(true);

        function tryPair() {
            attempt++;
            buttonText.textContent = `Attempt ${attempt}/${maxAttempts}...`;
            progressText.textContent = `Attempting to pair... (${attempt}/${maxAttempts})`;
            countdown.textContent = '';

            pairOnce(request).then(({ result, adopted }) => {
                if (result.Success) {
                    onBridgePaired(session, result, adopted, ip, name);
                    return;
                }
                // Cancelled: no further attempts, and nothing shown in a modal that has moved on
                if (!live()) return;
                if (attempt < maxAttempts) {
                    let remaining = 3;
                    countdown.textContent = `Retrying in ${remaining}s...`;
                    const interval = setInterval(() => {
                        if (!live()) {
                            clearInterval(interval);
                            return;
                        }
                        remaining--;
                        if (remaining > 0) {
                            countdown.textContent = `Retrying in ${remaining}s...`;
                        } else {
                            clearInterval(interval);
                            session.countdown = null;
                            countdown.textContent = '';
                            tryPair();
                        }
                    }, 1000);
                    session.countdown = interval;
                } else {
                    idle();
                    progressText.textContent = `Authentication failed after ${maxAttempts} attempts.`;
                    countdown.textContent = 'Press the link button on your Hue bridge and try again.';
                }
            }).catch(error => {
                console.error('Authentication error:', error);
                if (!live()) return;
                idle();
                showPairingProgress(false);
                Dashboard.alert('Authentication failed. Check the server logs for details.');
            });
        }

        tryPair();
    }

    // The server stored the pairing and pairOnce adopted the stored lists (unless that reload failed). Saves a name
    // the user typed (the server keeps an existing entry's name), then tells the user whether or not they are
    // still in the modal.
    function onBridgePaired(session, result, adopted, ip, typedName) {
        const live = () => isLiveBridgeSession(session);
        if (live()) {
            bridgeField('#bridgeKey').value = result.Username;
            bridgeField('#bridgeAuthProgressText').textContent = 'Paired; saving…';
            bridgeField('#bridgeAuthCountdown').textContent = '';
        }

        const finish = stored => {
            if (!live()) {
                if (stored) Dashboard.alert(`Pairing with ${ip} completed; the bridge was saved.`);
                return;
            }
            setBridgeBusy(false);
            bridgeField('#authenticateBridge span').textContent = 'Authenticate';
            showPairingProgress(false);
            if (!stored) return;
            closeModal('bridgeModal');
            Dashboard.alert(session.mode === 'edit' ? `Bridge "${stored.Name}" paired again.` : `Bridge "${stored.Name}" added and authenticated!`);
        };

        if (!adopted) {
            finish(null);
            return;
        }
        const storedBridge = () => state.bridges[indexOfId(state.bridges, result.Id)] || null;
        const bridge = storedBridge();
        if (!bridge) {
            showError('Bridge not shown', 'The paired bridge is not in the stored configuration, probably because another change was saved at the same moment. Pair it again.');
            finish(null);
            return;
        }
        // 'Bridge' is Add's pre-filled name, never a rename; in Edit the typed name is the one the user wants
        const rename = session.mode === 'edit' ? bridge.Name !== typedName : typedName !== 'Bridge' && bridge.Name !== typedName;
        if (!rename) {
            finish(bridge);
            return;
        }
        commit(draft => {
            const i = indexOfId(draft.Bridges, result.Id);
            if (i < 0) {
                showError('Not saved', 'The paired bridge was removed before its name could be saved.');
                return false;
            }
            draft.Bridges[i].Name = typedName;
        }).then(saved => finish(saved ? storedBridge() : null));
    }

    // ---- Profiles list ----

    const profileCard = profileId =>
        Array.from($('#profilesList').querySelectorAll('.profile-card')).find(card => card.dataset.profileId === profileId) || null;

    function renderProfiles() {
        try {
        const container = $('#profilesList');
        const profiles = state.profiles;
        if (profiles.length === 0) {
            container.innerHTML = '<div class="fieldDescription">No profiles configured. Add your first profile to get started!</div>';
            return;
        }

        const item = (action, icon, text, attributes = '') =>
            `<div class="hue-dropdown-item" data-action="${action}"${attributes}><span class="material-icons">${icon}</span>${text}</div>`;
        container.innerHTML = '<div class="hue-card-stack">' + profiles.map((profile, index) => {
            const disabled = profile.Enabled === false;
            return `<div class="hue-card profile-card${disabled ? ' is-disabled' : ''}" draggable="true" data-profile-id="${escapeHtml(profile.Id)}">
                <div class="hue-card-header">
                    <h3 class="hue-card-title">${escapeHtml(profile.Name || 'Unnamed Profile')}${disabled ? ' <span class="hue-card-flag">Disabled</span>' : ''}</h3>
                    <div class="profile-menu">
                        <button is="emby-button" type="button" class="raised profile-menu-button" data-action="toggle-menu">&#x22EE;</button>
                        <div class="hue-dropdown">
                            ${item('edit', 'edit', 'Edit')}
                            ${item('test', 'play_arrow', 'Test Play', ' data-test-action="Play"')}
                            ${item('test', 'pause', 'Test Pause', ' data-test-action="Pause"')}
                            ${item('test', 'stop', 'Test Stop', ' data-test-action="Stop"')}
                            ${item('duplicate', 'content_copy', 'Duplicate')}
                            ${index > 0 ? item('move-up', 'arrow_upward', 'Move Up') : ''}
                            ${index < profiles.length - 1 ? item('move-down', 'arrow_downward', 'Move Down') : ''}
                            ${item('delete', 'delete', 'Delete')}
                        </div>
                    </div>
                </div>
                <div class="hue-test-error profile-test-error" style="display:none;"></div>
                <ul class="hue-card-summary">${profileSummary(profile, state.bridges).map(line => `<li>${escapeHtml(line)}</li>`).join('')}</ul>
            </div>`;
        }).join('') + '</div>';
        } catch (error) {
            console.error('Error rendering profiles:', error);
            Dashboard.alert('Error rendering profiles: ' + error.message);
        }
    }

    function closeProfileMenus() {
        $('#profilesList').querySelectorAll('.hue-dropdown.is-open').forEach(menu => menu.classList.remove('is-open'));
    }

    function toggleProfileMenu(card) {
        const menu = card.querySelector('.hue-dropdown');
        const open = !menu.classList.contains('is-open');
        closeProfileMenus();
        menu.classList.toggle('is-open', open);
    }

    // Test Play / Pause / Stop from a profile card's menu: the saved profile
    function testProfile(profileId, action) {
        const profile = state.profiles[indexOfId(state.profiles, profileId)];
        const card = profileCard(profileId);
        if (!profile || !card) return;
        card.querySelector('.profile-test-error').style.display = 'none';
        showTestState(card.querySelector('.profile-menu-button'), 'busy');

        runTest(action, profile).then(result => {
            // The list may have been redrawn while the test ran; the result goes to the card as it is now
            const current = profileCard(profileId);
            if (!current) return;
            showTestState(current.querySelector('.profile-menu-button'), result.Success ? 'ok' : 'fail', '&#x22EE;');
            if (!result.Success) {
                const errorLine = current.querySelector('.profile-test-error');
                errorLine.textContent = result.Error || 'Test failed; check the server logs';
                errorLine.style.display = '';
            }
        });
    }

    // Appended to the list. The copy is taken from the lists being saved, so a profile whose delete was saved
    // first is reported instead of brought back.
    function duplicateProfile(profileId) {
        commit(draft => {
            const i = indexOfId(draft.Profiles, profileId);
            if (i < 0) {
                showError('Not saved', 'This profile was removed before it could be duplicated.');
                return false;
            }
            const copy = deepCopy(draft.Profiles[i]);
            copy.Id = generateGuid();
            copy.Name = draft.Profiles[i].Name + ' (Copy)';
            draft.Profiles.push(copy);
        });
    }

    function moveProfile(profileId, direction) {
        commit(draft => {
            const from = indexOfId(draft.Profiles, profileId);
            if (from < 0) {
                showError('Not saved', 'This profile was removed before it could be moved.');
                return false;
            }
            const to = from + direction;
            if (to < 0 || to >= draft.Profiles.length) return false;
            const moved = draft.Profiles.splice(from, 1)[0];
            draft.Profiles.splice(to, 0, moved);
        });
    }

    function deleteProfile(profileId) {
        if (!confirm('Are you sure you want to delete this profile?')) return;
        commit(draft => {
            const i = indexOfId(draft.Profiles, profileId);
            if (i < 0) {
                showError('Not saved', 'This profile was already removed.');
                return false;
            }
            draft.Profiles.splice(i, 1);
        });
    }

    // Reorder by dragging a card onto another. Only a drag that started on a card of this list carries the
    // private type, so selected text, links or files dropped on a card do nothing.
    const isProfileDrag = e =>
        state.draggedProfileId !== null && Array.prototype.indexOf.call(e.dataTransfer.types, DRAG_TYPE) >= 0;
    const cardOf = e => (e.target instanceof Element ? e.target.closest('.profile-card') : null);

    function clearDropMarks() {
        $('#profilesList').querySelectorAll('.hue-drop-before, .hue-drop-after')
            .forEach(card => card.classList.remove('hue-drop-before', 'hue-drop-after'));
    }

    function wireProfileDrag() {
        const list = $('#profilesList');
        list.addEventListener('dragstart', e => {
            const card = cardOf(e);
            if (!card) return;
            state.draggedProfileId = card.dataset.profileId;
            e.dataTransfer.setData(DRAG_TYPE, card.dataset.profileId);
            e.dataTransfer.effectAllowed = 'move';
            card.classList.add('hue-dragging');
        });
        list.addEventListener('dragend', e => {
            state.draggedProfileId = null;
            const card = cardOf(e);
            if (card) card.classList.remove('hue-dragging');
            clearDropMarks();
        });
        list.addEventListener('dragover', e => {
            const card = cardOf(e);
            if (!card || !isProfileDrag(e)) return;
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
            const from = indexOfId(state.profiles, state.draggedProfileId);
            const to = indexOfId(state.profiles, card.dataset.profileId);
            card.classList.toggle('hue-drop-after', from < to);
            card.classList.toggle('hue-drop-before', from >= to);
        });
        list.addEventListener('dragleave', e => {
            const card = cardOf(e);
            if (card) card.classList.remove('hue-drop-before', 'hue-drop-after');
        });
        list.addEventListener('drop', e => {
            const card = cardOf(e);
            if (!card || !isProfileDrag(e)) return;
            e.preventDefault();
            e.stopPropagation();
            const draggedId = state.draggedProfileId;
            const targetId = card.dataset.profileId;
            clearDropMarks();
            if (draggedId === targetId) return;

            commit(draft => {
                const from = indexOfId(draft.Profiles, draggedId);
                const to = indexOfId(draft.Profiles, targetId);
                if (from < 0 || to < 0) {
                    showError('Not saved', 'A profile was removed before the move could be saved.');
                    return false;
                }
                const moved = draft.Profiles.splice(from, 1)[0];
                draft.Profiles.splice(to, 0, moved);
            });
        });
    }

    // ---- Profile editor ----

    // The only way the editor opens: a new profile, a template and an edit all come through here
    function openProfileEditor(profile, isNew, title) {
        const base = deepCopy(Object.assign({}, PROFILE_DEFAULTS, profile));
        state.editorSession = { profileId: base.Id, isNew, base };
        editor('#profileEditorTitle').textContent = title;
        fillEditor(base);
        populateBridgeDropdown(base.BridgeId, !isNew);
        loadEditorTargets({ group: base.TargetGroupId, play: base.PlaySceneId, pause: base.PauseSceneId, stop: base.StopSceneId });
        syncEditorVisibility();
        resetEditorTests();
        setSaving(editor('#saveProfileButton'), false, 'Save Profile');
        switchTab('general');
        openModal('profileEditorModal');
    }

    function editProfile(profileId) {
        const profile = state.profiles[indexOfId(state.profiles, profileId)];
        if (profile) openProfileEditor(profile, false, 'Edit Profile');
    }

    // templateId null: a blank profile
    function addProfile(templateId) {
        const template = TEMPLATES.find(t => t.id === templateId);
        const values = Object.assign({}, template ? template.values : {}, { Id: generateGuid() });
        openProfileEditor(values, true, template ? 'Add Profile from Template' : 'Add Profile');
    }

    function setSlider(kind, sliderSelector, inputSelector, descSelector, value) {
        editor(inputSelector).value = value;
        editor(sliderSelector).value = value;
        editor(descSelector).textContent = SLIDERS[kind].describe(value);
    }

    function fillEditor(profile) {
        editor('#profileName').value = profile.Name || '';
        editor('#profileEnabled').checked = profile.Enabled !== false;
        editor('#profileEnableForMovies').checked = profile.EnableForMovies !== false;
        editor('#profileEnableForTvShows').checked = !!profile.EnableForTvShows;
        editor('#profileClientName').value = profile.TargetClientName || '';
        editor('#profileIpAddress').value = profile.TargetIpAddress || '';
        editor('#profileEnableOutroLights').checked = !!profile.EnableOutroLights;

        STATES.forEach(s => {
            const ids = stateIds(s);
            const fields = stateFields(s);
            setSlider('brightness', ids.brightnessSlider, ids.brightness, ids.brightnessDesc, profile[fields.brightness]);
            editor(ids.transitionEnabled).checked = !!profile[fields.transitionEnabled];
            setSlider('transition', ids.transitionSlider, ids.transition, ids.transitionDesc, profile[fields.transition]);
            if (s.turnOff) editor(ids.turnOff).checked = !!profile.TurnOffLightsOnPlay;
            if (s.gracePeriod) {
                const grace = profile.PauseGracePeriodSeconds || 0;
                editor(ids.graceEnabled).checked = grace > 0;
                editor(ids.grace).value = grace > 0 ? grace : 30;
            }
        });

        state.profileDeviceIds = (profile.TargetDeviceIds || []).slice();
        renderProfileDeviceIdList();
        editor('#profileDeviceIdInputRow').style.display = 'none';
        editor('#profileShowAddDeviceId').style.display = '';
        refreshKnownDevices();
    }

    // The profile as the editor shows it, saved or not. Starts from the profile as opened, so a field the
    // page has no control for survives an edit.
    function readEditorProfile() {
        const session = state.editorSession;
        const profile = Object.assign(deepCopy(session.base), {
            Id: session.profileId,
            Name: editor('#profileName').value,
            Enabled: editor('#profileEnabled').checked,
            BridgeId: editor('#profileBridgeId').value,
            EnableForMovies: editor('#profileEnableForMovies').checked,
            EnableForTvShows: editor('#profileEnableForTvShows').checked,
            TargetClientName: editor('#profileClientName').value,
            // A copy: the editor stays open while saving, and its device list must not change the saved profile
            TargetDeviceIds: state.profileDeviceIds.slice(),
            TargetIpAddress: editor('#profileIpAddress').value,
            TargetGroupId: editor('#profileTargetGroupId').value,
            EnableOutroLights: editor('#profileEnableOutroLights').checked
        });
        STATES.forEach(s => {
            const ids = stateIds(s);
            const fields = stateFields(s);
            profile[fields.scene] = editor(ids.scene).value;
            profile[fields.brightness] = numberFrom(editor(ids.brightness), SLIDERS.brightness.min, SLIDERS.brightness.max);
            profile[fields.transitionEnabled] = editor(ids.transitionEnabled).checked;
            profile[fields.transition] = numberFrom(editor(ids.transition), SLIDERS.transition.min, SLIDERS.transition.max);
            if (s.turnOff) profile.TurnOffLightsOnPlay = editor(ids.turnOff).checked;
            if (s.gracePeriod) {
                profile.PauseGracePeriodSeconds = editor(ids.graceEnabled).checked ? numberFrom(editor(ids.grace), 1, 600) : 0;
            }
        });
        return profile;
    }

    // The only show/hide rule in the editor. Brightness is ignored while a scene is selected, and Play's
    // brightness while Turn Off is ticked; the transition and grace fields follow their checkboxes.
    function syncEditorVisibility() {
        const show = (selector, visible) => { editor(selector).style.display = visible ? '' : 'none'; };
        STATES.forEach(s => {
            const ids = stateIds(s);
            const sceneChosen = editor(ids.scene).value !== '';
            const turnOff = s.turnOff && editor(ids.turnOff).checked;
            if (s.turnOff) show(ids.turnOffContainer, !sceneChosen);
            show(ids.brightnessContainer, !sceneChosen && !turnOff);
            show(ids.transitionContainer, editor(ids.transitionEnabled).checked);
            if (s.gracePeriod) show(ids.graceContainer, editor(ids.graceEnabled).checked);
        });
    }

    // The first visible number field that is blank or not a number, as a message naming it; null when all are
    // usable. action limits the check to one section ('Play', 'Pause', 'Stop'); null checks all.
    function validateEditorNumbers(action) {
        const fields = [];
        STATES.forEach(s => fields.push({ state: s, input: stateIds(s).brightness, container: stateIds(s).brightnessContainer, name: `${s.name} brightness` }));
        STATES.forEach(s => fields.push({ state: s, input: stateIds(s).transition, container: stateIds(s).transitionContainer, name: `${s.name} transition` }));
        STATES.filter(s => s.gracePeriod).forEach(s => fields.push({ state: s, input: stateIds(s).grace, container: stateIds(s).graceContainer, name: `the ${s.name.toLowerCase()} grace period` }));
        for (const field of fields) {
            if (action && field.state.name !== action) continue;
            // A hidden field is not in use: its scene is selected or its checkbox is off
            if (editor(field.container).style.display === 'none') continue;
            if (isBlankNumber(editor(field.input))) return `Enter a value for ${field.name}.`;
        }
        return null;
    }

    function bindSlider(kind, sliderSelector, inputSelector, descSelector) {
        const spec = SLIDERS[kind];
        const slider = editor(sliderSelector);
        const input = editor(inputSelector);
        const desc = editor(descSelector);
        slider.addEventListener('input', () => {
            input.value = slider.value;
            desc.textContent = spec.describe(parseInt(slider.value, 10));
        });
        input.addEventListener('input', () => {
            const value = parseInt(input.value, 10);
            if (isNaN(value)) {
                desc.textContent = 'Enter a value';
                return;
            }
            const shown = Math.max(spec.min, Math.min(spec.max, value));
            slider.value = shown;
            desc.textContent = spec.describe(shown);
        });
    }

    function switchTab(tabName) {
        const overlay = overlays.profileEditorModal;
        overlay.querySelectorAll('.profile-tab-content').forEach(content => {
            content.style.display = content.id === `tab-${tabName}` ? 'block' : 'none';
        });
        overlay.querySelectorAll('.profile-tab').forEach(tab => tab.classList.toggle('active', tab.dataset.tab === tabName));
        // A new tab starts at its top: the body scrolls on large screens, the whole sheet on small ones
        const tabBar = overlay.querySelector('.hue-tab-bar');
        overlay.querySelector('.hue-modal-body').scrollTop = 0;
        if (overlay.scrollTop > tabBar.offsetTop) overlay.scrollTop = tabBar.offsetTop;
    }

    // Every editor Test button back to "Test", every error line hidden
    function resetEditorTests() {
        overlays.profileEditorModal.querySelectorAll('.hue-test-button').forEach(button => showTestState(button, 'idle', 'Test'));
        STATES.forEach(s => { editor(stateIds(s).error).style.display = 'none'; });
    }

    // Test button on an editor section: the values on screen, saved or not
    function testEditorAction(button) {
        const action = button.dataset.testAction;
        const errorLine = editor(`#profileTest${action}Error`);
        errorLine.style.display = 'none';

        // A blank number would test a value nobody typed
        const problem = validateEditorNumbers(action);
        if (problem) {
            errorLine.textContent = problem;
            errorLine.style.display = '';
            return;
        }
        const session = state.editorSession;
        showTestState(button, 'busy');

        runTest(action, readEditorProfile()).then(result => {
            // The editor was closed or reopened while this test was out; the result belongs to that session
            if (session !== state.editorSession) return;
            showTestState(button, result.Success ? 'ok' : 'fail', 'Test');
            if (!result.Success) {
                errorLine.textContent = result.Error || 'Test failed; check the server logs';
                errorLine.style.display = '';
            }
        });
    }

    function saveCurrentProfile() {
        const profile = readEditorProfile();

        if (!profile.Name || !profile.Name.trim()) {
            Dashboard.alert('Please enter a profile name.');
            return;
        }
        if (!profile.EnableForMovies && !profile.EnableForTvShows) {
            Dashboard.alert('At least one media type (Movies or TV Shows) must be enabled.');
            return;
        }
        const numberProblem = validateEditorNumbers(null);
        if (numberProblem) {
            Dashboard.alert(numberProblem);
            return;
        }
        // With two or more bridges an empty BridgeId matches nothing at playback
        if (state.bridges.length >= 2 && !profile.BridgeId) {
            Dashboard.alert('Choose a bridge for this profile.');
            return;
        }

        // The editor closes only once the server has the profile; a failed save keeps what was typed. Retrying
        // reuses the session's profile Id, so a new profile is never added twice.
        const session = state.editorSession;
        const saveButton = editor('#saveProfileButton');
        setSaving(saveButton, true);
        commit(draft => {
            const index = indexOfId(draft.Profiles, profile.Id);
            if (index >= 0) {
                draft.Profiles[index] = profile;
            } else if (!session.isNew) {
                showError('Not saved', 'This profile was removed while the editor was open.');
                return false;
            } else {
                draft.Profiles.push(profile);
            }
        }).then(saved => {
            // The editor was closed or reopened meanwhile; this result belongs to that session
            if (session !== state.editorSession) return;
            setSaving(saveButton, false, 'Save Profile');
            if (saved) closeModal('profileEditorModal');
        });
    }

    // Bridge select in the profile editor. An existing profile keeps its saved bridge even when that bridge
    // failed its check or has no key - the option is labelled, not disabled - so opening and saving a profile
    // can never move it to another bridge. Unusable bridges are disabled for everything else, except that
    // failed bridges stay selectable when no bridge is usable at all. An empty saved id follows the server's
    // FindProfileBridge rule: the only bridge, otherwise a choice the user has to make.
    function populateBridgeDropdown(savedBridgeId, isExisting) {
        const select = editor('#profileBridgeId');
        const bridges = state.bridges;
        select.innerHTML = '';

        const addOption = (value, text, disabled, first) => {
            const option = new Option(text, value);
            option.disabled = !!disabled;
            if (first) {
                select.insertBefore(option, select.firstChild);
            } else {
                select.appendChild(option);
            }
        };
        const failed = bridge => (state.bridgeStatus[bridge.Id] || {}).state === 'failed';
        const usable = bridge => !!bridge.Username && !failed(bridge);

        select.onchange = () => loadEditorTargets(currentTargetSelections());

        if (bridges.length === 0) {
            addOption('', 'No bridges configured');
            select.value = '';
            return;
        }

        // With no usable bridge at all, disabling the failed ones would leave nothing to choose
        const anyUsable = bridges.some(usable);
        const implicitId = isExisting && !savedBridgeId && bridges.length === 1 ? bridges[0].Id : '';
        const keptId = isExisting ? (savedBridgeId || implicitId) : '';
        bridges.forEach(bridge => {
            let label = `${bridge.Name} (${bridge.IpAddress})`;
            if (!bridge.Username) {
                label += ' (not authenticated)';
            } else if (failed(bridge)) {
                label += ' (connection failed)';
            }
            addOption(bridge.Id, label, bridge.Id !== keptId && (!bridge.Username || (anyUsable && !usable(bridge))));
        });

        if (keptId) {
            select.value = keptId;
            if (select.value !== keptId) {
                addOption(keptId, `Unknown bridge (${keptId})`);
                select.value = keptId;
            }
            return;
        }

        const first = isExisting ? null : bridges.find(usable);
        if (first) {
            select.value = first.Id;
        } else {
            addOption('', 'Choose a bridge', false, true);
            select.value = '';
        }
    }

    // ---- Rooms and scenes ----

    // The editor's current group and scene choices, carried over when the bridge changes
    function currentTargetSelections() {
        const selections = { group: editor('#profileTargetGroupId').value };
        STATES.forEach(s => { selections[s.name.toLowerCase()] = editor(stateIds(s).scene).value; });
        return selections;
    }

    function renderTargetsStatus(text) {
        const status = editor('#profileTargetsStatus');
        status.textContent = text || '';
        status.style.display = text ? '' : 'none';
    }

    // Rooms, zones and scenes for the bridge the editor shows, read from the bridge on every call so ones made
    // in the Hue app since the last open are listed. The selects are rebuilt at once with only the defaults plus
    // this profile's saved ids, so nothing from an earlier editor session survives; an answer for an earlier
    // load (another session or bridge choice) is ignored; a failed load keeps the saved ids and says so.
    function loadEditorTargets(saved) {
        const generation = ++state.editorTargetsGeneration;
        const bridgeId = editor('#profileBridgeId').value;
        const groupSelect = editor('#profileTargetGroupId');
        const sceneSelects = STATES.map(s => editor(stateIds(s).scene));
        // Nothing loads without a bridge, so saved ids then say they are not loaded
        const loadingLabel = bridgeId ? 'Saved selection (loading…)' : 'Saved selection (not loaded)';

        resetSelect(groupSelect, '0', 'All Lights');
        setSelectValuePreservingUnknown(groupSelect, saved.group, loadingLabel);
        STATES.forEach((s, i) => {
            resetSelect(sceneSelects[i], '', 'None (Use Brightness)');
            setSelectValuePreservingUnknown(sceneSelects[i], saved[s.name.toLowerCase()], loadingLabel);
        });
        syncEditorVisibility();
        renderTargetsStatus(null);

        if (!bridgeId) {
            renderTargetsStatus('Choose a bridge to load its rooms and scenes.');
            return;
        }

        const bridge = state.bridges[indexOfId(state.bridges, bridgeId)];
        const bridgeName = bridge ? bridge.Name : 'this bridge';
        const current = () => generation === state.editorTargetsGeneration;

        ApiClient.getJSON(ApiClient.getUrl('api/hueplugin/targets', { bridgeId })).then(targets => {
            if (!current()) return;
            fillGroupSelect(groupSelect, (targets && targets.Groups) || {});
            sceneSelects.forEach(select => fillSceneSelect(select, (targets && targets.Scenes) || {}));
            syncEditorVisibility();
        }).catch(error => {
            if (!current()) return;
            console.error('Error loading rooms, zones and scenes for profile:', error);
            [groupSelect].concat(sceneSelects).forEach(select => {
                Array.from(select.options).forEach(option => {
                    if (option.text === loadingLabel) option.text = 'Saved selection (not loaded)';
                });
            });
            renderTargetsStatus(`Could not load rooms, zones and scenes from ${bridgeName}: ${describeError(error)}. The saved selections are kept.`);
        });
    }

    // ---- Device picker ----

    const findKnownDevice = deviceId => state.knownDevices.find(d => sameDeviceId(d.id, deviceId)) || null;

    // Fetch every device that has signed in to this server (admin-only endpoint), and the active sessions so
    // entries can show the IP they are connecting from. Resolves to the list; on failure leaves the cache
    // untouched and reports via the status line.
    function refreshKnownDevices() {
        const status = editor('#profileKnownDeviceStatus');
        status.textContent = 'Loading devices...';
        state.knownDevicesLoading = true;

        // Sessions are an enrichment only: if that call fails, the picker still works
        const sessions = ApiClient.getJSON(ApiClient.getUrl('Sessions')).catch(() => []);

        return Promise.all([ApiClient.getJSON(ApiClient.getUrl('Devices')), sessions]).then(([devices, activeSessions]) => {
            const ipsByDevice = sessionIpsByDevice(activeSessions);
            state.knownDevices = ((devices && devices.Items) || [])
                .filter(d => d && d.Id)
                .map(d => ({
                    id: d.Id,
                    name: d.CustomName || d.Name || 'Unnamed device',
                    app: d.AppName || '',
                    user: d.LastUserName || '',
                    ip: ipsByDevice[d.Id.toLowerCase()] || '',
                    lastActive: d.DateLastActivity || null
                }))
                .sort((a, b) => (b.lastActive ? new Date(b.lastActive).getTime() : 0) - (a.lastActive ? new Date(a.lastActive).getTime() : 0));
            state.knownDevicesLoading = false;
            status.textContent = '';
            populateKnownDeviceSelect();
            renderProfileDeviceIdList();
            return state.knownDevices;
        }).catch(error => {
            console.error('Failed to load device list', error);
            state.knownDevicesLoading = false;
            status.textContent = 'Could not load the device list from Jellyfin. Enter a Device ID manually below.';
            populateKnownDeviceSelect();
            return state.knownDevices;
        });
    }

    function populateKnownDeviceSelect() {
        const select = editor('#profileKnownDeviceSelect');
        const candidates = state.knownDevices.filter(d => !state.profileDeviceIds.some(id => sameDeviceId(id, d.id)));

        // Rebuilding the options must not discard a pick the user has already made
        const previous = select.value;
        let placeholder = 'Select a device...';
        if (candidates.length === 0) {
            placeholder = 'All known devices already added';
            if (state.knownDevices.length === 0) placeholder = state.knownDevicesLoading ? 'Loading devices...' : 'No devices found';
        }
        resetSelect(select, '', placeholder);
        candidates.forEach(d => {
            let text = describeKnownDevice(d, true);
            if (d.user) text += ' · ' + d.user;
            text += ' · ' + formatRelativeTime(d.lastActive);
            select.appendChild(new Option(text, d.id));
        });
        select.value = previous;
        if (select.value !== previous) select.value = '';

        editor('#profileAddKnownDeviceButton').disabled = candidates.length === 0;
    }

    function renderProfileDeviceIdList() {
        const container = editor('#profileDeviceIdList');
        if (state.profileDeviceIds.length === 0) {
            container.innerHTML = '<div class="hue-empty-note">No device IDs configured</div>';
            return;
        }
        container.innerHTML = state.profileDeviceIds.map(id => {
            const known = findKnownDevice(id);
            const label = known
                ? `<div>${escapeHtml(describeKnownDevice(known))}</div><div class="hue-device-id">${escapeHtml(id)}</div>`
                : `<div class="hue-device-id hue-device-id--only">${escapeHtml(id)}</div>`;
            return `<div class="hue-device-row">
                <div class="hue-device-name">${label}</div>
                <button is="emby-button" type="button" class="raised" data-action="remove-device" data-device-id="${escapeHtml(id)}">Remove</button>
            </div>`;
        }).join('');
    }

    function addDeviceIdToProfile(deviceId) {
        if (state.profileDeviceIds.some(id => sameDeviceId(id, deviceId))) {
            Dashboard.alert('This Device ID is already in the list');
            return false;
        }
        state.profileDeviceIds.push(deviceId);
        renderProfileDeviceIdList();
        populateKnownDeviceSelect();
        return true;
    }

    function addKnownDevice() {
        const deviceId = editor('#profileKnownDeviceSelect').value;
        if (!deviceId) {
            Dashboard.alert('Please select a device');
            return;
        }
        addDeviceIdToProfile(deviceId);
    }

    function addProfileDeviceId() {
        const input = editor('#profileNewDeviceId');
        const deviceId = input.value.trim();
        if (!deviceId) {
            Dashboard.alert('Please enter a Device ID');
            return;
        }
        if (addDeviceIdToProfile(deviceId)) input.value = '';
    }

    function removeProfileDeviceId(deviceId) {
        state.profileDeviceIds = state.profileDeviceIds.filter(id => id !== deviceId);
        renderProfileDeviceIdList();
        populateKnownDeviceSelect();
    }

    // ---- Modals and lifecycle ----

    // An open modal owns one history entry (same URL), the way Jellyfin's own dialogs do, so Back closes the
    // modal instead of navigating away underneath it. The entry keeps the router's state so its index stays
    // intact. closeModal's history.back() lands asynchronously, so don't open a modal in the same task that
    // closed one.
    function openModal(id) {
        const overlay = overlays[id];
        overlay.style.display = 'block';
        overlay.scrollTop = 0;
        overlay.querySelector('.hue-modal-body').scrollTop = 0;
        if (!(history.state && history.state.huePluginModal === id)) {
            history.pushState(Object.assign({}, history.state, { huePluginModal: id }), '', location.href);
        }
    }

    function closeModal(id) {
        overlays[id].style.display = 'none';
        onModalHidden(id);
        if (history.state && history.state.huePluginModal === id) {
            history.back();
        }
    }

    // Every way a modal disappears (its buttons, the backdrop, Back, leaving the page) ends here
    function onModalHidden(id) {
        if (id === 'bridgeModal') endBridgeSession();
        if (id === 'profileEditorModal') {
            state.editorSession = null;
            state.editorTargetsGeneration++;
        }
    }

    // Hides every open modal the current history entry no longer belongs to
    function onPopState() {
        if (isGone()) {
            teardown();
            return;
        }
        const current = history.state && history.state.huePluginModal;
        Object.keys(overlays).forEach(id => {
            if (id !== current && overlays[id].style.display === 'block') {
                overlays[id].style.display = 'none';
                onModalHidden(id);
            }
        });
    }

    // Closes the profile menus and the template menu on a click outside them
    function onDocumentClick(e) {
        if (isGone()) {
            teardown();
            return;
        }
        const target = e.target instanceof Element ? e.target : null;
        if (!target || !target.closest('.profile-menu')) closeProfileMenus();
        const templateButton = $('#addProfileTemplateButton');
        const templateMenu = $('#templateMenu');
        if (!target || (!templateButton.contains(target) && !templateMenu.contains(target))) {
            templateMenu.style.display = 'none';
        }
    }

    // Jellyfin evicted this view (viewdestroy), or removed it without telling (onPopState and onDocumentClick
    // find it gone). Safe to call more than once.
    function teardown() {
        if (state.destroyed) return;
        state.destroyed = true;
        endBridgeSession();
        state.editorSession = null;
        state.editorTargetsGeneration++;
        Object.keys(overlays).forEach(id => overlays[id].remove());
        window.removeEventListener('popstate', onPopState);
        document.removeEventListener('click', onDocumentClick);
    }

    function wireListeners() {
        // Bridges
        $('#addBridgeButton').addEventListener('click', () => openBridgeModal('add'));
        $('#bridgesList').addEventListener('click', e => {
            const card = e.target instanceof Element ? e.target.closest('.bridge-card') : null;
            if (!card) return;
            const action = e.target.closest('[data-action]');
            if (action && action.dataset.action === 'delete-bridge') {
                deleteBridge(card.dataset.bridgeId);
            } else {
                openBridgeModal('edit', card.dataset.bridgeId);
            }
        });
        bridgeField('#discoverBridge').addEventListener('click', discoverBridge);
        bridgeField('#authenticateBridge').addEventListener('click', authenticateBridge);
        bridgeField('#cancelBridge').addEventListener('click', () => closeModal('bridgeModal'));
        bridgeField('#saveBridge').addEventListener('click', saveBridge);

        // Profiles list: one listener for every card, menu item and menu button
        $('#profilesList').addEventListener('click', e => {
            const card = e.target instanceof Element ? e.target.closest('.profile-card') : null;
            if (!card) return;
            const actionElement = e.target.closest('[data-action]');
            const action = actionElement ? actionElement.dataset.action : null;
            if (action === 'toggle-menu') {
                toggleProfileMenu(card);
                return;
            }
            // A click inside an open menu that missed its items
            if (!action && e.target.closest('.profile-menu')) return;
            closeProfileMenus();
            const profileId = card.dataset.profileId;
            if (action === 'test') testProfile(profileId, actionElement.dataset.testAction);
            else if (action === 'duplicate') duplicateProfile(profileId);
            else if (action === 'move-up') moveProfile(profileId, -1);
            else if (action === 'move-down') moveProfile(profileId, 1);
            else if (action === 'delete') deleteProfile(profileId);
            else editProfile(profileId);
        });
        wireProfileDrag();
        $('#addProfileButton').addEventListener('click', () => addProfile(null));
        $('#addProfileTemplateButton').addEventListener('click', () => {
            const menu = $('#templateMenu');
            menu.style.display = menu.style.display === 'block' ? 'none' : 'block';
        });
        $('#templateMenu').addEventListener('click', e => {
            const option = e.target instanceof Element ? e.target.closest('[data-template]') : null;
            if (!option) return;
            $('#templateMenu').style.display = 'none';
            addProfile(option.dataset.template);
        });

        // Profile editor
        editor('#saveProfileButton').addEventListener('click', saveCurrentProfile);
        editor('#cancelProfileButton').addEventListener('click', () => closeModal('profileEditorModal'));
        editor('#stateSections').addEventListener('click', e => {
            const button = e.target instanceof Element ? e.target.closest('.hue-test-button') : null;
            if (button) testEditorAction(button);
        });
        editor('#tab-lights').addEventListener('change', syncEditorVisibility);
        STATES.forEach(s => {
            const ids = stateIds(s);
            bindSlider('brightness', ids.brightnessSlider, ids.brightness, ids.brightnessDesc);
            bindSlider('transition', ids.transitionSlider, ids.transition, ids.transitionDesc);
        });
        overlays.profileEditorModal.querySelectorAll('.profile-tab').forEach(tab => {
            tab.addEventListener('click', () => switchTab(tab.dataset.tab));
        });
        editor('#profileAddDeviceIdButton').addEventListener('click', addProfileDeviceId);
        editor('#profileAddKnownDeviceButton').addEventListener('click', addKnownDevice);
        editor('#profileNewDeviceId').addEventListener('keydown', e => {
            if (e.key === 'Enter') {
                e.preventDefault();
                addProfileDeviceId();
            }
        });
        editor('#profileShowAddDeviceId').addEventListener('click', () => {
            editor('#profileDeviceIdInputRow').style.display = '';
            editor('#profileShowAddDeviceId').style.display = 'none';
            populateKnownDeviceSelect();
            // Re-fetch so a device that just signed in shows up without reopening the editor
            refreshKnownDevices();
            editor('#profileKnownDeviceSelect').focus();
        });
        editor('#profileDeviceIdList').addEventListener('click', e => {
            const button = e.target instanceof Element ? e.target.closest('[data-action="remove-device"]') : null;
            if (button) removeProfileDeviceId(button.dataset.deviceId);
        });

        // Close a modal when a press both starts and ends on its backdrop. A click alone is not enough: selecting
        // text or dragging a slider past the modal's edge ends as a click on the overlay.
        Object.keys(overlays).forEach(id => {
            const overlay = overlays[id];
            overlay.addEventListener('pointerdown', e => { overlay.hueDownOnBackdrop = e.target === overlay; });
            overlay.addEventListener('pointerup', e => { overlay.hueUpOnBackdrop = e.target === overlay; });
            overlay.addEventListener('click', e => {
                if (e.target === overlay && overlay.hueDownOnBackdrop && overlay.hueUpOnBackdrop) closeModal(id);
            });
        });

        // Page lifetime: removed again by teardown()
        window.addEventListener('popstate', onPopState);
        document.addEventListener('click', onDocumentClick);
        // Leaving the page by any other route (drawer link, session expiry) must not strand a modal over the next view
        view.addEventListener('viewbeforehide', () => {
            Object.keys(overlays).forEach(id => {
                overlays[id].style.display = 'none';
                onModalHidden(id);
            });
        });
        view.addEventListener('viewdestroy', teardown);
    }

    // ---- Construction ----

    editor('#stateSections').innerHTML = STATES.map(renderStateSection).join('');
    $('#templateMenu').innerHTML = '<div class="hue-template-title">Select a Template:</div>' + TEMPLATES.map(t =>
        `<button is="emby-button" type="button" class="raised block hue-template-option" data-template="${t.id}"><span>${escapeHtml(t.label)}</span></button>`
    ).join('');

    // The modals live in body. In Jellyfin 12.0 an in-page fixed overlay covers the viewport only because the
    // dashboard overrides .mainAnimatedPage { contain }; in body they stay full-viewport if that override goes,
    // and Back handling and viewbeforehide find them in one place. teardown() removes them again. A copy left in
    // body by an instance Jellyfin dropped without viewdestroy (a view-cache reset) is removed first, or its
    // duplicate ids would confuse label targets and anything else that finds elements by id; a live instance's
    // modals stay.
    Object.keys(overlays).forEach(id => {
        document.querySelectorAll(`body > #${id}`).forEach(other => {
            if (!other.hueIsGone || other.hueIsGone()) other.remove();
        });
        overlays[id].hueIsGone = isGone;
        document.body.appendChild(overlays[id]);
    });

    wireListeners();
    load();
}

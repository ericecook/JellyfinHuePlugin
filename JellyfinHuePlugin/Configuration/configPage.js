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

// Every sentence the page can produce, grouped the way the design spec's message tables are, so a call site
// holds a name and its values and never prose. Entries are plain strings, or functions of the values a call
// site has on hand. Not every entry is wired to a call site yet - validation, discovery-inline, the device
// picker and the two delete confirms move onto this catalog in later tasks; their entries live here now so
// nothing is invented twice.
const MESSAGES = {
    // "This bridge/profile was removed" was eight different sentences before this catalog; now it is one,
    // shared by every place that finds the thing it was about to act on already gone.
    gone: subject => `This ${subject} was removed.`,

    bridges: {
        empty: 'No bridges configured. Click "Add Bridge" to discover and pair a Hue bridge.',
        statusChecking: 'Checking…',
        statusNoKey: 'Not authenticated',
        statusConnected: 'Connected',
        // The check itself can fail for a reason (the bridge answered) or for no reason at all (it never did)
        statusFailed: reason => reason || 'The bridge could not be checked.',
        // confirm, buttons Delete/Cancel
        deleteBridge: name => `Delete bridge "${name}"?`,
        // dialog 'Not deleted'
        bridgeInUse: names => `These profiles use this bridge: ${names}. Reassign or delete them first.`
    },

    bridgeModal: {
        // field errors - wired once the bridge modal gains inline validation
        addressMissing: "Enter the bridge's IP address.",
        keyMissing: 'Authenticate with the bridge, or paste an API key.',
        addressTaken: 'Another bridge already uses this address.',
        // inline, beside the picker
        discoveryMany: n => `Found ${n} bridges — select one.`,
        discoveryOne: ip => `Bridge found at ${ip}.`,
        discoveryNone: 'No bridges answered. Enter the address manually.',
        // Falls back so a request that never reached the server (describeFailure can still return
        // a falsy-adjacent value) never renders as a bare "Discovery failed: "
        discoveryFailed: reason => reason || 'Discovery failed.',
        // The modal's one status line, shared with discovery. Falls back so a Success:false result
        // with an empty Error never renders as an empty line.
        verifyFailed: reason => reason || 'The bridge could not be checked.',
        pairingAttempt: (n, max) => `Attempting to pair… (${n}/${max})`,
        pairingRetry: s => `Retrying in ${s}s…`,
        // Read after every failed attempt, so three failures in a row can read the same reason three
        // times. Falls back for the same empty-Error case testFailed and verifyFailed guard against.
        pairingFailed: reason => reason || 'Pairing failed.',
        pairingSaving: 'Paired; saving…',
        // The authenticate request itself never reached the server - not one failed pairing attempt among
        // three, but the whole exchange failing to happen. Shown in the same progress line as pairingFailed.
        pairingRequestFailed: reason => `The pairing request couldn't be sent: ${reason}`,
        pairedAfterClose: ip => `Pairing with ${ip} finished; the bridge was saved.`,
        addedAfterClose: name => `Bridge "${name}" was saved.`,
        // dialog 'Not saved'
        bridgeNotShown: "The paired bridge isn't in the stored configuration, because another change was saved at the same moment. Pair it again."
    },

    profiles: {
        empty: 'No profiles configured. Add your first profile to get started!',
        // confirm, buttons Delete/Cancel
        deleteProfile: name => `Delete profile "${name}"?`,
        // Falls back so a Success: false result with an empty Error can never render a blank line
        testFailed: reason => reason || 'The test failed.',
        // The test POST itself never reached the server - distinct from testFailed, which is the server
        // answering that the light command failed
        testUnreachable: reason => `The test couldn't be sent: ${reason}`,
        // dialog 'Could not load'
        renderFailed: message => `The profile list could not be drawn: ${message}`,
        // field errors - wired once the editor gains inline validation
        nameMissing: 'Enter a name for this profile.',
        mediaTypeMissing: 'Enable Movies, TV Shows, or both.',
        valueMissing: field => `Enter a value for ${field}.`,
        bridgeMissing: 'Choose a bridge for this profile.',
        targetsNoBridge: 'Choose a bridge to load its rooms and scenes.',
        targetsFailed: (bridge, reason) => `Rooms and scenes couldn't be loaded from ${bridge}: ${reason} The saved selections are kept.`
    },

    devicePicker: {
        // field errors - wired once the device picker gains inline validation
        deviceNotChosen: 'Select a device.',
        deviceIdMissing: 'Enter a Device ID.',
        deviceDuplicate: 'That Device ID is already in the list.',
        devicesFailed: "The device list couldn't be loaded from Jellyfin. Enter a Device ID manually."
    },

    load: {
        failed: reason => `The plugin configuration couldn't be loaded: ${reason}`,
        // dialog 'Not saved'
        notLoaded: "The configuration hasn't finished loading.",
        // dialog 'Not saved'
        saveFailed: reason => `The change couldn't be saved: ${reason}`,
        // dialog 'Could not refresh'
        refreshFailed: reason => `The bridge list couldn't be reloaded: ${reason} Reload the page.`
    }
};

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

// A truthful, admin-facing reason for a failed request. The rejected value is the raw fetch Response
// (Fact 1); its body is read once (Fact 2 - a second read throws) and resolved in order: a JSON object's
// detail or title, or its first errors entry (ASP.NET model validation); a JSON string body (Fact 3); a
// non-empty text body, trimmed; HTTP <status> <statusText> from the status (Fact 4, an empty body); the
// exception's own message; 'unknown error' when nothing else is known.
async function describeFailure(error) {
    const isResponse = error instanceof Response || (error && typeof error.status === 'number' && typeof error.text === 'function');
    if (isResponse) {
        let body = '';
        try {
            body = await error.text();
        } catch (_) {
            body = '';
        }
        if (body) {
            try {
                const parsed = JSON.parse(body);
                if (parsed && typeof parsed === 'object') {
                    if (parsed.detail) return parsed.detail;
                    if (parsed.title) return parsed.title;
                    if (parsed.errors) {
                        const first = Object.values(parsed.errors)[0];
                        const message = Array.isArray(first) ? first[0] : first;
                        if (message) return message;
                    }
                } else if (typeof parsed === 'string' && parsed) {
                    return parsed;
                }
            } catch (_) {
                // Not JSON: falls through to the raw text below
            }
            const trimmed = body.trim();
            if (trimmed) return trimmed;
        }
        return `HTTP ${error.status}${error.statusText ? ' ' + error.statusText : ''}`;
    }
    if (error && error.message) return error.message;
    return 'unknown error';
}

// Where a field's message belongs: right after the block that visually owns it. A field paired with a
// button in a .hue-field-row (the bridge address and key rows; the device picker's select-and-Add and its
// manual-entry-and-Add) would become a third flex item beside that button if the message landed inside the
// row itself, so the message goes after the row instead. Everything else sits in an .inputContainer,
// .selectContainer or .checkboxContainer that the message can simply follow.
function fieldAnchor(input) {
    return input.closest('.hue-field-row') || input.closest('.inputContainer, .selectContainer, .checkboxContainer') || input;
}

// Creates its message element the first time a field is invalid - no markup for this exists in
// configPage.html yet, E wires ARIA to these ids later - right after fieldAnchor's block, and marks the
// field so its own styling can show the invalid state. clearFieldError undoes both.
// aria-describedby is a list, and a slider's number input already names its description line (E's slider
// work). Adding an error id must therefore append, and clearing must remove only that id - assigning over
// the attribute would silently delete the description and leave the field describing nothing once the
// error clears.
function describedBy(input) {
    return (input.getAttribute('aria-describedby') || '').split(/\s+/).filter(Boolean);
}

function addDescribedBy(input, id) {
    const ids = describedBy(input);
    if (!ids.includes(id)) {
        input.setAttribute('aria-describedby', ids.concat(id).join(' '));
    }
}

function removeDescribedBy(input, id) {
    const ids = describedBy(input).filter(existing => existing !== id);
    if (ids.length) {
        input.setAttribute('aria-describedby', ids.join(' '));
    } else {
        input.removeAttribute('aria-describedby');
    }
}

function fieldError(input, text) {
    const id = input.id + 'Error';
    let el = document.getElementById(id);
    if (!el) {
        el = document.createElement('div');
        el.id = id;
        el.className = 'fieldDescription hue-field-error';
        fieldAnchor(input).insertAdjacentElement('afterend', el);
    }
    el.textContent = text;
    el.style.display = '';
    input.classList.add('hue-field-invalid');
    input.setAttribute('aria-invalid', 'true');
    addDescribedBy(input, id);
}

function clearFieldError(input) {
    const el = document.getElementById(input.id + 'Error');
    if (el) el.style.display = 'none';
    input.classList.remove('hue-field-invalid');
    input.removeAttribute('aria-invalid');
    removeDescribedBy(input, input.id + 'Error');
}

// Every field error a modal might still be showing from an earlier session, hidden and unmarked - called
// when the profile editor or the bridge modal opens, so nothing stale survives into the next one.
function clearAllFieldErrors(root) {
    root.querySelectorAll('.hue-field-error').forEach(el => { el.style.display = 'none'; });
    root.querySelectorAll('.hue-field-invalid').forEach(el => {
        el.classList.remove('hue-field-invalid');
        el.removeAttribute('aria-invalid');
        removeDescribedBy(el, el.id + 'Error');
    });
}

// The generic inline-status line: set the text and show or hide with it, so a call site never has to spell
// out style.display itself. Used for Test results, pairing progress and the targets status line alike.
function inlineStatus(el, text) {
    el.textContent = text || '';
    el.style.display = text ? '' : 'none';
}

// A success the page cannot show any other way. Jellyfin escapes this itself (Fact 6): never build it from
// unescaped HTML.
function toast(text) {
    Dashboard.alert(text);
}

// Locks every input, select, textarea and button under root except one marked data-keep-enabled (a modal's
// Cancel button), remembering exactly what it disabled so the matching busy=false call re-enables those and
// nothing else - a bridge option already disabled for having no key, say, stays disabled either way.
function setFormBusy(root, busy) {
    if (busy) {
        const controls = Array.from(root.querySelectorAll('input, select, textarea, button'))
            .filter(el => !el.disabled && !el.hasAttribute('data-keep-enabled'));
        controls.forEach(el => { el.disabled = true; });
        root.hueBusyControls = controls;
    } else {
        (root.hueBusyControls || []).forEach(el => { el.disabled = false; });
        root.hueBusyControls = null;
    }
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
    // labelId names the row's visible <label>. The label's own `for` can only point at one control and
    // points at the number input, so the range is named through aria-labelledby instead; both inputs point
    // at the description line beside them, so "Very Dim (8%)" is announced with the control rather than
    // stranded next to it.
    const sliderRow = (kind, slider, input, desc, value, labelId) => {
        const spec = SLIDERS[kind];
        const step = spec.step ? ` step="${spec.step}"` : '';
        return `<div class="hue-slider-row">
                <input type="range" id="${id(slider)}" min="${spec.min}" max="${spec.max}" value="${value}" aria-labelledby="${labelId}" aria-describedby="${id(desc)}" />
                <input type="number" id="${id(input)}" is="emby-input" class="hue-number" min="${spec.min}" max="${spec.max}" value="${value}"${step} aria-describedby="${id(desc)}" />
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
            <label class="inputLabel" id="${id(ids.brightness)}Label" for="${id(ids.brightness)}">Brightness</label>
            ${sliderRow('brightness', ids.brightnessSlider, ids.brightness, ids.brightnessDesc, PROFILE_DEFAULTS[fields.brightness], `${id(ids.brightness)}Label`)}
        </div>
        <div class="checkboxContainer hue-check-tight">
            <label>
                <input type="checkbox" is="emby-checkbox" id="${id(ids.transitionEnabled)}" />
                <span>Custom Transition Duration</span>
            </label>
        </div>
        <div id="${id(ids.transitionContainer)}" class="inputContainer hue-subfield" style="display:none;">
            <label class="inputLabel" id="${id(ids.transition)}Label" for="${id(ids.transition)}">Transition Duration</label>
            ${sliderRow('transition', ids.transitionSlider, ids.transition, ids.transitionDesc, PROFILE_DEFAULTS[fields.transition], `${id(ids.transition)}Label`)}
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
    // 'busy' disabled this button on its own, ahead of any setFormBusy sweep - a light-command round trip
    // and the save queue have no ordering relationship, so this result can land while the editor's own lock
    // is still up. Re-enabling unconditionally would let this one control slip past a lock everything else
    // is still under. When the modal it lives in is locked, it stays disabled and joins that lock's captured
    // set instead, so the eventual setFormBusy(root, false) re-enables it along with everything else rather
    // than stranding it disabled forever.
    const lockedRoot = button.closest('.hue-modal-overlay');
    if (lockedRoot && lockedRoot.hueBusyControls) {
        if (lockedRoot.hueBusyControls.indexOf(button) < 0) lockedRoot.hueBusyControls.push(button);
    } else {
        button.disabled = false;
    }
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
    return ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl('api/hueplugin/test'),
        data: JSON.stringify({ Action: action, Profile: profile }),
        contentType: 'application/json',
        dataType: 'json'
    }).then(result => result || { Success: false, Error: MESSAGES.profiles.testUnreachable('the server sent no result') })
        .catch(async error => {
            console.error('Test failed:', error);
            return { Success: false, Error: MESSAGES.profiles.testUnreachable(await describeFailure(error)) };
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

    // ---- Messages ----
    // Two of the five channel helpers live here because they need the page's own isGone(); the other three
    // (fieldError/clearFieldError, inlineStatus, toast) are pure and live at module level above.

    // Errors that must stay until read: a Jellyfin dialog, not a toast. The dialog puts the message through
    // innerHTML, so it is escaped here; the title is set as text.
    function showError(title, detail) {
        Dashboard.alert({ title, message: escapeHtml(detail) });
    }

    // Dashboard.confirm never resolves a promise (Fact 7): it always needs a callback, called exactly once,
    // true for the confirm button and false for Cancel and for Back alike - both read as "no". Every value
    // confirm renders is raw HTML (Fact 6); a call site must escape what it puts into options itself.
    function confirmAction(options, onYes) {
        Dashboard.confirm(options, options.title, confirmed => {
            if (confirmed && !isGone()) onYes();
        });
    }

    // ---- Queue operations ----

    // Profiles need a bridge with a key, and nothing is editable before the configuration has loaded
    function updateConfigSectionVisibility() {
        const hasKey = state.bridges.some(b => b.Username && b.Username.length > 0);
        $('#configSection').style.display = state.loaded && hasKey ? 'block' : 'none';
    }

    // An empty page after a failed load would look like a fresh install, and a save from it would overwrite
    // everything, so the failure replaces the bridge list and offers Retry
    function renderLoadFailure(reason) {
        const container = $('#bridgesList');
        container.innerHTML = '<div class="hue-test-error hue-load-error"></div>' +
            '<button is="emby-button" type="button" class="raised" id="retryLoadButton"><span>Retry</span></button>';
        inlineStatus(container.querySelector('.hue-test-error'), MESSAGES.load.failed(reason));
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
        }).catch(async error => {
            console.error('Error fetching configuration:', error);
            state.loaded = false;
            const reason = await describeFailure(error);
            if (!isGone()) renderLoadFailure(reason);
        }).then(() => Dashboard.hideLoadingMsg());
    }

    // The one way the page saves. mutate(draft) changes draft.Bridges / draft.Profiles in place, addressing
    // entries by Id, and returns false (after telling the user why) to save nothing. The copy becomes what the
    // page shows only once the server has stored it. Resolves true when stored; never rejects. A page that is
    // gone by the time its turn comes saves nothing.
    function commit(mutate) {
        if (!state.loaded) {
            showError('Not saved', MESSAGES.load.notLoaded);
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
        }).catch(async error => {
            console.error('Saving the configuration failed', error);
            if (!isGone()) showError('Not saved', MESSAGES.load.saveFailed(await describeFailure(error)));
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
                    if (isGone()) return { result, adopted: false };
                    adoptLists(config.Bridges || [], config.Profiles || []);
                    return { result, adopted: true };
                }, async error => {
                    console.error('Reloading the configuration failed', error);
                    if (isGone()) return { result, adopted: false };
                    showError('Could not refresh', MESSAGES.load.refreshFailed(await describeFailure(error)));
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
            container.innerHTML = `<div class="fieldDescription">${MESSAGES.bridges.empty}</div>`;
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
                            <span class="bridge-status-dot"></span><span class="bridge-status-text">${hasKey ? MESSAGES.bridges.statusChecking : MESSAGES.bridges.statusNoKey}</span>
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
            state.bridgeStatus[bridgeId] = { state: 'checking', text: MESSAGES.bridges.statusChecking, result: null };
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('api/hueplugin/verifyconnection'),
                data: JSON.stringify({ BridgeIp: bridge.IpAddress, Username: bridge.Username }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(result => {
                updateBridgeStatus(generation, bridgeId, result.Success, result.Success ? MESSAGES.bridges.statusConnected : MESSAGES.bridges.statusFailed(result.Error), result);
            }).catch(() => {
                updateBridgeStatus(generation, bridgeId, false, MESSAGES.bridges.statusFailed());
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
            showError('Not deleted', MESSAGES.bridges.bridgeInUse(affected.map(p => `"${p.Name}"`).join(', ')));
            return;
        }
        // Every value confirm renders is raw HTML (Fact 6), so the bridge's name is escaped here, not left to confirmAction
        confirmAction({
            title: 'Delete bridge',
            text: MESSAGES.bridges.deleteBridge(escapeHtml(bridge.Name)),
            confirmText: 'Delete',
            cancelText: 'Cancel',
            primary: 'delete'
        }, () => {
            // One save; the server drops the removed bridge's cached rooms and pin on it
            commit(draft => {
                const i = indexOfId(draft.Bridges, bridgeId);
                if (i < 0) {
                    showError('Not saved', MESSAGES.gone('bridge'));
                    return false;
                }
                // Checked again on the lists being saved: a profile save that was still out may have moved a profile here
                const nowUsing = profilesUsingBridge(draft.Profiles, draft.Bridges, bridgeId);
                if (nowUsing.length > 0) {
                    showError('Not deleted', MESSAGES.bridges.bridgeInUse(nowUsing.map(p => `"${p.Name}"`).join(', ')));
                    return false;
                }
                draft.Bridges.splice(i, 1);
            });
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
        inlineStatus(bridgeField('#bridgeConnectionStatus'), null);
        showPairingProgress(false);
        bridgeField('#authenticateBridge span').textContent = 'Authenticate';
        bridgeField('#saveBridge span').textContent = 'Save';
        clearAllFieldErrors(overlays.bridgeModal);
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

    const isLiveBridgeSession = session => !!session && session === state.bridgeSession && !session.ended && !isGone();

    // While pairing, verifying or saving only Cancel stays usable, so a retry can never use values that differ
    // from the screen. Re-expressed through setFormBusy so the bridge modal and the profile editor lock the
    // same way; every field and button here except Cancel would be disabled by the generic sweep anyway.
    function setBridgeBusy(busy) {
        setFormBusy(overlays.bridgeModal, busy);
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
        fieldError(bridgeField('#bridgeIp'), MESSAGES.bridgeModal.addressTaken);
        return true;
    }

    function discoverBridge() {
        const session = state.bridgeSession;
        Dashboard.showLoadingMsg();
        const status = bridgeField('#bridgeConnectionStatus');

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
                inlineStatus(status, MESSAGES.bridgeModal.discoveryMany(bridges.length));
            } else if (bridges && bridges.length === 1) {
                bridgeField('#bridgeDiscoverySelector').style.display = 'none';
                ipInput.value = bridges[0].InternalIpAddress;
                inlineStatus(status, MESSAGES.bridgeModal.discoveryOne(bridges[0].InternalIpAddress));
            } else {
                inlineStatus(status, MESSAGES.bridgeModal.discoveryNone);
            }
        }).catch(async error => {
            Dashboard.hideLoadingMsg();
            console.error('Bridge discovery failed:', error);
            if (!isLiveBridgeSession(session)) return;
            const reason = await describeFailure(error);
            if (!isLiveBridgeSession(session)) return; // the session may have moved on while the body was read
            inlineStatus(status, MESSAGES.bridgeModal.discoveryFailed(reason));
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
            fieldError(bridgeField('#bridgeIp'), MESSAGES.bridgeModal.addressMissing);
            return;
        }
        if (!key) {
            fieldError(bridgeField('#bridgeKey'), MESSAGES.bridgeModal.keyMissing);
            return;
        }
        inlineStatus(bridgeField('#bridgeConnectionStatus'), null);

        let stored = null;
        if (session.mode === 'edit') {
            stored = state.bridges[indexOfId(state.bridges, session.bridgeId)];
            if (!stored) {
                showError('Not saved', MESSAGES.gone('bridge'));
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

        const store = (hardwareId, verified) => {
            buttonText.textContent = 'Saving…';
            let storedName = name;
            commit(draft => {
                if (session.mode === 'add') {
                    storedName = upsertBridge(draft.Bridges, { Id: generateGuid(), Name: name, IpAddress: ip, Username: key, HardwareId: hardwareId || '' }).Name;
                    return;
                }
                const i = indexOfId(draft.Bridges, session.bridgeId);
                if (i < 0) {
                    showError('Not saved', MESSAGES.gone('bridge'));
                    return false;
                }
                // Checked again on the lists being saved: a bridge added meanwhile may have this address
                if (normalizeAddress(ip) !== normalizeAddress(draft.Bridges[i].IpAddress) && addressTakenByAnother(draft.Bridges, ip, session.bridgeId)) {
                    showError('Not saved', MESSAGES.bridgeModal.addressTaken);
                    return false;
                }
                // Unchanged address and key: only the name was possibly edited, so only the name is written here.
                // A pairing stores its own key and pin server-side and this must never overwrite that.
                if (verified) {
                    Object.assign(draft.Bridges[i], { Name: name, IpAddress: ip, Username: key });
                } else {
                    draft.Bridges[i].Name = name;
                }
            }).then(saved => {
                // A save already sent completes even if the modal was cancelled meanwhile; the page is gone
                // from under it, so only a toast can say it landed
                if (!live()) {
                    if (saved && session.mode === 'add') toast(MESSAGES.bridgeModal.addedAfterClose(storedName));
                    return;
                }
                idle();
                if (!saved) return;
                closeModal('bridgeModal');
                // The card already shows the added bridge once the modal closes; nothing more to say
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
                // The server's own sentence, with no prefix added here - a prefix on both ends is how today's
                // "Connection failed: Connection failed: ..." happens
                inlineStatus(bridgeField('#bridgeConnectionStatus'), MESSAGES.bridgeModal.verifyFailed(result.Error));
                return;
            }
            store(result.HardwareId, true);
        }).catch(async error => {
            console.error('Verify connection failed:', error);
            if (!live()) return;
            idle();
            const reason = await describeFailure(error);
            if (!live()) return; // the session may have moved on while the body was read
            inlineStatus(bridgeField('#bridgeConnectionStatus'), MESSAGES.bridgeModal.verifyFailed(reason));
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
            fieldError(bridgeField('#bridgeIp'), MESSAGES.bridgeModal.addressMissing);
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
            progressText.textContent = MESSAGES.bridgeModal.pairingAttempt(attempt, maxAttempts);
            countdown.textContent = '';

            pairOnce(request).then(({ result, adopted }) => {
                if (result.Success) {
                    onBridgePaired(session, result, adopted, ip, name);
                    return;
                }
                // Cancelled: no further attempts, and nothing shown in a modal that has moved on
                if (!live()) return;
                progressText.textContent = MESSAGES.bridgeModal.pairingFailed(result.Error);
                if (attempt < maxAttempts) {
                    let remaining = 3;
                    countdown.textContent = MESSAGES.bridgeModal.pairingRetry(remaining);
                    const interval = setInterval(() => {
                        if (!live()) {
                            clearInterval(interval);
                            return;
                        }
                        remaining--;
                        if (remaining > 0) {
                            countdown.textContent = MESSAGES.bridgeModal.pairingRetry(remaining);
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
                    countdown.textContent = '';
                }
            }).catch(async error => {
                console.error('Authentication error:', error);
                if (!live()) return;
                idle();
                const reason = await describeFailure(error);
                if (!live()) return; // the session may have moved on while the body was read
                // Not one failed attempt among three (pairingFailed's territory) but the request never
                // reaching the server at all; shown where pairingFailed shows, not as a toast
                progressText.textContent = MESSAGES.bridgeModal.pairingRequestFailed(reason);
                countdown.textContent = '';
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
            bridgeField('#bridgeAuthProgressText').textContent = MESSAGES.bridgeModal.pairingSaving;
            bridgeField('#bridgeAuthCountdown').textContent = '';
        }

        const finish = stored => {
            if (!live()) {
                if (stored) toast(MESSAGES.bridgeModal.pairedAfterClose(ip));
                return;
            }
            setBridgeBusy(false);
            bridgeField('#authenticateBridge span').textContent = 'Authenticate';
            showPairingProgress(false);
            if (!stored) return;
            closeModal('bridgeModal');
            // The card already shows the paired bridge once the modal closes; nothing more to say
        };

        if (!adopted) {
            finish(null);
            return;
        }
        const storedBridge = () => state.bridges[indexOfId(state.bridges, result.Id)] || null;
        const bridge = storedBridge();
        if (!bridge) {
            showError('Not saved', MESSAGES.bridgeModal.bridgeNotShown);
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
                showError('Not saved', MESSAGES.gone('bridge'));
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
                container.innerHTML = `<div class="fieldDescription">${MESSAGES.profiles.empty}</div>`;
                return;
            }

            // A native <button>, not a <div> and not is="emby-button": a real button is what both Tab and
            // Jellyfin's D-pad focusManager accept (spec Facts 3, 4), while .emby-button is one of the
            // selectors Jellyfin suppresses focus outlines on (spec Fact 6) and would strip the focus ring
            // from the one surface this change exists to make keyboard-usable. The class, the icon span and
            // data-action are unchanged, so click handling and the harness's selectors are untouched.
            const item = (action, icon, text, attributes = '') =>
                `<button type="button" class="hue-dropdown-item" data-action="${action}"${attributes}><span class="material-icons">${icon}</span>${text}</button>`;
            container.innerHTML = '<div class="hue-card-stack">' + profiles.map((profile, index) => {
                const disabled = profile.Enabled === false;
                return `<div class="hue-card profile-card${disabled ? ' is-disabled' : ''}" draggable="true" data-profile-id="${escapeHtml(profile.Id)}">
                    <div class="hue-card-header">
                        <h3 class="hue-card-title">${escapeHtml(profile.Name || 'Unnamed Profile')}${disabled ? ' <span class="hue-card-flag">Disabled</span>' : ''}</h3>
                        <div class="profile-menu">
                            <button is="emby-button" type="button" class="raised profile-menu-button" data-action="toggle-menu" aria-haspopup="true" aria-expanded="false" aria-label="Profile actions">&#x22EE;</button>
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
            showError('Could not load', MESSAGES.profiles.renderFailed(error.message));
        }
    }

    // Every close path goes through here - the menu's own item, a click anywhere else (onDocumentClick),
    // Escape - so this is the one place that can guarantee no trigger is left claiming to be expanded.
    function closeProfileMenus() {
        $('#profilesList').querySelectorAll('.hue-dropdown.is-open').forEach(menu => menu.classList.remove('is-open'));
        $('#profilesList').querySelectorAll('.profile-menu-button[aria-expanded="true"]')
            .forEach(button => button.setAttribute('aria-expanded', 'false'));
    }

    function toggleProfileMenu(card) {
        const menu = card.querySelector('.hue-dropdown');
        const open = !menu.classList.contains('is-open');
        closeProfileMenus();
        menu.classList.toggle('is-open', open);
        card.querySelector('.profile-menu-button').setAttribute('aria-expanded', open ? 'true' : 'false');
    }

    // Test Play / Pause / Stop from a profile card's menu: the saved profile
    function testProfile(profileId, action) {
        const profile = state.profiles[indexOfId(state.profiles, profileId)];
        const card = profileCard(profileId);
        if (!profile || !card) return;
        inlineStatus(card.querySelector('.profile-test-error'), null);
        showTestState(card.querySelector('.profile-menu-button'), 'busy');

        runTest(action, profile).then(result => {
            // The list may have been redrawn while the test ran; the result goes to the card as it is now
            const current = profileCard(profileId);
            if (!current) return;
            showTestState(current.querySelector('.profile-menu-button'), result.Success ? 'ok' : 'fail', '&#x22EE;');
            inlineStatus(current.querySelector('.profile-test-error'), result.Success ? null : MESSAGES.profiles.testFailed(result.Error));
        });
    }

    // Appended to the list. The copy is taken from the lists being saved, so a profile whose delete was saved
    // first is reported instead of brought back.
    function duplicateProfile(profileId) {
        commit(draft => {
            const i = indexOfId(draft.Profiles, profileId);
            if (i < 0) {
                showError('Not saved', MESSAGES.gone('profile'));
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
                showError('Not saved', MESSAGES.gone('profile'));
                return false;
            }
            const to = from + direction;
            if (to < 0 || to >= draft.Profiles.length) return false;
            const moved = draft.Profiles.splice(from, 1)[0];
            draft.Profiles.splice(to, 0, moved);
        });
    }

    function deleteProfile(profileId) {
        const profile = state.profiles[indexOfId(state.profiles, profileId)];
        if (!profile) return;
        confirmAction({
            title: 'Delete profile',
            text: MESSAGES.profiles.deleteProfile(escapeHtml(profile.Name)),
            confirmText: 'Delete',
            cancelText: 'Cancel',
            primary: 'delete'
        }, () => {
            commit(draft => {
                const i = indexOfId(draft.Profiles, profileId);
                if (i < 0) {
                    showError('Not saved', MESSAGES.gone('profile'));
                    return false;
                }
                draft.Profiles.splice(i, 1);
            });
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
                    showError('Not saved', MESSAGES.gone('profile'));
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
        clearAllFieldErrors(overlays.profileEditorModal);
        // A save left in flight by a session this reopen abandons (saveCurrentProfile's session check bails
        // out of its own unlock) never gets a matching busy=false; this one guarantees a clean start anyway
        setFormBusy(overlays.profileEditorModal, false);
        editor('#saveProfileButton').querySelector('span').textContent = 'Save Profile';
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

    // Every number field the editor shows: each state's brightness, then each state's transition, then the
    // grace fields - the order Save and the per-section Test buttons have always checked in. action limits
    // the list to one section ('Play', 'Pause', 'Stop'); null lists all.
    function numberFields(action) {
        const fields = [];
        STATES.forEach(s => fields.push({ state: s, input: stateIds(s).brightness, container: stateIds(s).brightnessContainer, name: `${s.name} brightness` }));
        STATES.forEach(s => fields.push({ state: s, input: stateIds(s).transition, container: stateIds(s).transitionContainer, name: `${s.name} transition` }));
        STATES.filter(s => s.gracePeriod).forEach(s => fields.push({ state: s, input: stateIds(s).grace, container: stateIds(s).graceContainer, name: `the ${s.name.toLowerCase()} grace period` }));
        return action ? fields.filter(f => f.state.name === action) : fields;
    }

    // The first field in the list that is visible and blank or not a number; null when all are usable. A
    // hidden field is not in use: its scene is selected or its checkbox is off.
    function firstBlankNumberField(action) {
        return numberFields(action).find(f => editor(f.container).style.display !== 'none' && isBlankNumber(editor(f.input))) || null;
    }

    // The first visible number field that is blank or not a number, as a message naming it; null when all are
    // usable. Only the text is wanted here; the per-section Test buttons don't switch tabs or take focus.
    function validateEditorNumbers(action) {
        const field = firstBlankNumberField(action);
        return field ? MESSAGES.profiles.valueMissing(field.name) : null;
    }

    // The editor's own required fields, walked in tab order so Save can jump to the first one that needs
    // fixing: General (name, then the media-type checkboxes), Lights (the bridge, then each number field in
    // the order above). Filters and Advanced have nothing this checks. Returns null when the profile can be
    // saved as it stands.
    function validateProfile(profile) {
        if (!profile.Name || !profile.Name.trim()) {
            return { tab: 'general', input: editor('#profileName'), message: MESSAGES.profiles.nameMissing };
        }
        if (!profile.EnableForMovies && !profile.EnableForTvShows) {
            return { tab: 'general', input: editor('#profileEnableForMovies'), message: MESSAGES.profiles.mediaTypeMissing };
        }
        if (state.bridges.length >= 2 && !profile.BridgeId) {
            return { tab: 'lights', input: editor('#profileBridgeId'), message: MESSAGES.profiles.bridgeMissing };
        }
        const field = firstBlankNumberField(null);
        if (field) {
            return { tab: 'lights', input: editor(field.input), message: MESSAGES.profiles.valueMissing(field.name) };
        }
        return null;
    }

    function bindSlider(kind, sliderSelector, inputSelector, descSelector) {
        const spec = SLIDERS[kind];
        const slider = editor(sliderSelector);
        const input = editor(inputSelector);
        const desc = editor(descSelector);
        slider.addEventListener('input', () => {
            clearFieldError(input);
            input.value = slider.value;
            desc.textContent = spec.describe(parseInt(slider.value, 10));
        });
        input.addEventListener('input', () => {
            clearFieldError(input);
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
        overlay.querySelectorAll('.profile-tab').forEach(tab => {
            const selected = tab.dataset.tab === tabName;
            tab.classList.toggle('active', selected);
            // aria-selected names the shown tab; the roving tabindex keeps the tablist a single Tab stop,
            // so Tab moves past the bar into the panel rather than through four buttons.
            tab.setAttribute('aria-selected', selected ? 'true' : 'false');
            if (selected) {
                tab.removeAttribute('tabindex');
            } else {
                tab.setAttribute('tabindex', '-1');
            }
        });
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
        inlineStatus(errorLine, null);

        // A blank number would test a value nobody typed
        const problem = validateEditorNumbers(action);
        if (problem) {
            inlineStatus(errorLine, problem);
            return;
        }
        const session = state.editorSession;
        showTestState(button, 'busy');

        runTest(action, readEditorProfile()).then(result => {
            // The editor was closed or reopened while this test was out; the result belongs to that session
            if (session !== state.editorSession) return;
            showTestState(button, result.Success ? 'ok' : 'fail', 'Test');
            inlineStatus(errorLine, result.Success ? null : MESSAGES.profiles.testFailed(result.Error));
        });
    }

    function saveCurrentProfile() {
        const profile = readEditorProfile();

        const invalid = validateProfile(profile);
        if (invalid) {
            switchTab(invalid.tab);
            invalid.input.focus();
            invalid.input.scrollIntoView({ block: 'nearest' });
            fieldError(invalid.input, invalid.message);
            return;
        }

        // The editor closes only once the server has the profile; a failed save keeps what was typed. Retrying
        // reuses the session's profile Id, so a new profile is never added twice. Locked with setFormBusy
        // before the button's own label changes, so the label change is not mistaken for a control that was
        // already disabled for its own reason and left out of what gets re-enabled.
        const session = state.editorSession;
        const saveButton = editor('#saveProfileButton');
        setFormBusy(overlays.profileEditorModal, true);
        saveButton.querySelector('span').textContent = 'Saving…';
        commit(draft => {
            const index = indexOfId(draft.Profiles, profile.Id);
            if (index >= 0) {
                draft.Profiles[index] = profile;
            } else if (!session.isNew) {
                showError('Not saved', MESSAGES.gone('profile'));
                return false;
            } else {
                draft.Profiles.push(profile);
            }
        }).then(saved => {
            // The editor was closed or reopened meanwhile; this result belongs to that session
            if (session !== state.editorSession) return;
            setFormBusy(overlays.profileEditorModal, false);
            saveButton.querySelector('span').textContent = 'Save Profile';
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
        inlineStatus(editor('#profileTargetsStatus'), text);
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
            renderTargetsStatus(MESSAGES.profiles.targetsNoBridge);
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
        }).catch(async error => {
            if (!current()) return;
            console.error('Error loading rooms, zones and scenes for profile:', error);
            const reason = await describeFailure(error);
            if (!current()) return; // the editor may have moved on while the body was read
            [groupSelect].concat(sceneSelects).forEach(select => {
                Array.from(select.options).forEach(option => {
                    if (option.text === loadingLabel) option.text = 'Saved selection (not loaded)';
                });
            });
            renderTargetsStatus(MESSAGES.profiles.targetsFailed(bridgeName, reason));
        });
    }

    // ---- Device picker ----

    const findKnownDevice = deviceId => state.knownDevices.find(d => sameDeviceId(d.id, deviceId)) || null;

    // Fetch every device that has signed in to this server (admin-only endpoint), and the active sessions so
    // entries can show the IP they are connecting from. Resolves to the list; on failure leaves the cache
    // untouched and reports via the status line.
    function refreshKnownDevices() {
        const status = editor('#profileKnownDeviceStatus');
        inlineStatus(status, 'Loading devices...');
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
            inlineStatus(status, null);
            populateKnownDeviceSelect();
            renderProfileDeviceIdList();
            return state.knownDevices;
        }).catch(error => {
            console.error('Failed to load device list', error);
            state.knownDevicesLoading = false;
            inlineStatus(status, MESSAGES.devicePicker.devicesFailed);
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

    // Adds deviceId once: false, after showing why under whichever control was used to try, when it cannot be.
    function addDeviceIdToProfile(deviceId, control) {
        if (state.profileDeviceIds.some(id => sameDeviceId(id, deviceId))) {
            fieldError(control, MESSAGES.devicePicker.deviceDuplicate);
            return false;
        }
        clearFieldError(control);
        state.profileDeviceIds.push(deviceId);
        renderProfileDeviceIdList();
        populateKnownDeviceSelect();
        return true;
    }

    function addKnownDevice() {
        const select = editor('#profileKnownDeviceSelect');
        const deviceId = select.value;
        if (!deviceId) {
            fieldError(select, MESSAGES.devicePicker.deviceNotChosen);
            return;
        }
        addDeviceIdToProfile(deviceId, select);
    }

    function addProfileDeviceId() {
        const input = editor('#profileNewDeviceId');
        const deviceId = input.value.trim();
        if (!deviceId) {
            fieldError(input, MESSAGES.devicePicker.deviceIdMissing);
            return;
        }
        if (addDeviceIdToProfile(deviceId, input)) input.value = '';
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
    // The element that opened the modal, so onModalHidden can put focus back where it came from rather
    // than dropping it on <body>, where the next Tab would restart at the top of the dashboard.
    function focusableIn(overlay) {
        return Array.from(overlay.querySelectorAll('a[href], button, input, select, textarea, [tabindex]'))
            .filter(el => !el.disabled && el.tabIndex !== -1 && el.offsetParent !== null);
    }

    function openModal(id) {
        const overlay = overlays[id];
        state.focusBeforeModal = document.activeElement instanceof HTMLElement ? document.activeElement : null;
        overlay.style.display = 'block';
        overlay.scrollTop = 0;
        overlay.querySelector('.hue-modal-body').scrollTop = 0;
        const first = focusableIn(overlay)[0];
        if (first) first.focus();
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
        // Only if the element is still in the document: the profile list is re-rendered while a modal is
        // open, so the card button that opened it may no longer exist.
        const previous = state.focusBeforeModal;
        state.focusBeforeModal = null;
        if (previous && document.contains(previous)) previous.focus();
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

    // Escape closes the innermost open thing first, so a dropdown inside the profile list closes without
    // also dismissing whatever is behind it. Jellyfin only handles Escape in TV layout (spec Fact 5), so
    // this is the page's own.
    function onDocumentKeydown(e) {
        if (isGone()) {
            teardown();
            return;
        }
        const open = Object.keys(overlays).find(id => overlays[id].style.display === 'block');
        if (e.key === 'Escape') {
            const menuOpen = $('#profilesList').querySelector('.hue-dropdown.is-open');
            if (menuOpen) {
                closeProfileMenus();
                e.preventDefault();
                return;
            }
            if (open) {
                closeModal(open);
                e.preventDefault();
            }
            return;
        }
        // A modal is modal: Tab cycles within it instead of walking into the dashboard behind it.
        if (e.key === 'Tab' && open) {
            const items = focusableIn(overlays[open]);
            if (!items.length) return;
            const first = items[0];
            const last = items[items.length - 1];
            if (e.shiftKey && document.activeElement === first) {
                last.focus();
                e.preventDefault();
            } else if (!e.shiftKey && document.activeElement === last) {
                first.focus();
                e.preventDefault();
            }
        }
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
        document.removeEventListener('keydown', onDocumentKeydown);
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
        bridgeField('#bridgeIp').addEventListener('input', () => clearFieldError(bridgeField('#bridgeIp')));
        bridgeField('#bridgeKey').addEventListener('input', () => clearFieldError(bridgeField('#bridgeKey')));

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
            if (s.gracePeriod) editor(ids.grace).addEventListener('input', () => clearFieldError(editor(ids.grace)));
        });
        // Validation's own fields: each clears only itself, except the two media-type checkboxes, which
        // share one message anchored to the first of them
        editor('#profileName').addEventListener('input', () => clearFieldError(editor('#profileName')));
        editor('#profileEnableForMovies').addEventListener('change', () => clearFieldError(editor('#profileEnableForMovies')));
        editor('#profileEnableForTvShows').addEventListener('change', () => clearFieldError(editor('#profileEnableForMovies')));
        editor('#profileBridgeId').addEventListener('change', () => clearFieldError(editor('#profileBridgeId')));
        overlays.profileEditorModal.querySelectorAll('.profile-tab').forEach(tab => {
            tab.addEventListener('click', () => switchTab(tab.dataset.tab));
        });
        editor('#profileAddDeviceIdButton').addEventListener('click', addProfileDeviceId);
        editor('#profileAddKnownDeviceButton').addEventListener('click', addKnownDevice);
        editor('#profileKnownDeviceSelect').addEventListener('change', () => clearFieldError(editor('#profileKnownDeviceSelect')));
        editor('#profileNewDeviceId').addEventListener('input', () => clearFieldError(editor('#profileNewDeviceId')));
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
        document.addEventListener('keydown', onDocumentKeydown);
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

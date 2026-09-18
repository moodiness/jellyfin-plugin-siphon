(function () {
    'use strict';
    var ui = window.SiphonUi;
    ui.init();
    var root = window.location.pathname.replace(/\/Siphon\/User\/?$/i, '').replace(/\/$/, '');
    var token = null, currentUser = null, serverId = null, generation = 0;
    var deviceId = 'siphon-portal-' + Array.from(crypto.getRandomValues(new Uint8Array(16)), function (value) { return value.toString(16).padStart(2, '0'); }).join('');
    var requests = new Map(), loaded = new Set();
    var preferences = null, preferencesDirty = false, preferencesBusy = false;
    var webhookSettings = null, webhookDirty = false, webhookBusy = false;
    var itemOffset = 0, itemQuery = '', downloadsTimer = null, downloadsSnapshot = null;
    var batch = null, batchExpiryTimer = null;
    var kinds = ['NewEpisode', 'DateChanged', 'AnnouncedRelease'];
    var tabs = Array.from(document.querySelectorAll('[role="tab"]'));
    function get(id) { return document.getElementById(id); }
    function t(key, values) { return ui.t('user.' + key, values); }
    function node(tag, value, parent) { var element = document.createElement(tag); if (value !== undefined) element.textContent = value; if (parent) parent.appendChild(element); return element; }
    function paint(element, render) { element.removeAttribute('data-i18n'); element.dataset.userText = ''; element.siphonText = render; element.textContent = render(); return element; }
    function label(tag, key, values, parent) { return paint(node(tag, undefined, parent), function () { return t(key, typeof values === 'function' ? values() : values); }); }
    function action(parent, key, handler) { var control = label('button', key, null, parent); control.type = 'button'; control.addEventListener('click', handler); return control; }
    function message(id, key, values, error) { var element = typeof id === 'string' ? get(id) : id; paint(element, function () { return key ? t(key, typeof values === 'function' ? values() : values) : ''; }); element.dataset.error = String(Boolean(error)); }
    function failure(id, error, fallback) { var element = typeof id === 'string' ? get(id) : id; paint(element, function () { return ui.errorText(error, t(fallback)); }); element.dataset.error = 'true'; }
    function apiUrl(path) { return root + '/' + path; }
    function stamp(value) { return value && Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleString(ui.locale()) : t('notRecorded'); }
    function size(value) { return Number.isFinite(value) && value >= 0 ? t('size', { size: (value / 1048576).toLocaleString(ui.locale(), { maximumFractionDigits: 1 }) }) : t('unknownSize'); }
    function enumText(value, allowed, fallback) { return allowed.indexOf(value) >= 0 ? t(value) : t(fallback); }
    function fact(parent, key, render) { label('dt', key, null, parent); paint(node('dd', undefined, parent), render); }
    function abortError() { return new DOMException('Obsolete request', 'AbortError'); }
    function authorization() { return 'MediaBrowser Client="Siphon portal", Device="Browser", DeviceId="' + deviceId + '", Version="1.6.0.0"' + (token ? ', Token="' + token + '"' : ''); }
    async function request(path, method, data, signal, allowFailure) {
        var requestToken = token, expected = generation;
        var response = await fetch(apiUrl(path), { method: method || 'GET', credentials: 'omit', cache: 'no-store', referrerPolicy: 'no-referrer', signal: signal, headers: { Authorization: authorization(), 'Content-Type': 'application/json', Accept: 'application/json' }, body: data === undefined ? undefined : JSON.stringify(data) });
        if (expected !== generation || (signal && signal.aborted)) throw abortError();
        if (!response.ok) {
            var error = await ui.error(response, t('requestFailed'));
            if (expected !== generation || (signal && signal.aborted)) throw abortError();
            if (response.status === 401 && requestToken && token === requestToken) endSession('sessionExpired');
            throw error;
        }
        if (response.status === 204) return null;
        var text = await response.text();
        if (expected !== generation || (signal && signal.aborted)) throw abortError();
        var result = text ? JSON.parse(text) : null;
        if (result && result.Success === false && !allowFailure) throw await ui.error({ status: response.status, responseJSON: result }, t('requestFailed'));
        return result;
    }
    function begin(key) { if (requests.has(key)) requests.get(key).abort(); var controller = new AbortController(); controller.focusTarget = document.activeElement; requests.set(key, controller); return controller; }
    function isCurrent(key, controller, expected) { return token && expected === generation && requests.get(key) === controller && !controller.signal.aborted; }
    function finish(key, controller) {
        if (requests.get(key) !== controller) return;
        requests.delete(key);
        var target = controller.focusTarget;
        if (document.activeElement === document.body && target && target.isConnected && !target.disabled && !target.closest('[hidden]')) target.focus({ preventScroll: true });
    }
    function cancel(key, status, text) { if (requests.has(key)) requests.get(key).abort(); requests.delete(key); if (status) message(status, text || 'cancelled'); }
    function cancelSourceActions() { Array.from(requests.keys()).forEach(function (key) { if (/^(prepare|enqueue|ticket)-/.test(key)) cancel(key); }); }
    function clearWebhookSecret() { get('webhookSecret').value = ''; get('webhookSecretPanel').hidden = true; }
    function clearBatch() { clearTimeout(batchExpiryTimer); batchExpiryTimer = null; batch = null; get('batchResults').replaceChildren(); get('batchOutcomes').replaceChildren(); }
    function endSession(key) {
        generation++; token = null; currentUser = null; serverId = null;
        requests.forEach(function (controller) { controller.abort(); }); requests.clear(); loaded.clear();
        clearTimeout(downloadsTimer); downloadsTimer = null; downloadsSnapshot = null; clearBatch();
        preferences = null; preferencesDirty = false; preferencesBusy = false;
        webhookSettings = null; webhookDirty = false; webhookBusy = false; clearWebhookSecret();
        get('authenticated').querySelectorAll('[data-user-text]').forEach(function (element) { element.siphonText = null; element.removeAttribute('data-user-text'); element.textContent = ''; });
        ['calendarList', 'notificationsList', 'sourceResults', 'itemSearchList', 'downloadsList'].forEach(function (id) { get(id).replaceChildren(); get(id).removeAttribute('aria-busy'); });
        ['preferencesStatus', 'calendarStatus', 'notificationsStatus', 'sourcesStatus', 'itemSearchStatus', 'downloadsStatus', 'webhookStatus', 'batchStatus'].forEach(function (id) { message(id, ''); });
        ['loginForm', 'preferencesForm', 'webhookForm', 'itemSearchForm', 'sourcesForm', 'batchForm'].forEach(function (id) { get(id).reset(); });
        document.querySelectorAll('input, select').forEach(function (field) { field.setCustomValidity(''); delete field.dataset.siphonInvalid; });
        ['username', 'password', 'webhookUrl', 'sourceItem', 'itemSearch', 'batchItem', 'webhookSecret'].forEach(function (id) { get(id).value = ''; });
        ['webhookDelivery', 'downloadsQuota', 'downloadsWindow', 'effectivePreferences', 'notificationPermission'].forEach(function (id) { message(id, ''); });
        itemOffset = 0; itemQuery = ''; resetDates();
        get('authenticated').hidden = true; get('signIn').hidden = false; get('signOut').hidden = true; get('loginFields').disabled = false;
        get('itemPrevious').hidden = true; get('itemNext').hidden = true; get('preferencesRetry').hidden = true;
        ['calendarCancel', 'sourcesCancel', 'itemSearchCancel', 'batchCancel'].forEach(function (id) { get(id).hidden = true; });
        ['calendarLoad', 'notificationsLoad', 'sourcesLoad', 'sourcesRefresh', 'itemSearchSubmit', 'downloadsLoad', 'batchPreview'].forEach(function (id) { get(id).disabled = false; });
        get('downloadsAuto').checked = true;
        paint(get('account'), function () { return t('accountHelp'); }); preferenceState(); webhookState();
        message('sessionStatus', key || 'signedOut'); get('username').focus();
    }
    function selectPanel(id, focus) {
        clearTimeout(downloadsTimer); downloadsTimer = null;
        if (id !== 'notificationsPanel') clearWebhookSecret();
        tabs.forEach(function (tab) { var selected = tab.getAttribute('aria-controls') === id; tab.setAttribute('aria-selected', String(selected)); tab.tabIndex = selected ? 0 : -1; get(tab.getAttribute('aria-controls')).hidden = !selected; if (focus && selected) tab.focus(); });
        if (!token) return;
        if (id === 'preferencesPanel' && !loaded.has(id)) loadPreferences();
        if (id === 'calendarPanel' && !loaded.has(id)) loadCalendar();
        if (id === 'notificationsPanel' && !loaded.has(id)) loadNotifications();
        if (id === 'notificationsPanel' && !webhookSettings && !webhookBusy) loadWebhook();
        if (id === 'downloadsPanel') loadDownloads();
    }
    function preferenceState() {
        get('preferencesFields').disabled = !preferences || preferencesBusy;
        get('savePreferences').disabled = !preferencesDirty || preferencesBusy || !preferences;
        get('preferenceSaveBar').dataset.dirty = String(preferencesDirty);
        message('preferenceSaveState', preferencesBusy ? 'working' : !preferences ? 'preferencesUnavailable' : preferencesDirty ? 'unsavedPreferences' : 'preferencesSaved');
        message('reloadPreferences', preferencesDirty ? 'discard' : 'reload');
    }
    function showPreferences(value) {
        preferences = value; get('searchMode').value = value.SearchMode; get('hideUnreleased').value = value.HideUnreleased == null ? 'inherit' : String(value.HideUnreleased);
        get('notificationsEnabled').checked = value.NotificationsEnabled; get('notificationsEnabled').disabled = !value.NotificationsAllowed;
        message('notificationPermission', value.NotificationsAllowed ? 'notificationAllowed' : 'notificationDenied');
        message('effectivePreferences', 'effectivePreferences', function () { return { search: t(value.EffectiveSearchMode === 'Local' ? 'searchLocal' : 'searchAll'), release: t(value.EffectiveHideUnreleased ? 'hideUnreleased' : 'showAnnounced') }; });
        preferencesDirty = false; preferenceState();
    }
    async function loadPreferences() {
        if (!token || preferencesBusy) return;
        var controller = begin('preferences'), expected = generation;
        preferencesBusy = true; preferenceState(); get('preferencesRetry').hidden = true; message('preferencesStatus', 'loadingPreferences');
        try { var value = await request('Siphon/Preferences', 'GET', undefined, controller.signal); if (!isCurrent('preferences', controller, expected)) return; showPreferences(value); loaded.add('preferencesPanel'); message('preferencesStatus', ''); }
        catch (error) { if (isCurrent('preferences', controller, expected)) { failure('preferencesStatus', error, 'preferencesLoadFailed'); get('preferencesRetry').hidden = false; } }
        finally { if (expected === generation) { preferencesBusy = false; preferenceState(); } finish('preferences', controller); }
    }
    async function savePreferences(event) {
        event.preventDefault(); if (!token || !preferences || !preferencesDirty || preferencesBusy) return;
        var draft = { SearchMode: get('searchMode').value, HideUnreleased: get('hideUnreleased').value === 'inherit' ? null : get('hideUnreleased').value === 'true', NotificationsEnabled: get('notificationsEnabled').checked };
        var controller = begin('preferences'), expected = generation;
        preferencesBusy = true; preferenceState(); message('preferencesStatus', 'savingPreferences');
        try { await request('Siphon/Preferences', 'PUT', draft, controller.signal); if (!isCurrent('preferences', controller, expected)) return; var value = await request('Siphon/Preferences', 'GET', undefined, controller.signal); if (!isCurrent('preferences', controller, expected)) return; showPreferences(value); loaded.delete('notificationsPanel'); if (!webhookDirty) webhookSettings = null; message('preferencesStatus', 'preferencesSaved'); }
        catch (error) { if (isCurrent('preferences', controller, expected)) failure('preferencesStatus', error, 'saveUnconfirmed'); }
        finally { if (expected === generation) { preferencesBusy = false; preferenceState(); get('reloadPreferences').focus({ preventScroll: true }); } finish('preferences', controller); }
    }
    function episodeTitle(episode) { var number = (episode.SeasonNumber == null ? '' : 'S' + episode.SeasonNumber) + (episode.EpisodeNumber == null ? '' : ' E' + episode.EpisodeNumber); return (episode.SeriesName ? episode.SeriesName + ' · ' : '') + (number ? number.trim() + ' · ' : '') + episode.Name; }
    function nativeLink(parent, title, itemId) { var link = node('a', title, parent); link.href = apiUrl('web/index.html') + '#/details?id=' + encodeURIComponent(itemId) + '&serverId=' + encodeURIComponent(serverId || ''); return link; }
    function episodeRow(parent, episode) {
        var row = node('li', undefined, parent); nativeLink(node('h3', undefined, row), episodeTitle(episode), episode.Id);
        paint(node('p', undefined, row), function () { return episode.Date + ' · ' + (episode.IsDateOnly ? t('dateOnly') : stamp(episode.AnnouncedReleaseUtc)) + ' · ' + t(episode.AnnouncedReleased ? 'dateReached' : 'upcoming'); }).className = 'help'; return row;
    }
    async function loadCalendar() {
        if (!token) return;
        var from = get('calendarFrom').value, to = get('calendarTo').value, first = Date.parse(from + 'T00:00:00Z'), last = Date.parse(to + 'T00:00:00Z');
        if (!Number.isFinite(first) || !Number.isFinite(last) || last < first || last - first > 89 * 86400000) { message('calendarStatus', 'dateRange', null, true); return; }
        var controller = begin('calendar'), expected = generation;
        get('calendarLoad').disabled = true; get('calendarCancel').hidden = false; get('calendarList').replaceChildren(); get('calendarList').setAttribute('aria-busy', 'true'); message('calendarStatus', 'loadingCalendar');
        try {
            var result = await request('Siphon/Calendar?from=' + encodeURIComponent(from) + '&to=' + encodeURIComponent(to), 'GET', undefined, controller.signal); if (!isCurrent('calendar', controller, expected)) return;
            result.Items.forEach(function (episode) { var row = episodeRow(get('calendarList'), episode); action(row, 'checkSources', function () { get('sourceItem').value = episode.Id; selectPanel('sourcesPanel', true); loadSources(false); }); }); loaded.add('calendarPanel');
            paint(get('calendarStatus'), function () { return (result.Items.length ? t('calendarCount', { count: result.Items.length }) : t('calendarEmpty')) + ' ' + (result.Truncated ? t('calendarTruncated') + ' ' : '') + t('availabilityNote'); });
        } catch (error) { if (isCurrent('calendar', controller, expected)) failure('calendarStatus', error, 'calendarFailed'); }
        finally { if (requests.get('calendar') === controller) { get('calendarLoad').disabled = false; get('calendarCancel').hidden = true; get('calendarList').setAttribute('aria-busy', 'false'); } finish('calendar', controller); }
    }
    async function loadNotifications() {
        if (!token) return;
        var controller = begin('notifications'), expected = generation;
        get('notificationsLoad').disabled = true; get('notificationsList').replaceChildren(); get('notificationsList').setAttribute('aria-busy', 'true'); message('notificationsStatus', 'loadingInbox');
        try {
            var result = await request('Siphon/Notifications', 'GET', undefined, controller.signal); if (!isCurrent('notifications', controller, expected)) return;
            result.Items.forEach(function (entry) {
                var row = episodeRow(get('notificationsList'), entry.Episode); row.dataset.unread = String(!entry.ReadUtc);
                label('p', 'received', function () { return { kind: enumText(entry.Kind, kinds, 'notificationKind'), date: stamp(entry.CreatedUtc) }; }, row);
                if (entry.PreviousAnnouncedReleaseUtc) label('p', 'previouslyAnnounced', function () { return { date: stamp(entry.PreviousAnnouncedReleaseUtc) }; }, row).className = 'help';
                if (!entry.ReadUtc) {
                    var mark = action(row, 'markRead', async function () {
                        mark.disabled = true;
                        try {
                            await request('Siphon/Notifications/' + encodeURIComponent(entry.Id) + '/Read', 'POST', undefined, controller.signal);
                            if (expected !== generation || !token || controller.signal.aborted || !row.isConnected) return;
                            var heldFocus = document.activeElement === mark; row.dataset.unread = 'false'; mark.remove(); label('p', 'read', null, row).className = 'help'; result.UnreadCount = Math.max(0, result.UnreadCount - 1); message('notificationsStatus', 'unread', { count: result.UnreadCount });
                            if (heldFocus) get('notificationsLoad').focus({ preventScroll: true });
                        } catch (error) { if (expected === generation && token && !controller.signal.aborted && row.isConnected) { mark.disabled = false; failure('notificationsStatus', error, 'markReadFailed'); } }
                    });
                } else label('p', 'readAt', function () { return { date: stamp(entry.ReadUtc) }; }, row).className = 'help';
            }); loaded.add('notificationsPanel');
            paint(get('notificationsStatus'), function () { return (result.Items.length ? t('inboxCount', { unread: result.UnreadCount, count: result.Items.length }) : t('inboxEmpty')) + ' ' + t(!result.NotificationsAllowed ? 'inboxDisabled' : !result.NotificationsEnabled ? 'inboxOptIn' : 'noBacklog'); });
        } catch (error) { if (isCurrent('notifications', controller, expected)) failure('notificationsStatus', error, 'inboxFailed'); }
        finally { if (requests.get('notifications') === controller) { get('notificationsLoad').disabled = false; get('notificationsList').setAttribute('aria-busy', 'false'); } finish('notifications', controller); }
    }
    function webhookState() {
        get('webhookFields').disabled = !webhookSettings || webhookBusy;
        get('webhookReload').disabled = webhookBusy;
        get('webhookSave').disabled = !webhookSettings || !webhookDirty || webhookBusy;
        get('webhookUrl').required = get('webhookEnabled').checked;
        get('webhookTest').disabled = !webhookSettings || !webhookSettings.Allowed || !webhookSettings.Enabled || !webhookSettings.HasSecret || webhookDirty || webhookBusy;
        get('webhookRotate').disabled = !webhookSettings || !webhookSettings.Url || webhookDirty || webhookBusy;
        paint(get('webhookSetup'), function () { return t('setup' + get('webhookAdapter').value) + (get('webhookAdapter').value === 'Json' ? ' ' + t('jsonSecretRequired') + ' ' + t('jsonDigestShape') : ''); });
    }
    function showWebhook(value) {
        webhookSettings = value; webhookDirty = false;
        get('webhookEnabled').checked = value.Enabled; get('webhookUrl').value = value.Url || ''; get('webhookAdapter').value = value.Adapter || 'Json'; get('webhookDigest').value = value.DigestMinutes || 0;
        kinds.forEach(function (kind) { get('kind' + kind).checked = (value.Kinds || kinds).indexOf(kind) >= 0; });
        paint(get('webhookDelivery'), function () { return t(value.Allowed ? 'externalAllowed' : 'externalDenied') + ' ' + t('deliverySummary', { pending: value.PendingCount || 0, abandoned: value.AbandonedCount || 0, success: stamp(value.LastSuccessUtc), attempt: stamp(value.LastAttemptUtc), count: value.LastAttemptCount || 0, next: stamp(value.NextAttemptUtc), outcome: enumText(value.LastOutcome, ['Delivered', 'Retrying', 'Abandoned', 'Suppressed'], 'notRecorded') }) + (value.LastError ? ' · ' + t('webhookError', { reason: controlledText(value.LastError, 'webhookFailed') }) : ''); }); webhookState();
    }
    async function loadWebhook() {
        if (!token || webhookBusy) return;
        var controller = begin('webhook'), expected = generation;
        webhookBusy = true; webhookState(); clearWebhookSecret(); message('webhookStatus', 'loadingWebhook');
        try { var value = await request('Siphon/Notifications/Webhook', 'GET', undefined, controller.signal); if (!isCurrent('webhook', controller, expected)) return; showWebhook(value); message('webhookStatus', ''); }
        catch (error) { if (isCurrent('webhook', controller, expected)) failure('webhookStatus', error, 'webhookLoadFailed'); }
        finally { if (expected === generation) { webhookBusy = false; webhookState(); } finish('webhook', controller); }
    }
    async function webhookAction(kind, data) {
        if (!token || webhookBusy || !webhookSettings) return;
        var controller = begin('webhook'), expected = generation, focused = document.activeElement;
        webhookBusy = true; webhookState(); clearWebhookSecret(); message('webhookStatus', kind === 'Test' ? 'sendingTest' : 'savingWebhook');
        try {
            var result = await request('Siphon/Notifications/Webhook' + (kind ? '/' + kind : ''), kind ? 'POST' : 'PUT', data, controller.signal, kind === 'Test');
            if (!isCurrent('webhook', controller, expected)) return;
            if (kind === 'Secret' && (!result || !result.Secret)) throw new Error(t('webhookFailed'));
            var saved = await request('Siphon/Notifications/Webhook', 'GET', undefined, controller.signal); if (!isCurrent('webhook', controller, expected)) return; showWebhook(saved);
            if (kind === 'Secret' && result && result.Secret) { get('webhookSecret').value = result.Secret; get('webhookSecretPanel').hidden = false; }
            message('webhookStatus', kind === 'Secret' ? 'secretRotated' : kind === 'Test' ? (result && result.Success === true ? 'testAccepted' : 'testFailed') : 'webhookSaved', null, kind === 'Test' && (!result || result.Success !== true));
        } catch (error) { if (isCurrent('webhook', controller, expected)) failure('webhookStatus', error, 'webhookFailed'); }
        finally { if (expected === generation) { webhookBusy = false; webhookState(); if (kind === 'Secret' && !get('webhookSecretPanel').hidden) get('webhookCopy').focus(); else if (focused && focused.isConnected && !focused.disabled) focused.focus({ preventScroll: true }); else get('webhookReload').focus({ preventScroll: true }); } finish('webhook', controller); }
    }
    function scheduleDownloads() {
        clearTimeout(downloadsTimer); downloadsTimer = null;
        if (token && !get('downloadsPanel').hidden && get('downloadsAuto').checked && !document.hidden) downloadsTimer = setTimeout(function () { if (requests.has('downloadAction')) scheduleDownloads(); else loadDownloads(true); }, 4000);
    }
    function quotaText(value) { return t('quota', { used: size(value.UsedBytes), reserved: size(value.ReservedBytes), personal: size(value.UserMaximumBytes), global: size(value.MaximumBytes) }) + ' ' + t('reservationOverlap'); }
    function windowText(value) { return value.WindowEnabled ? t('window', { start: String(value.WindowStartUtcHour).padStart(2, '0'), end: String(value.WindowEndUtcHour).padStart(2, '0') }) : t('windowDisabled'); }
    function priorityInput(parent, id) {
        var wrapper = node('div', undefined, parent); wrapper.className = 'inputContainer';
        var title = label('label', 'priority', null, wrapper); title.className = 'inputLabel'; title.htmlFor = id;
        var input = node('input', undefined, wrapper); input.id = id; input.type = 'number'; input.min = '-10'; input.max = '10'; input.step = '1'; input.value = '0'; input.required = true; input.className = 'compact'; return input;
    }
    function renderDownloads(result) {
        var focused = document.activeElement && document.activeElement.id;
        get('downloadsList').replaceChildren();
        result.Jobs.forEach(function (job) {
            var row = node('li', undefined, get('downloadsList')); node('h3', job.Name, row);
            paint(node('p', undefined, row), function () { return enumText(job.State, ['Queued', 'Running', 'Paused', 'Completed', 'Failed', 'Cancelled', 'Expired'], 'unknownState') + ' · ' + enumText(job.Phase, ['Waiting', 'WaitingWindow', 'Receiving', 'PreparingAudio', 'Preparing', 'Assembling', 'Ready'], 'unknownPhase'); }).className = 'download-state';
            label('p', 'downloadBytes', function () { return { received: size(job.BytesReceived), total: job.TotalBytes == null ? '' : t('ofSize', { size: size(job.TotalBytes) }), date: stamp(job.UpdatedUtc) }; }, row).className = 'help';
            if (job.State === 'Running') {
                var progress = node('progress', undefined, row); progress.dataset.i18nLabel = 'user.transferProgress'; progress.setAttribute('aria-label', t('transferProgress'));
                if (job.Phase === 'Receiving' && job.TotalBytes > 0) { progress.max = job.TotalBytes; progress.value = Math.min(job.TotalBytes, Math.max(0, job.BytesReceived)); }
                paint(node('p', undefined, row), function () {
                    if (job.Phase !== 'Receiving') return t('etaPreparing');
                    var rate = Number.isFinite(job.BytesPerSecond) && job.BytesPerSecond > 0 ? t('downloadRate', { rate: size(job.BytesPerSecond) }) : t('rateUnknown');
                    return rate + ' · ' + (Number.isFinite(job.EstimatedSecondsRemaining) && job.EstimatedSecondsRemaining >= 0 ? t('eta', { time: t('minutes', { count: Math.max(1, Math.ceil(job.EstimatedSecondsRemaining / 60)).toLocaleString(ui.locale()) }) }) : t('etaUnknown'));
                }).className = 'help';
            }
            if (job.NextEligibleUtc) label('p', 'nextEligible', function () { return { date: stamp(job.NextEligibleUtc) }; }, row).className = 'help';
            label('p', 'keptTracks', function () { return { audio: job.AudioLanguages == null ? t('allTracks') : job.AudioLanguages.length ? job.AudioLanguages.join(', ') : t('noTracks'), subtitles: job.SubtitleLanguages == null ? t('allTracks') : job.SubtitleLanguages.length ? job.SubtitleLanguages.join(', ') : t('noTracks') }; }, row).className = 'help';
            if (job.Error) paint(node('p', undefined, row), function () { return controlledText(job.Error, 'operationFailed'); }).className = 'message';
            var controls = node('div', undefined, row); controls.className = 'actions';
            function jobAction(key, kind, allowed) { var button = action(controls, key, function () { downloadAction(job, kind); }); button.id = 'download-' + job.Id + '-' + kind; button.disabled = !allowed || requests.has('downloadAction'); return button; }
            if (job.State === 'Completed') jobAction('saveFile', 'Ticket', result.Allowed);
            if (job.State === 'Queued' || job.State === 'Running') jobAction('pause', 'Pause', result.Enabled && result.Allowed);
            if (job.State === 'Paused') jobAction('resume', 'Resume', result.Enabled && result.Allowed);
            if (['Queued', 'Running', 'Paused'].indexOf(job.State) >= 0) jobAction('cancel', 'Cancel', result.Allowed);
            if (job.CanRetry) jobAction('retry', 'Retry', result.Enabled && result.Allowed);
            jobAction('delete', 'Delete', result.Allowed);
            if (['Queued', 'Running', 'Paused'].indexOf(job.State) >= 0) {
                var form = node('form', undefined, row), input = priorityInput(form, 'priority-' + job.Id); input.value = job.Priority || 0;
                var apply = label('button', 'applyPriority', null, form); apply.type = 'submit'; apply.id = 'download-' + job.Id + '-Priority';
                input.disabled = apply.disabled = !result.Enabled || !result.Allowed || requests.has('downloadAction');
                form.addEventListener('submit', function (event) { event.preventDefault(); downloadAction(job, 'Priority', Number(input.value)); });
            }
        });
        if (focused && get(focused) && !get(focused).disabled) get(focused).focus({ preventScroll: true });
    }
    async function loadDownloads(quiet) {
        if (!token || requests.has('downloadAction')) return;
        var controller = begin('downloads'), expected = generation;
        get('downloadsLoad').disabled = true; get('downloadsList').setAttribute('aria-busy', 'true');
        if (!quiet) message('downloadsStatus', 'loadingDownloads');
        try {
            var result = await request('Siphon/Downloads', 'GET', undefined, controller.signal); if (!isCurrent('downloads', controller, expected)) return;
            downloadsSnapshot = result;
            // Do not replace the focused priority draft or keyboard action during automatic polling.
            if (!quiet || !get('downloadsList').contains(document.activeElement)) renderDownloads(result);
            message('downloadsStatus', !result.Enabled ? 'downloadsDisabled' : !result.Allowed ? 'downloadsDenied' : result.Jobs.length ? 'jobsCount' : 'jobsEmpty', { count: result.Jobs.length });
            paint(get('downloadsQuota'), function () { return quotaText(result); }); paint(get('downloadsWindow'), function () { return windowText(result); });
            get('batchPreview').disabled = !result.Enabled || !result.Allowed || requests.has('batch');
            if (batch) updateBatchSelection();
        } catch (error) { if (isCurrent('downloads', controller, expected)) failure('downloadsStatus', error, 'downloadsFailed'); }
        finally { if (requests.get('downloads') === controller) { get('downloadsLoad').disabled = false; get('downloadsList').setAttribute('aria-busy', 'false'); } finish('downloads', controller); if (expected === generation) scheduleDownloads(); }
    }
    function openDownload(ticket) {
        var url = new URL(ticket.Url, window.location.href);
        if (url.origin !== window.location.origin || !url.pathname.startsWith(root + '/Siphon/')) throw new Error(t('unexpectedDestination'));
        var link = node('a', t('downloadLink'), document.body); link.href = url.href; link.download = ''; link.referrerPolicy = 'no-referrer'; link.rel = 'noreferrer'; link.click(); link.remove();
    }
    async function downloadAction(job, kind, priority) {
        if (!token || requests.has('downloadAction')) return;
        if (kind === 'Priority' && (!Number.isInteger(priority) || priority < -10 || priority > 10)) { message('downloadsStatus', 'priorityInvalid', null, true); return; }
        if ((kind === 'Delete' || kind === 'Cancel') && !window.confirm(t(kind === 'Delete' ? 'deleteConfirm' : 'cancelConfirm'))) return;
        cancel('downloads'); clearTimeout(downloadsTimer);
        var controller = begin('downloadAction'), expected = generation, focused = document.activeElement && document.activeElement.id;
        get('downloadsList').querySelectorAll('button, input').forEach(function (control) { control.disabled = true; }); message('downloadsStatus', 'working');
        try {
            var result = await request('Siphon/Downloads/' + encodeURIComponent(job.Id) + (kind === 'Delete' ? '' : '/' + kind), kind === 'Delete' ? 'DELETE' : kind === 'Priority' ? 'PUT' : 'POST', kind === 'Priority' ? { Priority: priority } : undefined, controller.signal);
            if (!isCurrent('downloadAction', controller, expected)) return;
            if (kind === 'Ticket') openDownload(result);
            finish('downloadAction', controller); await loadDownloads();
            if (expected === generation && token) message('downloadsStatus', 'operationDone');
        } catch (error) { if (isCurrent('downloadAction', controller, expected)) failure('downloadsStatus', error, 'operationFailed'); }
        finally {
            finish('downloadAction', controller);
            if (expected === generation && token) {
                if (downloadsSnapshot) renderDownloads(downloadsSnapshot);
                var target = focused && get(focused); (target && !target.disabled ? target : get('downloadsLoad')).focus({ preventScroll: true }); scheduleDownloads();
            }
        }
    }
    function parseItemId(value) { var text = value.trim(); if (/^[0-9a-f]{32}$/i.test(text) || /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(text)) return text; var match = /[?&]id=([0-9a-f-]{32,36})(?:[&#]|$)/i.exec(text); return match && /^[0-9a-f]{32}$/i.test(match[1].replace(/-/g, '')) ? match[1] : null; }
    async function searchItems(offset) {
        if (!token || !currentUser) return;
        var query = get('itemSearch').value.trim(); if (!query) { get('itemSearch').focus(); return; }
        if (query !== itemQuery) offset = 0; itemQuery = query; itemOffset = offset;
        var controller = begin('items'), expected = generation;
        get('itemSearchSubmit').disabled = true; get('itemSearchCancel').hidden = false; get('itemPrevious').hidden = true; get('itemNext').hidden = true; get('itemSearchList').replaceChildren(); get('itemSearchList').setAttribute('aria-busy', 'true'); message('itemSearchStatus', 'searching');
        try {
            var result = await request('Items?userId=' + encodeURIComponent(currentUser.Id) + '&searchTerm=' + encodeURIComponent(query) + '&recursive=true&includeItemTypes=Movie,Episode,Series,Season&startIndex=' + offset + '&limit=25&enableTotalRecordCount=true', 'GET', undefined, controller.signal);
            if (!isCurrent('items', controller, expected)) return;
            result.Items.forEach(function (item) {
                var row = node('li', undefined, get('itemSearchList')); node('strong', (item.SeriesName ? item.SeriesName + ' · ' : '') + item.Name, row);
                paint(node('p', undefined, row), function () { return enumText(item.Type, ['Movie', 'Episode', 'Series', 'Season'], 'item') + (item.ProductionYear ? ' · ' + item.ProductionYear : ''); }).className = 'help';
                if (item.Type === 'Series' || item.Type === 'Season') action(row, 'previewBatch', function () { selectPanel('downloadsPanel', true); get('batchItem').value = item.Id; loadBatchPreview(); });
                else action(row, 'chooseItem', function () { get('sourceItem').value = item.Id; loadSources(false); get('sourcesStatus').scrollIntoView({ block: 'nearest' }); });
            });
            message('itemSearchStatus', result.Items.length ? 'searchCount' : 'searchEmpty', { first: offset + 1, last: offset + result.Items.length, total: result.TotalRecordCount });
            get('itemPrevious').hidden = offset === 0; get('itemNext').hidden = offset + result.Items.length >= result.TotalRecordCount;
        } catch (error) { if (isCurrent('items', controller, expected)) failure('itemSearchStatus', error, 'searchFailed'); }
        finally { if (requests.get('items') === controller) { get('itemSearchSubmit').disabled = false; get('itemSearchCancel').hidden = true; get('itemSearchList').setAttribute('aria-busy', 'false'); } finish('items', controller); }
    }
    async function loadSources(refresh) {
        if (!token) return;
        var itemId = parseItemId(get('sourceItem').value); if (!itemId) { message('sourcesStatus', 'invalidItem', null, true); get('sourceItem').focus(); return; }
        cancelSourceActions(); var controller = begin('sources'), expected = generation;
        get('sourcesLoad').disabled = true; get('sourcesRefresh').disabled = true; get('sourcesCancel').hidden = false; get('sourceResults').replaceChildren(); get('sourceResults').setAttribute('aria-busy', 'true'); message('sourcesStatus', refresh ? 'refreshingSources' : 'loadingSources');
        try {
            var result = await request('Siphon/Items/' + encodeURIComponent(itemId) + '/Sources' + (refresh ? '/Refresh' : ''), refresh ? 'POST' : 'GET', undefined, controller.signal); if (!isCurrent('sources', controller, expected)) return;
            renderSources(result, expected); message('sourcesStatus', result.Sources.length ? 'sourceCount' : 'sourcesEmpty', { count: result.Sources.length });
        } catch (error) { if (isCurrent('sources', controller, expected)) failure('sourcesStatus', error, 'sourcesFailed'); }
        finally { if (requests.get('sources') === controller) { get('sourcesLoad').disabled = false; get('sourcesRefresh').disabled = false; get('sourcesCancel').hidden = true; get('sourceResults').setAttribute('aria-busy', 'false'); } finish('sources', controller); }
    }
    function renderSources(result, expected) {
        var parent = get('sourceResults'), facts = node('dl', undefined, parent); facts.className = 'facts';
        fact(facts, 'resolved', function () { return stamp(result.CreatedAt); }); fact(facts, 'cacheExpiry', function () { return stamp(result.ExpiresAt); });
        fact(facts, 'cacheAge', function () { return Number.isFinite(Date.parse(result.CreatedAt)) ? t('seconds', { count: Math.max(0, Math.floor((Date.now() - Date.parse(result.CreatedAt)) / 1000)) }) : t('notRecorded'); });
        label('h3', 'versions', null, parent); var sources = node('ol', undefined, parent); sources.className = 'list';
        result.Sources.forEach(function (source, index) {
            var row = node('li', undefined, sources);
            paint(node('strong', undefined, row), function () { return (index + 1) + '. ' + (source.Name || t('streamVersion')); });
            paint(node('p', undefined, row), function () { return t(source.Kind === 'P2p' ? 'p2pHelp' : 'httpSource') + ' · ' + size(source.Size); }).className = 'help';
            var status = node('p', '', row); status.className = 'message'; status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
            var preparation = node('div', undefined, row);
            var prepare = action(row, 'prepare', function () { prepareSource(result.ItemId, source, preparation, status, prepare, expected); }); prepare.disabled = !source.CanQueue;
            if (!source.CanQueue) label('p', 'queueUnavailable', null, row).className = 'help';
            if (result.CanDownload && source.CanDownload) {
                var download = action(row, 'downloadVersion', async function () {
                    if (expected !== generation || !token || !row.isConnected) return;
                    var key = 'ticket-' + source.MediaSourceId, controller = begin(key); download.disabled = true; message(status, 'preparingLink');
                    try { var ticket = await request('Siphon/Items/' + encodeURIComponent(result.ItemId) + '/DownloadTicket', 'POST', { MediaSourceId: source.MediaSourceId }, controller.signal); if (!isCurrent(key, controller, expected) || !row.isConnected) return; openDownload(ticket); message(status, 'downloadRequested', function () { return { date: stamp(ticket.ExpiresAtUtc) }; }); }
                    catch (error) { if (isCurrent(key, controller, expected) && row.isConnected) failure(status, error, 'downloadFailed'); }
                    finally { finish(key, controller); if (expected === generation && row.isConnected) download.disabled = false; }
                });
            } else if (source.DownloadUnavailableReason) label('p', 'directUnavailable', null, row).className = 'help';
        });
        if (!result.CanDownload) label('p', 'downloadPermission', null, parent).className = 'help';
        label('h3', 'addonOutcomes', null, parent); var addons = node('ul', undefined, parent); addons.className = 'list';
        result.Addons.forEach(function (addon) {
            var row = node('li', undefined, addons); paint(node('strong', undefined, row), function () { return addon.Name || t('addon'); });
            label('p', 'addonResult', function () { return { outcome: enumText(addon.Outcome, ['Available', 'Empty', 'Unsupported', 'Unavailable'], 'outcomeUnknown'), duration: addon.ElapsedMilliseconds, count: addon.Accepted }; }, row);
            Object.keys(addon.Rejected || {}).forEach(function (reason) { label('p', 'rejection', function () { return { reason: controlledText(reason, 'rejectedSource'), count: addon.Rejected[reason] }; }, row).className = 'help'; });
        });
        if (!result.Addons.length) label('li', 'noAddons', null, addons); label('p', 'sourcePrivacy', null, parent).className = 'help';
    }
    function trackSelector(parent, key, languages) {
        var fieldset = node('fieldset', undefined, parent); fieldset.className = 'track-options'; label('legend', key, null, fieldset);
        var wrapper = node('label', undefined, fieldset); label('span', key, null, wrapper).className = 'inputLabel';
        var mode = node('select', undefined, wrapper); label('option', 'allTracks', null, mode).value = 'all'; label('option', 'customTracks', null, mode).value = 'custom';
        var options = node('div', undefined, fieldset); options.className = 'checkboxContainer'; options.hidden = true;
        var entries = languages.map(function (language) { var title = node('label', undefined, options), input = node('input', undefined, title); input.type = 'checkbox'; input.checked = false; node('span', language, title); return { language: language, input: input }; });
        if (!entries.length) label('p', 'noLanguages', null, options).className = 'help';
        mode.addEventListener('change', function () { options.hidden = mode.value === 'all'; });
        return function () { return mode.value === 'all' ? null : entries.filter(function (entry) { return entry.input.checked; }).map(function (entry) { return entry.language; }); };
    }
    async function prepareSource(itemId, source, parent, status, button, expected) {
        if (expected !== generation || !token || !parent.isConnected) return;
        var key = 'prepare-' + source.MediaSourceId, controller = begin(key); button.disabled = true; parent.replaceChildren(); parent.setAttribute('aria-busy', 'true'); message(status, 'preparingSource');
        try {
            var result = await request('Siphon/Downloads/Preparation', 'POST', { ItemId: itemId, MediaSourceId: source.MediaSourceId }, controller.signal);
            if (!isCurrent(key, controller, expected) || !parent.isConnected) return;
            var limits = await request('Siphon/Downloads', 'GET', undefined, controller.signal);
            if (!isCurrent(key, controller, expected) || !parent.isConnected) return;
            var form = node('form', undefined, parent); form.className = 'preparation';
            label('p', 'preparationEstimate', function () { return { kind: enumText(result.Kind, ['Hls', 'Progressive', 'P2p'], 'formatUnknown'), source: size(result.EstimatedSourceBytes), temporary: size(result.EstimatedTemporaryBytes) }; }, form).className = 'help';
            paint(node('p', undefined, form), function () { return quotaText(limits) + ' ' + windowText(limits); }).className = 'help';
            (result.Warnings || []).forEach(function (warning) { paint(node('p', undefined, form), function () { return controlledText(warning, 'preparationWarning'); }).className = 'help'; });
            var audio = function () { return null; }, subtitles = function () { return null; };
            if (result.Kind === 'Hls') { label('p', 'tracksHelp', null, form).className = 'help'; audio = trackSelector(form, 'audio', result.AudioLanguages || []); subtitles = trackSelector(form, 'subtitles', result.SubtitleLanguages || []); }
            else label('p', 'tracksUnsupported', null, form).className = 'help';
            var priority = priorityInput(form, 'prepare-priority-' + source.MediaSourceId);
            var queue = label('button', 'queue', null, form); queue.type = 'submit'; queue.disabled = !limits.Enabled || !limits.Allowed;
            if (queue.disabled) message(status, !limits.Enabled ? 'downloadsDisabled' : 'downloadsDenied'); else message(status, '');
            form.addEventListener('submit', async function (event) {
                event.preventDefault(); if (queue.disabled || expected !== generation || !token || !form.isConnected) return;
                var value = Number(priority.value); if (!Number.isInteger(value) || value < -10 || value > 10) { message(status, 'priorityInvalid', null, true); return; }
                var queueKey = 'enqueue-' + source.MediaSourceId, enqueue = begin(queueKey);
                var draft = { ItemId: itemId, MediaSourceId: source.MediaSourceId, AudioLanguages: audio(), SubtitleLanguages: subtitles(), Priority: value };
                form.querySelectorAll('input, select, button').forEach(function (control) { control.disabled = true; }); button.disabled = true; message(status, 'queueing');
                try {
                    await request('Siphon/Downloads', 'POST', draft, enqueue.signal); if (!isCurrent(queueKey, enqueue, expected) || !form.isConnected) return;
                    form.replaceChildren(); message(status, 'queued'); var view = action(form, 'viewDownloads', function () { selectPanel('downloadsPanel', true); }); view.focus({ preventScroll: true });
                } catch (error) { if (isCurrent(queueKey, enqueue, expected) && form.isConnected) { failure(status, error, 'queueFailed'); form.querySelectorAll('input, select, button').forEach(function (control) { control.disabled = false; }); queue.focus({ preventScroll: true }); } }
                finally { finish(queueKey, enqueue); if (expected === generation && parent.isConnected) button.disabled = false; }
            });
            (queue.disabled ? priority : queue).focus({ preventScroll: true });
        } catch (error) { if (isCurrent(key, controller, expected) && parent.isConnected) failure(status, error, 'prepareFailed'); }
        finally { if (expected === generation && parent.isConnected) { button.disabled = false; parent.setAttribute('aria-busy', 'false'); } finish(key, controller); }
    }
    function updateBatchSelection() {
        if (!batch || !batch.form.isConnected) return;
        var selected = batch.rows.filter(function (row) { return row.include.checked; }), bytes = 0, unknown = 0, complete = true;
        selected.forEach(function (row) { var source = row.item.Sources.find(function (value) { return value.MediaSourceId === row.version.value; }); if (!source) complete = false; else if (Number.isFinite(source.Size) && source.Size >= 0) bytes += source.Size; else unknown++; });
        labelBatchSummary(selected.length, bytes, unknown);
        var expired = Date.parse(batch.preview.ExpiresAtUtc) <= Date.now();
        batch.submit.disabled = batch.submitted || requests.has('batch') || expired || !selected.length || !complete || selected.length > batch.preview.MaximumJobs || Boolean(downloadsSnapshot && (!downloadsSnapshot.Enabled || !downloadsSnapshot.Allowed));
        if (expired && !batch.submitted) message('batchStatus', 'batchExpired', null, true);
    }
    function labelBatchSummary(count, bytes, unknown) { if (batch) message(batch.selection, 'batchSelected', function () { return { count: count, size: size(bytes), unknown: unknown }; }); }
    async function loadBatchPreview() {
        if (!token || requests.has('batch')) return;
        var itemId = parseItemId(get('batchItem').value); if (!itemId) { message('batchStatus', 'invalidItem', null, true); get('batchItem').focus(); return; }
        if (batch && !batch.submitted && batch.rows.some(function (row) { return row.include.checked; }) && !window.confirm(t('reloadingDiscard'))) return;
        clearBatch(); var controller = begin('batch'), expected = generation;
        get('batchPreview').disabled = true; get('batchCancel').hidden = false; get('batchResults').setAttribute('aria-busy', 'true'); message('batchStatus', 'batchLoading');
        try {
            var result = await request('Siphon/Downloads/BatchPreview', 'POST', { ItemId: itemId }, controller.signal); if (!isCurrent('batch', controller, expected)) return;
            renderBatch(result, expected); message('batchStatus', result.Items.length ? 'batchSummary' : 'batchEmpty', function () { return { count: result.Items.length, maximum: result.MaximumJobs, date: stamp(result.ExpiresAtUtc) }; });
        } catch (error) { if (isCurrent('batch', controller, expected)) failure('batchStatus', error, 'batchFailed'); }
        finally {
            if (isCurrent('batch', controller, expected)) { get('batchPreview').disabled = Boolean(downloadsSnapshot && (!downloadsSnapshot.Enabled || !downloadsSnapshot.Allowed)); get('batchCancel').hidden = true; get('batchResults').setAttribute('aria-busy', 'false'); }
            finish('batch', controller); if (expected === generation) updateBatchSelection();
        }
    }
    function renderBatch(preview, expected) {
        var form = node('form', undefined, get('batchResults')); form.id = 'batchSelectionForm';
        if (preview.Truncated) label('p', 'batchTruncated', null, form).className = 'message';
        label('p', 'batchTracks', null, form).className = 'help';
        var list = node('ul', undefined, form); list.className = 'list';
        var rows = preview.Items.map(function (item, index) {
            var row = node('li', undefined, list); nativeLink(node('h3', undefined, row), episodeTitle(item), item.ItemId);
            var title = node('label', undefined, row); title.className = 'inline-label';
            var include = node('input', undefined, title); include.type = 'checkbox'; include.checked = false; include.disabled = !item.Sources.length; include.id = 'batch-include-' + index;
            label('span', 'batchSelect', { name: item.Name }, title);
            var wrapper = node('div', undefined, row); wrapper.className = 'batch-source';
            var versionTitle = label('label', 'exactVersion', null, wrapper); versionTitle.htmlFor = 'batch-version-' + index; versionTitle.className = 'inputLabel';
            var version = node('select', undefined, wrapper); version.id = versionTitle.htmlFor; version.disabled = true;
            label('option', 'chooseVersion', null, version).value = '';
            item.Sources.forEach(function (source) {
                var option = paint(node('option', undefined, version), function () { return (source.Name || t('streamVersion')) + ' · ' + enumText(source.Kind, ['Hls', 'Progressive', 'Http', 'P2p'], 'formatUnknown') + ' · ' + size(source.Size); });
                option.value = source.MediaSourceId;
            });
            if (!item.Sources.length) label('p', 'noVersions', null, row).className = 'help';
            include.addEventListener('change', function () { version.disabled = !include.checked; updateBatchSelection(); });
            version.addEventListener('change', updateBatchSelection);
            return { item: item, include: include, version: version };
        });
        var selection = node('p', '', form); selection.className = 'help'; selection.setAttribute('role', 'status');
        var priority = priorityInput(form, 'batchPriority'); var submit = label('button', 'queueBatch', null, form); submit.id = 'batchSubmit'; submit.type = 'submit'; submit.disabled = true;
        batch = { preview: preview, form: form, rows: rows, selection: selection, priority: priority, submit: submit, submitted: false };
        form.addEventListener('submit', function (event) { event.preventDefault(); submitBatch(expected); });
        batchExpiryTimer = setTimeout(function () { if (expected === generation && batch && batch.preview === preview) updateBatchSelection(); }, Math.max(0, Math.min(300001, Date.parse(preview.ExpiresAtUtc) - Date.now() + 20)));
        var first = rows.find(function (row) { return !row.include.disabled; }); if (first) first.include.focus({ preventScroll: true });
        updateBatchSelection();
    }
    async function submitBatch(expected) {
        if (!token || expected !== generation || !batch || batch.submitted || requests.has('batch')) return;
        var selected = batch.rows.filter(function (row) { return row.include.checked; }), priority = Number(batch.priority.value);
        if (Date.parse(batch.preview.ExpiresAtUtc) <= Date.now()) { message('batchStatus', 'batchExpired', null, true); updateBatchSelection(); return; }
        if (!selected.length || selected.length > batch.preview.MaximumJobs || selected.some(function (row) { return !row.item.Sources.some(function (source) { return source.MediaSourceId === row.version.value; }); })) { message('batchStatus', 'batchInvalid', null, true); return; }
        if (!Number.isInteger(priority) || priority < -10 || priority > 10) { message('batchStatus', 'priorityInvalid', null, true); return; }
        var currentBatch = batch, controller = begin('batch');
        var draft = { Token: currentBatch.preview.Token, Items: selected.map(function (row) { return { ItemId: row.item.ItemId, MediaSourceId: row.version.value, AudioLanguages: null, SubtitleLanguages: null, Priority: priority }; }) };
        currentBatch.submitted = true; clearTimeout(batchExpiryTimer);
        currentBatch.form.querySelectorAll('input, select, button').forEach(function (control) { control.disabled = true; }); get('batchPreview').disabled = true; get('batchCancel').hidden = false; message('batchStatus', 'batchSubmitting');
        try {
            var result = await request('Siphon/Downloads/Batch', 'POST', draft, controller.signal); if (!isCurrent('batch', controller, expected) || batch !== currentBatch) return;
            get('batchOutcomes').replaceChildren();
            (result.Jobs || []).forEach(function (job) { label('li', 'batchAccepted', { name: job.Name }, get('batchOutcomes')); });
            (result.Rejected || []).forEach(function (entry) {
                var item = currentBatch.preview.Items.find(function (value) { return value.ItemId === entry.ItemId; });
                label('li', 'batchRejected', function () { return { name: item ? item.Name : t('item'), reason: entry.Code ? ui.errorText({ code: entry.Code }, t('batchRejectedReason')) : controlledText(entry.Message, 'batchRejectedReason') }; }, get('batchOutcomes'));
            });
            message('batchStatus', 'batchOutcome', { accepted: (result.Jobs || []).length, rejected: (result.Rejected || []).length }, Boolean((result.Rejected || []).length));
            await loadDownloads(true);
        } catch (error) { if (isCurrent('batch', controller, expected) && batch === currentBatch) failure('batchStatus', error, 'batchUnconfirmed'); }
        finally {
            if (isCurrent('batch', controller, expected)) { get('batchPreview').disabled = Boolean(downloadsSnapshot && (!downloadsSnapshot.Enabled || !downloadsSnapshot.Allowed)); get('batchCancel').hidden = true; get('batchPreview').focus({ preventScroll: true }); }
            finish('batch', controller);
        }
    }
    function controlledText(value, fallback) {
        var key = Object.prototype.hasOwnProperty.call(controlledMessages, value) ? controlledMessages[value] : null; return key ? t(key) : t(fallback);
    }
    var controlledMessages = {
        InvalidP2p: 'invalidP2p', P2pDisabled: 'p2pDisabled', UnsupportedUrl: 'unsupportedUrl', UnsupportedHeaders: 'unsupportedHeaders',
        'The download storage limit was reduced.': 'reducedLimits',
        'The download queue limits were reduced.': 'reducedLimits',
        'The per-user download storage limit was reduced.': 'reducedLimits',
        'This version exceeds the configured download storage limit.': 'versionOverLimit',
        'The private download storage is unavailable.': 'storageUnavailable',
        'The HLS source uses an unsupported format. Choose a finite, non-DRM version with compatible video and tracks.': 'unsupportedHls',
        'The download failed. The selected source may be unavailable, unsupported, or over its storage limit.': 'transferFailed',
        'Webhook delivery expired, exhausted its retry budget or lost notification access.': 'webhookAbandoned',
        'Webhook pending delivery capacity reached; excess events were not queued.': 'webhookCapacity',
        'Webhook test delivery failed.': 'testFailed',
        'Pending webhook deliveries were abandoned after settings or permissions changed.': 'webhookGeneration',
        'Pending webhook deliveries were abandoned during the delivery format upgrade.': 'webhookUpgrade',
        'Finite HLS is staged again after pause, restart or failure; incomplete Matroska output is never appended.': 'hlsRestart',
        'Offline HLS requires video, compatible copy-mode codecs and configured FFmpeg/FFprobe. Audio-only HLS, live streams and DRM are not supported; choose a progressive source instead.': 'hlsRequirements',
        'Peer exports retain the complete selected file. Track filtering is not supported.': 'peerTracks',
        'Progressive exports retain the complete selected file. Track filtering is not supported. Resume requires an unchanged strong ETag and exact byte ranges.': 'progressiveTracks',
        'Language choices apply to advertised renditions. Null retains all; an empty list removes separate tracks. Embedded closed captions cannot be removed without conversion.': 'renditionTracks',
        'Temporary storage includes downloaded resources, audio preparation and the final container; estimates are not a storage guarantee.': 'temporaryWarning',
        'Some renditions have no advertised language; retain all to include unnamed tracks.': 'unnamedTracks',
        'Embedded audio cannot be isolated by advertised rendition; use all audio or no audio.': 'embeddedAudio',
        'Embedded closed captions are retained in the video bitstream; subtitle filtering is unavailable for this source.': 'embeddedCaptions',
        'This media playlist does not advertise track languages. Retain all tracks or remove a track type; named selection requires a master playlist.': 'mediaTracks',
        'The completed download could not be safely stored.': 'storageFailed',
        'Download permission or the playback profile changed.': 'profileChanged'
    };
    function resetDates() { var today = new Date(); get('calendarFrom').value = today.toISOString().slice(0, 10); get('calendarTo').value = new Date(today.getTime() + 29 * 86400000).toISOString().slice(0, 10); }
    function validationMessage(field) {
        field.setCustomValidity('');
        var validity = field.validity;
        if (validity.valid) { delete field.dataset.siphonInvalid; return; }
        var key = validity.valueMissing ? 'requiredValue' : field.type === 'url' ? 'invalidUrl' : field.type === 'number' ? 'integerInterval' : 'invalidValue';
        field.dataset.siphonInvalid = 'true'; field.setCustomValidity(t(key, { minimum: field.min, maximum: field.max }));
    }
    function translatePage() {
        document.documentElement.lang = ui.locale(); document.title = t('title') + ' · Jellyfin'; get('portalLanguage').value = ui.locale();
        ui.translate(document); document.querySelector('[role="tablist"]').setAttribute('aria-label', t('tools'));
        document.querySelectorAll('[data-user-text]').forEach(function (element) { if (element.siphonText) element.textContent = element.siphonText(); });
        document.querySelectorAll('[data-siphon-invalid]').forEach(validationMessage);
    }
    get('brandIcon').src = apiUrl('Siphon/Icon'); get('jellyfinLink').href = apiUrl('web/index.html'); resetDates(); translatePage(); webhookState();
    get('portalLanguage').addEventListener('change', function () { ui.setLocale(get('portalLanguage').value); });
    document.addEventListener('invalid', function (event) { if (typeof event.target.setCustomValidity === 'function') validationMessage(event.target); }, true);
    document.addEventListener('input', function (event) { if (typeof event.target.setCustomValidity === 'function') { event.target.setCustomValidity(''); delete event.target.dataset.siphonInvalid; } }, true);
    document.addEventListener('siphonlanguagechange', translatePage);
    get('loginForm').addEventListener('submit', async function (event) {
        event.preventDefault(); if (get('loginFields').disabled) return;
        var controller = begin('login'), expected = generation;
        get('loginFields').disabled = true; message('sessionStatus', 'signingIn');
        try {
            var result = await request('Users/AuthenticateByName', 'POST', { Username: get('username').value.trim(), Pw: get('password').value }, controller.signal);
            if (expected !== generation || requests.get('login') !== controller || controller.signal.aborted) return;
            if (!result || !result.AccessToken || !result.User) throw new Error(t('loginFailed'));
            token = result.AccessToken; currentUser = result.User; serverId = result.ServerId; generation++;
            get('password').value = ''; get('signIn').hidden = true; get('authenticated').hidden = false; get('signOut').hidden = false;
            paint(get('account'), function () { return currentUser ? t('signedIn', { name: currentUser.Name }) : t('accountHelp'); }); message('sessionStatus', ''); selectPanel('preferencesPanel', true);
        } catch (error) {
            if (expected === generation && !controller.signal.aborted) { get('password').value = ''; message('sessionStatus', error.status === 401 || error.status === 403 ? 'loginRejected' : 'loginFailed', null, true); }
        } finally { if (requests.get('login') === controller) { get('loginFields').disabled = false; finish('login', controller); } }
    });
    get('signOut').addEventListener('click', async function () {
        if ((preferencesDirty || webhookDirty) && !window.confirm(t('signOutConfirm'))) return;
        var logout = fetch(apiUrl('Sessions/Logout'), { method: 'POST', credentials: 'omit', cache: 'no-store', referrerPolicy: 'no-referrer', headers: { Authorization: authorization() } });
        endSession('signedOut'); var expected = generation;
        try { var response = await logout; if (!response.ok && expected === generation && !token) message('sessionStatus', 'logoutUnconfirmed'); }
        catch (_) { if (expected === generation && !token) message('sessionStatus', 'logoutUnconfirmed'); }
    });
    tabs.forEach(function (tab, index) {
        tab.addEventListener('click', function () { selectPanel(tab.getAttribute('aria-controls')); });
        tab.addEventListener('keydown', function (event) { var target = event.key === 'ArrowRight' ? (index + 1) % tabs.length : event.key === 'ArrowLeft' ? (index + tabs.length - 1) % tabs.length : event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : -1; if (target >= 0) { event.preventDefault(); selectPanel(tabs[target].getAttribute('aria-controls'), true); } });
    });
    get('preferencesForm').addEventListener('input', function () { preferencesDirty = true; preferenceState(); });
    get('preferencesForm').addEventListener('submit', savePreferences);
    get('reloadPreferences').addEventListener('click', function () { if (!preferencesDirty || window.confirm(t('discardPreferences'))) loadPreferences(); });
    get('preferencesRetry').addEventListener('click', loadPreferences);
    get('calendarForm').addEventListener('submit', function (event) { event.preventDefault(); loadCalendar(); });
    get('calendarCancel').addEventListener('click', function () { cancel('calendar', 'calendarStatus'); get('calendarLoad').disabled = false; get('calendarCancel').hidden = true; get('calendarList').setAttribute('aria-busy', 'false'); get('calendarLoad').focus(); });
    get('notificationsLoad').addEventListener('click', function () { loadNotifications(); if (!webhookDirty) loadWebhook(); });
    get('webhookForm').addEventListener('input', function (event) { if (event.target === get('webhookSecret')) return; webhookDirty = true; webhookState(); message('webhookStatus', 'webhookDraft'); });
    get('webhookForm').addEventListener('submit', function (event) { event.preventDefault(); webhookAction('', { Enabled: get('webhookEnabled').checked, Url: get('webhookUrl').value.trim(), Adapter: get('webhookAdapter').value, Kinds: kinds.filter(function (kind) { return get('kind' + kind).checked; }), DigestMinutes: Number(get('webhookDigest').value) }); });
    get('webhookReload').addEventListener('click', function () { if (!webhookDirty || window.confirm(t('discardWebhook'))) loadWebhook(); });
    get('webhookTest').addEventListener('click', function () { webhookAction('Test'); });
    get('webhookRotate').addEventListener('click', function () { if (window.confirm(t('rotateConfirm'))) webhookAction('Secret'); });
    get('webhookHide').addEventListener('click', function () { clearWebhookSecret(); get('webhookRotate').focus(); });
    get('webhookCopy').addEventListener('click', async function () {
        var expected = generation, secret = get('webhookSecret').value; if (!token || !secret) return;
        var copied = await ui.copyText(secret); if (expected !== generation || !token || get('webhookSecret').value !== secret) return;
        if (copied) message('webhookStatus', 'secretCopied'); else { get('webhookSecret').focus(); get('webhookSecret').select(); message('webhookStatus', 'manualCopy'); }
    });
    get('downloadsLoad').addEventListener('click', function () { loadDownloads(); });
    get('downloadsAuto').addEventListener('change', scheduleDownloads);
    get('downloadsSources').addEventListener('click', function () { selectPanel('sourcesPanel', true); get('itemSearch').focus(); });
    document.addEventListener('visibilitychange', scheduleDownloads);
    get('batchForm').addEventListener('submit', function (event) { event.preventDefault(); loadBatchPreview(); });
    get('batchItem').addEventListener('input', function () { var submitted = batch && batch.submitted && requests.has('batch'); cancel('batch'); clearBatch(); get('batchPreview').disabled = Boolean(downloadsSnapshot && (!downloadsSnapshot.Enabled || !downloadsSnapshot.Allowed)); get('batchCancel').hidden = true; get('batchResults').setAttribute('aria-busy', 'false'); message('batchStatus', submitted ? 'batchUnconfirmed' : '', null, Boolean(submitted)); });
    get('batchCancel').addEventListener('click', function () { var submitted = batch && batch.submitted; cancel('batch', 'batchStatus', submitted ? 'batchUnconfirmed' : 'cancelled'); clearBatch(); get('batchPreview').disabled = false; get('batchCancel').hidden = true; get('batchResults').setAttribute('aria-busy', 'false'); get('batchPreview').focus(); });
    get('itemSearchForm').addEventListener('submit', function (event) { event.preventDefault(); searchItems(0); });
    get('itemSearch').addEventListener('input', function () { cancel('items', 'itemSearchStatus', 'searchPrompt'); get('itemSearchList').replaceChildren(); get('itemSearchList').setAttribute('aria-busy', 'false'); get('itemPrevious').hidden = true; get('itemNext').hidden = true; get('itemSearchSubmit').disabled = false; get('itemSearchCancel').hidden = true; });
    get('itemSearchCancel').addEventListener('click', function () { cancel('items', 'itemSearchStatus'); get('itemSearchSubmit').disabled = false; get('itemSearchCancel').hidden = true; get('itemSearchList').setAttribute('aria-busy', 'false'); get('itemSearchSubmit').focus(); });
    get('itemPrevious').addEventListener('click', function () { searchItems(Math.max(0, itemOffset - 25)); });
    get('itemNext').addEventListener('click', function () { searchItems(itemOffset + 25); });
    get('sourcesForm').addEventListener('submit', function (event) { event.preventDefault(); loadSources(false); });
    get('sourceItem').addEventListener('input', function () { cancel('sources', 'sourcesStatus', 'sourcesPrompt'); cancelSourceActions(); get('sourceResults').replaceChildren(); get('sourceResults').setAttribute('aria-busy', 'false'); get('sourcesLoad').disabled = false; get('sourcesRefresh').disabled = false; get('sourcesCancel').hidden = true; });
    get('sourcesRefresh').addEventListener('click', function () { loadSources(true); });
    get('sourcesCancel').addEventListener('click', function () { cancel('sources', 'sourcesStatus', 'viewCancelled'); get('sourcesLoad').disabled = false; get('sourcesRefresh').disabled = false; get('sourcesCancel').hidden = true; get('sourceResults').setAttribute('aria-busy', 'false'); get('sourcesLoad').focus(); });
    window.addEventListener('beforeunload', function (event) { if (preferencesDirty || webhookDirty) { event.preventDefault(); event.returnValue = ''; } });
    window.addEventListener('pagehide', function () { endSession('sessionCleared'); });
}());

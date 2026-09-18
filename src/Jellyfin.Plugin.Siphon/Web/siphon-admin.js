window.SiphonAdmin = function (page, initiallyVisible) {
        'use strict';
        if (page.dataset.siphonBound) return;
        page.dataset.siphonBound = 'true';
        var ui = window.SiphonUi;
        ui.init();
        function t(key, values) { return ui.t('admin.' + key, values); }

        var textBindings = new Map();
        function resolveText(value) { while (typeof value === 'function') value = value(); return value == null ? '' : String(value); }
        function bindText(element, value, attribute) {
            if (value === undefined) return;
            var bindings = textBindings.get(element);
            if (!bindings) { bindings = {}; textBindings.set(element, bindings); }
            bindings[attribute || 'textContent'] = value;
            if (attribute) element.setAttribute(attribute, resolveText(value));
            else { element.removeAttribute('data-i18n'); element.textContent = resolveText(value); }
        }
        var translationObserver = new MutationObserver(function () {
            textBindings.forEach(function (_, element) { if (!page.contains(element)) textBindings.delete(element); });
        });
        translationObserver.observe(page, { childList: true, subtree: true });
        function translatePage() {
            page.lang = ui.locale();
            get('siphonLocale').value = ui.locale();
            ui.translate(page);
            textBindings.forEach(function (bindings, element) {
                if (!page.contains(element)) { textBindings.delete(element); return; }
                Object.keys(bindings).forEach(function (attribute) {
                    if (attribute === 'textContent') element.textContent = resolveText(bindings[attribute]);
                    else element.setAttribute(attribute, resolveText(bindings[attribute]));
                });
            });
            if (config) { renderMetadataAddons(); metadataControlsState(); updateState(); }
            if (diagnostics) renderHealth();
        }

        var pluginId = 'b2df1c14-4b7e-4e7b-9a95-8f9ad8d2b0c1';
        var config = null;
        var savedConfiguration = '';
        var manifests = new Map();
        var manifestStates = new Map();
        var discoveries = new Map();
        var discoveredKeys = new Set();
        var openCatalogGroups = new Set();
        var busy = false;
        var dirty = false;
        var visible = false;
        var generation = 0;
        var nextId = 0;
        var taskDefinitions = [
            { key: 'SiphonCatalogSync', kind: 'Catalogs', get name() { return t('catalogSynchronization'); }, button: 'siphonSync', state: 'siphonCatalogState', last: 'siphonLastSync', schedule: 'siphonSchedule', link: 'siphonScheduleLink' },
            { key: 'SiphonFollowedSeriesSync', kind: 'FollowedSeries', get name() { return t('followedSeries'); }, button: 'siphonFollowed', state: 'siphonFollowedState', last: 'siphonLastFollowed', schedule: 'siphonFollowedSchedule', link: 'siphonFollowedScheduleLink' },
            { key: 'SiphonMetadataRefresh', kind: 'FullRefresh', get name() { return t('fullMetadataRefresh'); }, button: 'siphonFull', state: 'siphonFullState', last: 'siphonLastFull', schedule: 'siphonFullSchedule', link: 'siphonFullScheduleLink' },
            { key: 'SiphonTargetedSync', kind: 'Targeted', get name() { return t('targetedSynchronization'); }, button: 'siphonTarget', state: 'siphonTargetState', last: 'siphonLastTarget', schedule: 'siphonTargetSchedule', link: 'siphonTargetScheduleLink' }
        ];
        var nativeTasks = [];
        var tasksKnown = false;
        var currentRunKind = null;
        var statusPromise = null;
        var statusTimer = null;
        var statusLoading = false;
        var textFields = ['PublicBaseUrl', 'TmdbReadAccessToken', 'TvdbApiKey', 'FanartApiKey', 'MdbListApiKey', 'TvdbSubscriberPin', 'MetadataLanguage', 'MetadataUpdateMode', 'MetadataAddonId', 'DefaultSearchMode'];
        var numberFields = ['MaxItemsPerCatalog', 'MaxEpisodesPerSeries', 'AddonTimeoutSeconds', 'MaxConcurrentRequests', 'MissingItemRetentionDays', 'MetadataCacheHours', 'UnreleasedBufferDays', 'IntroDbCacheHours', 'P2pMaxConcurrentStreams', 'P2pMaxCacheMiB', 'P2pDownloadLimitKiB', 'P2pUploadLimitKiB', 'P2pIdleMinutes', 'P2pMetadataTimeoutSeconds', 'P2pListenPort', 'DownloadMaxConcurrentJobs', 'DownloadMaxStorageMiB', 'DownloadMaxFileMiB', 'DownloadMaxJobsPerUser', 'DownloadRetentionDays', 'DownloadMaxStoragePerUserMiB', 'DownloadWindowStartUtcHour', 'DownloadWindowEndUtcHour'];
        var booleanFields = ['ShowCatalogShortcuts', 'RemoveMissingItems', 'ProtectFavorites', 'ProtectResumePositions', 'EnableTmdbMetadata', 'EnableTvdbMetadata', 'EnableFanartMetadata', 'EnableMdbListMetadata', 'HideUnreleased', 'EnableIntroDb', 'EnableP2p', 'P2pEnableDht', 'EnableCalendarNotifications', 'EnableDownloadQueue', 'EnableNotificationWebhooks', 'DownloadWindowEnabled'];
        var metadataFields = { get Name() { return t('title'); }, get Overview() { return t('synopsis'); }, get ReleaseDate() { return t('releaseDateYear'); }, get Genres() { return t('genres'); }, get Ratings() { return t('ratingsCertification'); }, get People() { return t('castCrew'); }, get Images() { return t('artwork'); }, get Runtime() { return t('runtime'); }, get ProductionLocations() { return t('productionLocations'); }, get Status() { return t('seriesStatus'); } };
        var providers = [
            { name: 'Tmdb', credential: 'TmdbReadAccessToken' },
            { name: 'Tvdb', credential: 'TvdbApiKey' },
            { name: 'Fanart', credential: 'FanartApiKey' },
            { name: 'MdbList', credential: 'MdbListApiKey' }
        ];
        var diagnostics = null;
        var diagnosticsLoading = false;
        var diagnosticsRequestId = 0;
        var cleanupPreview = null;
        var cleanupItems = [];
        var targets = null;
        var targetsRequest = 0;
        var targetsLoading = false;
        var seriesOffset = 0;
        var historyRequest = 0;
        var historyLoading = false;
        var historyDetailRequest = 0;
        var historySelection = null;
        var historyOffset = 0;
        var historyTotal = 0;
        var historyDetailLoading = false;
        var searchRequest = 0;
        var searchLoading = false;
        var searchItems = [];
        var searchOffset = 0;
        var additionsRequest = 0;
        var additionsLoading = false;
        var additionsItems = [];
        var additionsOffset = 0;
        var addingIds = new Set();
        var removingIds = new Set();
        var pageSize = 25;
        var lastHistoryRun = null;
        var users = null;
        var usersLoading = false;
        var usersRequest = 0;
        var inspectionRequest = 0;
        var inspectionLoading = false;
        var recoveryPreview = null;
        var recoverySelection = new Set();
        var recoveryOffset = 0;
        var p2pLoading = false;
        var p2pKnown = false;

        var guideConnection = '';
        var guideManifests = new Map();
        var guideManifestChecks = new Map();
        var savedGuideJson = '';
        var savedGuideConfig = null;
        function savedSettings() {
            if (savedGuideJson !== savedConfiguration) {
                savedGuideJson = savedConfiguration;
                savedGuideConfig = savedConfiguration ? JSON.parse(savedConfiguration) : null;
            }
            return savedGuideConfig;
        }
        var guidePreview = null;
        var guideRun = null;
        var latestRunStatus = null;
        function renderHealth() {
            if (!diagnostics) return;
            var facts = get('siphonHealthFacts'); facts.replaceChildren();
            fact(facts, function () { return t('serverVersion'); }, function () { return diagnostics.ServerVersion || t('notReported'); });
            fact(facts, function () { return t('sourceVersion'); }, function () { return diagnostics.SourceVersion || t('notReported'); });
            fact(facts, function () { return t('repositoryVersion'); }, function () { return diagnostics.RepositoryVersionAtBuild || t('notReported'); });
            fact(facts, function () { return t('observedAt'); }, function () { return formattedDate(diagnostics.GeneratedAtUtc); });
            fact(facts, function () { return t('storageStatus'); }, function () { return healthCode(diagnostics.StorageCode); });
            fact(facts, function () { return t('availableDisk'); }, function () { return bytes(diagnostics.Storage && diagnostics.Storage.AvailableBytes); });
            if (diagnostics.Storage && diagnostics.Storage.Code) fact(facts, function () { return t('diskObservation'); }, function () { return healthCode(diagnostics.Storage.Code); });
            var downloads = diagnostics.Downloads;
            if (downloads) {
                fact(facts, function () { return t('downloadQueue'); }, function () { return downloads.Enabled ? t('enabled') : t('disabled'); });
                fact(facts, function () { return t('queueCounts'); }, function () { return t('queueCountsValue', { queued: count(downloads.Queued), running: count(downloads.Running), paused: count(downloads.Paused), failed: count(downloads.Failed), completed: count(downloads.Completed) }); });
                fact(facts, function () { return t('downloadStorage'); }, function () { return t('storageValue', { stored: bytes(downloads.StoredBytes), reserved: bytes(downloads.ReservedBytes), maximum: bytes(downloads.MaximumBytes) }); });
            }
            var p2p = diagnostics.P2p;
            if (p2p) {
                fact(facts, function () { return t('p2pEngine'); }, function () { return p2p.Enabled ? t('enabled') : t('disabled'); });
                fact(facts, function () { return t('activeReaders'); }, function () { return count(p2p.ActiveStreams) + ' / ' + count(p2p.MaximumStreams); });
                fact(facts, function () { return t('p2pStorage'); }, function () { return t('storageValue', { stored: p2p.CacheMeasuredAtUtc ? bytes(p2p.CacheBytes) : t('cacheNotMeasured'), reserved: bytes(p2p.ReservedBytes), maximum: bytes(p2p.MaximumCacheBytes) }); });
            }
            if (p2p) fact(facts, function () { return t('cacheMeasuredAt'); }, function () { return p2p.CacheMeasuredAtUtc ? formattedDate(p2p.CacheMeasuredAtUtc) : t('cacheNotMeasured'); });
            if (diagnostics.P2pCode) fact(facts, function () { return t('p2pStorageObservation'); }, function () { return healthCode(diagnostics.P2pCode); });
            var notifications = diagnostics.Notifications;
            if (notifications) {
                fact(facts, function () { return t('externalNotifications'); }, function () { return notifications.Enabled ? t('enabled') : t('disabled'); });
                fact(facts, function () { return t('notificationCounts'); }, function () { return t('notificationCountsValue', { configured: count(notifications.ConfiguredUsers), pending: count(notifications.Pending), retrying: count(notifications.Retrying), abandoned: count(notifications.Abandoned) }); });
            }
            var run = diagnostics.Sync && (diagnostics.Sync.CurrentRun || diagnostics.Sync.LastRun);
            fact(facts, function () { return t('synchronization'); }, function () { return run ? taskState(run.State) + ' · ' + formattedDate(run.FinishedAtUtc || run.StartedAtUtc) : t('noRun'); });
            var providerList = get('siphonHealthProviders'); providerList.replaceChildren();
            (diagnostics.Providers || []).forEach(function (provider) {
                node('li', function () { return providerName(provider.Provider) + ' · ' + providerAvailability(provider) + quotaObservation(provider); }, providerList);
            });
        }
        function healthCode(code) {
            return code === 'Ok' || !code ? t('storageOk') : t('storageUnavailable', { code: String(code).slice(0, 80) });
        }
        function renderGuide() {
            if (!get('siphonGuideCatalog')) return;
            var saved = savedSettings();
            var select = get('siphonGuideCatalog');
            var selected = select.value;
            select.replaceChildren();
            node('option', function () { return t('chooseSavedCatalog'); }, select).value = '';
            (targets && targets.Catalogs || []).filter(supportsCatalog).forEach(function (target) { node('option', function () { return target.Name; }, select).value = target.Key; });
            if (Array.from(select.options).some(function (option) { return option.value === selected; })) select.value = selected;
            var target = targets && targets.Catalogs.find(function (entry) { return entry.Key === select.value; });
            var connected = Boolean(saved && saved.PublicBaseUrl && guideConnection === savedConfiguration);
            var checked = target && guideManifests.get(target.InstallationId) === savedConfiguration;
            var previewed = Boolean(guidePreview && target && guidePreview.key === target.Key && guidePreview.configuration === savedConfiguration);
            bindText(get('siphonGuideConnectionState'), function () { return !saved || !saved.PublicBaseUrl ? t('guideConnectionRequired') : dirty ? t('guideSaveFirst') : connected ? t('guideConnectionReady') : t('guideConnectionTestRequired'); });
            bindText(get('siphonGuideManifestState'), function () { return dirty ? t('guideSaveFirst') : !target ? t('guideCatalogRequired') : checked ? t('guideManifestReady') : t('guideManifestRequired'); });
            var manifestFacts = get('siphonGuideManifestFacts'); manifestFacts.replaceChildren();
            var check = checked && guideManifestChecks.get(target.InstallationId);
            if (check && check.Capabilities) {
                fact(manifestFacts, function () { return t('resources'); }, function () { return (check.Capabilities.Resources || []).map(function (resource) { return resourceName(resource.Name); }).join(' · ') || t('none2'); });
                fact(manifestFacts, function () { return t('types'); }, function () { return (check.Capabilities.Types || []).map(mediaType).join(' · ') || t('none2'); });
            }
            get('siphonGuideCatalog').disabled = busy || dirty || !targets;
            get('siphonGuideTestConnection').disabled = busy || dirty || !saved || !saved.PublicBaseUrl;
            get('siphonGuideTestManifest').disabled = busy || dirty || !target || !connected;
            get('siphonGuidePreview').disabled = busy || dirty || !target || !connected || !checked;
            get('siphonGuideImport').disabled = busy || !previewed || !connected || !checked || taskBlocked(taskDefinitions[3]);
            if (guideRun) {
                var run = latestRunStatus && [latestRunStatus.CurrentRun, latestRunStatus.LastRun].find(function (entry) { return entry && entry.TargetKey === guideRun.key && Date.parse(entry.StartedAtUtc) >= guideRun.started - 1000; });
                bindText(get('siphonGuideImportState'), function () { return run ? t('guideRunState', { state: taskState(run.State), added: count(run.Added), updated: count(run.Updated), failed: count(run.FailedSubscriptions) }) : t('guideImportRequested'); });
            } else bindText(get('siphonGuideImportState'), function () { return previewed ? t('guideImportReady') : t('guidePreviewRequired'); });
            if (!previewed) get('siphonGuidePreviewFacts').replaceChildren();
        }
        async function testGuideConnection() {
            if (dirty || !config || !config.PublicBaseUrl) return;
            var expected = savedConfiguration;
            var result = await request('Siphon/Diagnostics/Connection', 'POST');
            if (expected !== savedConfiguration || !visible) return;
            guideConnection = result.Success ? expected : '';
            showResult(get('siphonConnectionResult'), result, function () { return t('guideConnectionReady'); });
            message(function () { return result.Success ? t('guideConnectionReady') : ui.errorText({ code: result.Code }, t('guideConnectionFailed')); }, result.Success ? 'info' : 'error');
            renderGuide();
        }
        async function testGuideManifest() {
            var target = targets && targets.Catalogs.find(function (entry) { return entry.Key === get('siphonGuideCatalog').value; });
            if (!target || dirty || guideConnection !== savedConfiguration) return;
            var expected = savedConfiguration;
            var result = await request('Siphon/Diagnostics/Addons/' + encodeURIComponent(target.InstallationId), 'POST');
            if (expected !== savedConfiguration || !visible) return;
            if (result.Success) guideManifests.set(target.InstallationId, expected); else guideManifests.delete(target.InstallationId);
            if (result.Success && result.Check) guideManifestChecks.set(target.InstallationId, result.Check); else guideManifestChecks.delete(target.InstallationId);
            message(function () { return result.Success ? t('guideManifestReady') : ui.errorText({ code: result.Code }, t('guideManifestFailed')); }, result.Success ? 'info' : 'error');
            renderGuide();
        }
        function previewGuideCatalog() {
            if (dirty || !targets) return;
            var target = targets.Catalogs.find(function (entry) { return entry.Key === get('siphonGuideCatalog').value; });
            if (!target || guideConnection !== savedConfiguration || guideManifests.get(target.InstallationId) !== savedConfiguration) return;
            var saved = savedSettings();
            var addon = saved.Addons.find(function (entry) { return canonicalId(entry.Id) === canonicalId(target.InstallationId); });
            var catalog = addon && addon.Catalogs.find(function (entry) { return canonicalId(entry.Key) === canonicalId(target.CatalogId); });
            if (!catalog || !catalog.Enabled || !addon.Enabled || !supportsCatalog(catalog)) return;
            guidePreview = { key: target.Key, configuration: savedConfiguration };
            var facts = get('siphonGuidePreviewFacts'); facts.replaceChildren();
            fact(facts, function () { return t('catalog'); }, function () { return target.Name; });
            fact(facts, function () { return t('mediaType'); }, function () { return mediaType(catalog.Type); });
            fact(facts, function () { return t('presentation'); }, function () { return presentationLabel(catalog.Presentation); });
            fact(facts, function () { return t('itemLimit'); }, function () { return count(catalog.MaxItems == null ? saved.MaxItemsPerCatalog : catalog.MaxItems); });
            fact(facts, function () { return t('filterCount'); }, function () { return count((catalog.Extras || []).length); });
            renderGuide();
            get('siphonGuideImport').focus();
        }
        function rejectionReason(code) { return { InvalidP2p: t('invalidP2p'), P2pDisabled: t('p2pDisabled'), UnsupportedUrl: t('unsupportedUrl'), UnsupportedHeaders: t('unsupportedHeaders') }[code] || t('unknownReason'); }
        function p2pState(state) { return { Preparing: t('preparing'), Metadata: t('p2pMetadata'), Streaming: t('p2pStreaming') }[state] || t('unknownState'); }
        function resourceName(name) { return { catalog: t('catalogs'), meta: t('metadata'), stream: t('streams'), subtitles: t('subtitles'), addon_catalog: t('addonDiscovery') }[name] || t('unknownResource'); }
        function mediaType(type) { return { get movie() { return t('movies'); }, get Movie() { return t('movie'); }, get series() { return t('series'); }, get Series() { return t('series'); }, get Episode() { return t('episode'); }, get Season() { return t('season'); }, get anime() { return t('anime'); } }[type] || t('otherMedia'); }
        function presentationLabel(value) { return { get Library() { return t('library'); }, get Collection() { return t('collection'); }, get Both() { return t('libraryAndCollection'); } }[value] || t('library'); }

        function canonicalId(value) { return String(value || '').replace(/-/g, '').toLowerCase(); }
        function catalogTarget(addon, catalog) {
            return targets && targets.Catalogs.find(function (target) {
                return canonicalId(target.InstallationId) === canonicalId(addon.Id) && canonicalId(target.CatalogId) === canonicalId(catalog.Key);
            });
        }
        function updateFeatureState() {
            var blocked = busy || targetsLoading || !targets || taskBlocked(taskDefinitions[3]);
            get('siphonTarget').disabled = blocked || !get('siphonTargetSeries').value;
            get('siphonTargetSeries').disabled = blocked || !targets.Series.length;
            get('siphonRefreshTargets').disabled = busy || targetsLoading || !config;
            page.querySelectorAll('[data-catalog-sync]').forEach(function (control) {
                var target = catalogTarget({ Id: control.dataset.installationId }, { Key: control.dataset.catalogId });
                control.disabled = blocked || !target;
                bindText(control, function () { return dirty ? t('saveOrDiscardChangesFirst') : running() ? t('waitForTheActiveTaskToStop') : !target ? t('noEnabledSavedTargetAvailableSaveSettings') : ''; }, 'title');
            });
            get('siphonSearchSubmit').disabled = busy || !config || dirty || !get('siphonSearchTerm').value.trim();
            get('siphonSearchCancel').hidden = !searchLoading;
            get('siphonRefreshAdditions').disabled = busy || !config || additionsLoading;
            page.querySelectorAll('[data-search-add]').forEach(function (control) {
                control.disabled = busy || !config || dirty || !tasksKnown || running() || addingIds.has(control.dataset.searchAdd) || removingIds.has(control.dataset.searchAdd);
                bindText(control, function () { return addingIds.has(control.dataset.searchAdd) ? t('adding') : t('addToLibrary'); });
            });
            page.querySelectorAll('[data-search-remove]').forEach(function (control) {
                control.disabled = busy || !config || dirty || !tasksKnown || running() || removingIds.has(control.dataset.searchRemove) || addingIds.has(control.dataset.searchRemove);
                bindText(control, function () { return removingIds.has(control.dataset.searchRemove) ? t('removingSavedAddition') : t('removeSavedAddition'); });
            });
            get('siphonRefreshHistory').disabled = historyLoading || !visible;
            get('siphonHistoryPrevious').disabled = historyDetailLoading || historyOffset === 0;
            get('siphonHistoryNext').disabled = historyDetailLoading || historyOffset + pageSize >= historyTotal;
            get('siphonReloadUsers').disabled = busy || usersLoading || !config;
            get('siphonProfileUser').disabled = busy || usersLoading || !users || !config;
            get('siphonP2pRefresh').disabled = busy || p2pLoading || !config;
            get('siphonP2pCleanup').disabled = busy || dirty || p2pLoading || !config || !p2pKnown;
            get('siphonInspect').disabled = busy || inspectionLoading || !config;
            get('siphonSources').disabled = busy || inspectionLoading || dirty || !config;
            get('siphonSourcesRefresh').disabled = busy || inspectionLoading || dirty || !config;
            get('siphonInspectCancel').hidden = !inspectionLoading;
            get('siphonRecoveryPreview').disabled = busy || dirty || !config || running() || !tasksKnown;
            get('siphonRecoveryNext').disabled = busy || dirty || !config || running() || !tasksKnown;
            get('siphonRecoveryExecute').disabled = busy || dirty || running() || !tasksKnown || !recoveryPreview || !recoverySelection.size || Date.parse(recoveryPreview.ExpiresAtUtc) <= Date.now();
        }
        function renderSeries() {
            var select = get('siphonTargetSeries');
            var selected = select.value;
            var query = get('siphonSeriesFilter').value.trim().toLocaleLowerCase();
            var series = targets ? targets.Series.filter(function (entry) { return entry.Name.toLocaleLowerCase().includes(query); }) : [];
            seriesOffset = Math.min(seriesOffset, Math.max(0, Math.ceil(series.length / pageSize) - 1) * pageSize);
            select.replaceChildren();
            node('option', function () { return series.length ? t('chooseASeries') : targets ? t('noMatchingExistingSeries') : t('targetsUnavailable'); }, select).value = '';
            series.slice(seriesOffset, seriesOffset + pageSize).forEach(function (entry) { node('option', function () { return entry.Name; }, select).value = entry.ContentKey; });
            if (Array.from(select.options).some(function (option) { return option.value === selected; })) select.value = selected;
            bindText(get('siphonSeriesPage'), function () { return pageRange(seriesOffset, series.length); });
            get('siphonSeriesPrevious').disabled = seriesOffset === 0;
            get('siphonSeriesNext').disabled = seriesOffset + pageSize >= series.length;
            updateFeatureState();
        }
        async function refreshTargets() {
            if (!config || !visible) return;
            var id = ++targetsRequest;
            var expected = generation;
            targetsLoading = true; targets = null;
            bindText(get('siphonTargetsStatus'), function () { return t('loadingSavedSynchronizationTargets'); });
            bindText(get('siphonCatalogTargetsStatus'), function () { return t('loadingSavedSynchronizationTargets'); });
            renderSeries();
            try {
                var result = await request('Siphon/Sync/Targets', 'GET');
                if (id !== targetsRequest || expected !== generation || !visible) return;
                targets = result;
                function text() { return result.Catalogs.length + t('savedCatalogs') + result.Series.length + t('existingSeriesSaveChangesBeforeRunningA'); }
                bindText(get('siphonTargetsStatus'), function () { return text; });
                bindText(get('siphonCatalogTargetsStatus'), function () { return text; });
            } catch (error) {
                if (id !== targetsRequest || expected !== generation || !visible) return;
                bindText(get('siphonTargetsStatus'), function () { return featureError(error, t('targetsCouldNotBeLoadedChooseReload')); });
                bindText(get('siphonCatalogTargetsStatus'), function () { return featureError(error, t('targetedSyncUnavailableOpenTasksAndChoose')); });
            } finally {
                if (id === targetsRequest) { targetsLoading = false; renderSeries(); renderGuide(); }
            }
        }
        function startTarget(selection) {
            work(async function () {
                if (dirty || !targets) return;
                await refreshStatus();
                if (taskBlocked(taskDefinitions[3])) return;
                var saved = selection.CatalogKey
                    ? targets.Catalogs.some(function (entry) { return entry.Key === selection.CatalogKey; })
                    : targets.Series.some(function (entry) { return entry.ContentKey === selection.ContentKey; });
                if (!saved) return;
                try {
                    var requestedAt = Date.now();
                    await request('Siphon/Sync/Target', 'POST', selection);
                    if (guidePreview && guidePreview.key === selection.CatalogKey) guideRun = { key: selection.CatalogKey, started: requestedAt };
                    renderGuide();
                    selectPanel('siphonPanelTasks', true);
                    message(function () { return t('targetedSynchronizationRequestedProgressUpdatesAutomatically'); });
                    await refreshStatus();
                } catch (error) {
                    message(function () { return featureError(error, t('targetedSynchronizationCouldNotStartAnotherOperation')); }, 'error');
                    await refreshTargets();
                }
            });
        }
        function quotaObservation(status) {
            if (!status) return '';
            var observed = Date.parse(status.QuotaObservedAtUtc);
            var reset = Date.parse(status.QuotaResetUtc);
            if (!Number.isFinite(observed)) return t('quotaObservationUnavailable');
            var expired = Number.isFinite(reset) && reset <= Date.now();
            var stale = Date.now() - observed > 24 * 60 * 60 * 1000;
            var values = t('quotaLastObserved') + formattedDate(status.QuotaObservedAtUtc) + t('notALiveBudget');
            if (expired) values += t('resetTimeHasPassedRemainingUnavailable');
            else if (stale) values += t('observationIsOver24HoursOldRemaining');
            else values += t('remainingAtObservation') + (status.QuotaRemaining == null ? t('unavailable') : count(status.QuotaRemaining)) + '.';
            values += t('limitAtObservation') + (status.QuotaLimit == null ? t('unavailable') : count(status.QuotaLimit)) + '.';
            values += t('reset') + (Number.isFinite(reset) ? formattedDate(status.QuotaResetUtc) : t('unavailable')) + '.';
            return values;
        }
        function invalidateHistory() {
            historyRequest++; historyDetailRequest++;
            historyLoading = false; historyDetailLoading = false;
            get('siphonHistory').setAttribute('aria-busy', 'false');
            get('siphonHistoryItems').setAttribute('aria-busy', 'false');
            if (historySelection) bindText(get('siphonHistoryDetailStatus'), function () { return t('savedViewRefreshHistoryToReloadDecisions'); });
        }
        function historyScope(scope) {
            return { get Catalog() { return t('oneCatalog'); }, get Series() { return t('oneSeries'); }, get All() { return t('allCatalogs'); }, get FollowedSeries() { return t('followedSeries'); } }[scope] || t('synchronization');
        }
        async function refreshHistory() {
            if (!visible || get('siphonPanelTasks').hidden) return;
            var id = ++historyRequest;
            var expected = generation;
            historyLoading = true;
            bindText(get('siphonHistoryStatus'), function () { return t('loadingRecentRuns'); });
            get('siphonHistory').setAttribute('aria-busy', 'true');
            updateFeatureState();
            try {
                var runs = await request('Siphon/Sync/History', 'GET');
                if (id !== historyRequest || expected !== generation || !visible) return;
                var list = get('siphonHistory');
                list.replaceChildren();
                runs.slice(0, 20).forEach(function (run) {
                    var row = node('li', undefined, list);
                    var definition = taskDefinitions.find(function (entry) { return entry.kind === run.Kind; });
                    node('strong', function () { return (run.Kind === 'Startup' ? t('startupRestoration') : definition ? definition.name : t('synchronization')) + ' · ' + taskState(run.State); }, row);
                    node('p', function () { return historyScope(run.Scope) + ' · ' + formattedDate(run.StartedAtUtc) + ' · ' + count(run.DecisionCount) + t('decisions'); }, row).className = 'siphon-help';
                    node('p', function () { return count(run.Added) + t('added') + count(run.Updated) + t('updated') + count(run.Unchanged) + t('unchanged') + count(run.Preserved) + t('preserved') + count(run.Removed) + t('removed'); }, row).className = 'siphon-help';
                    var control = button(row, function () { return t('viewTitleDecisions'); }, function () { historySelection = run.Id; loadHistoryDetails(0); });
                    bindText(control, function () { return  t('viewDecisionsFor') + historyScope(run.Scope) + t('started') + formattedDate(run.StartedAtUtc); }, 'aria-label');
                });
                bindText(get('siphonHistoryStatus'), function () { return runs.length ? t('mostRecent') + Math.min(20, runs.length) + t('recordedRuns') : t('noSynchronizationHistoryRecordedYet'); });
                if (historySelection && runs.some(function (run) { return run.Id === historySelection; })) loadHistoryDetails(historyOffset);
                else if (historySelection) {
                    historySelection = null; historyDetailRequest++;
                    get('siphonHistoryDetails').hidden = true;
                    get('siphonHistoryItems').replaceChildren();
                }
            } catch (error) {
                if (id === historyRequest && expected === generation && visible) bindText(get('siphonHistoryStatus'), function () { return featureError(error, t('historyUnavailablePreviouslyShownRunsMayBe')); });
            } finally {
                if (id === historyRequest) { historyLoading = false; get('siphonHistory').setAttribute('aria-busy', 'false'); updateFeatureState(); }
            }
        }
        async function loadHistoryDetails(offset) {
            if (!historySelection || !visible || get('siphonPanelTasks').hidden) return;
            var id = ++historyDetailRequest;
            var expected = generation;
            var selected = historySelection;
            historyOffset = offset; historyDetailLoading = true; historyTotal = 0;
            get('siphonHistoryDetails').hidden = false;
            get('siphonHistoryRetry').hidden = true;
            bindText(get('siphonHistoryDetailStatus'), function () { return t('loadingTitleDecisions'); });
            get('siphonHistoryItems').replaceChildren();
            get('siphonHistoryItems').setAttribute('aria-busy', 'true');
            updateFeatureState();
            try {
                var result = await request('Siphon/Sync/History/' + encodeURIComponent(selected) + '?startIndex=' + offset + '&limit=' + pageSize, 'GET');
                if (id !== historyDetailRequest || expected !== generation || !visible) return;
                historyTotal = result.TotalCount;
                var actions = { get Pending() { return t('pending'); }, get Added() { return t('added2'); }, get Updated() { return t('updated2'); }, get Unchanged() { return t('unchanged2'); }, get Preserved() { return t('preserved2'); }, get Removed() { return t('removed2'); }, get Error() { return t('error'); }, get ProviderPaused() { return t('providerPaused'); } };
                var reasons = { get Discovered() { return t('discoveredInCatalog'); }, get SeriesRefresh() { return t('existingSeriesRefreshed'); }, get IncompleteCatalog() { return t('incompleteCatalogRetained'); }, get CatalogUnavailable() { return t('catalogUnavailable'); }, get MetadataUnavailable() { return t('metadataUnavailable'); }, get NoChanges() { return t('noChangesDetected'); }, get Published() { return t('publishedToJellyfin'); }, get CleanupRetention() { return t('retainedByCleanupPolicy'); }, get ConfirmedAbsence() { return t('confirmedAbsent'); }, get MetadataLocked() { return t('metadataLockedInJellyfin'); }, get ProviderPaused() { return t('providerCooldownActive'); }, get ProviderError() { return t('providerRequestFailed'); }, get CacheHit() { return t('cachedMetadataReused'); }, get Cancelled() { return t('runCancelled'); }, get NotPublished() { return t('notPublished'); }, get Interrupted() { return t('runInterrupted'); } };
                result.Items.forEach(function (entry) {
                    var row = node('li', undefined, get('siphonHistoryItems'));
                    node('strong', function () { return entry.Name || t('untitledItem'); }, row);
                    node('p', function () { return actions[entry.Action] || t('unrecognizedDecision'); }, row);
                    node('p', function () { return (entry.Reasons || []).map(function (reason) { return reasons[reason] || t('unknownReason'); }).join(' · ') || t('noAdditionalReasonRecorded'); }, row).className = 'siphon-help';
                });
                bindText(get('siphonHistoryDetailStatus'), function () { return (historyTotal ? pageRange(offset, historyTotal) + t('decisions2') : t('noPerTitleDecisionsWereRecorded'))
                    + (result.StorageCode && result.StorageCode !== 'Ok' ? t('someHistoryDetailsAreUnavailableFromStorage') : ''); });
            } catch (error) {
                if (id === historyDetailRequest && expected === generation && visible) {
                    bindText(get('siphonHistoryDetailStatus'), function () { return featureError(error, t('decisionsUnavailableTheRunMayHaveExpired')); });
                    get('siphonHistoryRetry').hidden = false;
                }
            } finally {
                if (id === historyDetailRequest) { historyDetailLoading = false; get('siphonHistoryItems').setAttribute('aria-busy', 'false'); updateFeatureState(); }
            }
        }
        function pageRange(offset, total) { return total ? (offset + 1) + '–' + Math.min(total, offset + pageSize) + t('of') + total : t('0Items'); }
        function invalidateSearch() {
            searchRequest++; additionsRequest++;
            if (searchLoading) bindText(get('siphonSearchStatus'), function () { return t('searchCancelledSubmitYourQueryToSearch'); });
            if (additionsLoading) bindText(get('siphonAdditionsStatus'), function () { return t('loadingCancelledRefreshAdditionsToRetry'); });
            searchLoading = false; additionsLoading = false;
            get('siphonSearchResults').setAttribute('aria-busy', 'false');
            get('siphonAdditions').setAttribute('aria-busy', 'false');
            updateFeatureState();
        }
        function renderSearchItems(manual) {
            var items = manual ? additionsItems : searchItems;
            var offset = manual ? additionsOffset : searchOffset;
            var prefix = manual ? 'siphonAdditions' : 'siphonSearch';
            var parent = get(manual ? 'siphonAdditions' : 'siphonSearchResults');
            parent.replaceChildren();
            items.slice(offset, offset + pageSize).forEach(function (item) {
                var row = node('article', undefined, parent);
                row.className = 'siphon-media-row';
                var image = node('img', undefined, row);
                image.width = 48; image.height = 72; image.alt = ''; image.loading = 'lazy';
                image.src = ApiClient.getUrl('Items/' + encodeURIComponent(item.Id) + '/Images/Primary', { maxWidth: 96 });
                image.addEventListener('error', function () { image.hidden = true; });
                var content = node('div', undefined, row);
                var title = node('h3', undefined, content);
                var link = node('a', function () { return item.Name; }, title);
                link.href = '#/details?id=' + encodeURIComponent(item.Id) + '&serverId=' + encodeURIComponent(ApiClient.serverId());
                node('p', function () { return (item.Type === 'Series' ? t('series') : t('movie')) + (item.Year == null ? '' : ' · ' + item.Year); }, content).className = 'siphon-help';
                if (item.IsAdded) {
                    node('p', function () { return t('inYourLibrary'); }, content).className = 'siphon-help';
                    var remove = button(content, function () { return t('removeSavedAddition'); }, function () { removeSearchItem(item); });
                    remove.dataset.searchRemove = item.Id;
                    bindText(remove, function () { return t('removeSavedTitlePrefix') + item.Name + t('removeSavedTitleSuffix'); }, 'aria-label');
                } else if (item.CanAdd) {
                    var add = button(content, function () { return t('addToLibrary'); }, function () { addSearchItem(item); });
                    add.dataset.searchAdd = item.Id;
                    bindText(add, function () { return  t('add') + item.Name + t('toLibrary'); }, 'aria-label');
                } else node('p', function () { return t('cannotAddThisTitleItMayAlready'); }, content).className = 'siphon-help';
                var outcome = node('p', function () { return ''; }, content);
                outcome.dataset.searchOutcome = item.Id;
                outcome.className = 'siphon-result'; outcome.setAttribute('role', 'status');
            });
            bindText(get(prefix + 'Page'), function () { return pageRange(offset, items.length); });
            get(prefix + 'Previous').disabled = offset === 0;
            get(prefix + 'Next').disabled = offset + pageSize >= items.length;
            updateFeatureState();
        }
        async function searchTitles() {
            var term = get('siphonSearchTerm').value.trim();
            if (!config || dirty || busy || !term) return;
            var id = ++searchRequest;
            var expected = generation;
            searchLoading = true; searchItems = []; searchOffset = 0;
            renderSearchItems(false);
            get('siphonSearchResults').setAttribute('aria-busy', 'true');
            bindText(get('siphonSearchStatus'), function () { return t('searchingEnabledAddons'); });
            try {
                var result = await request('Siphon/Search?searchTerm=' + encodeURIComponent(term), 'GET');
                if (id !== searchRequest || expected !== generation || !visible) return;
                searchItems = result.Items;
                renderSearchItems(false);
                bindText(get('siphonSearchStatus'), function () { return result.Items.length ? result.Items.length + t('titlesReturnedFor') + term + t('previewResultsAreTemporaryUseAddTo') : t('noMatchingTitlesReturnedTryAnotherTitle'); });
            } catch (error) {
                if (id === searchRequest && expected === generation && visible) bindText(get('siphonSearchStatus'), function () { return featureError(error, t('searchFailedCheckAddonAvailabilityAndYour')); });
            } finally {
                if (id === searchRequest) { searchLoading = false; get('siphonSearchResults').setAttribute('aria-busy', 'false'); updateFeatureState(); }
            }
        }
        async function refreshAdditions() {
            if (!config || !visible || get('siphonPanelSearch').hidden) return;
            var id = ++additionsRequest;
            var expected = generation;
            additionsLoading = true; updateFeatureState();
            get('siphonAdditions').setAttribute('aria-busy', 'true');
            bindText(get('siphonAdditionsStatus'), function () { return t('loadingDurableAdditions'); });
            try {
                var result = await request('Siphon/Search/Items', 'GET');
                if (id !== additionsRequest || expected !== generation || !visible) return;
                additionsItems = result.Items;
                additionsOffset = Math.min(additionsOffset, Math.max(0, Math.ceil(additionsItems.length / pageSize) - 1) * pageSize);
                renderSearchItems(true);
                bindText(get('siphonAdditionsStatus'), function () { return additionsItems.length ? additionsItems.length + t('durableAdditions') : t('noExplicitAdditionsYetSearchForA'); });
            } catch (error) {
                if (id === additionsRequest && expected === generation && visible) bindText(get('siphonAdditionsStatus'), function () { return featureError(error, t('additionsCouldNotBeLoadedPreviouslyShown')); });
            } finally {
                if (id === additionsRequest) { additionsLoading = false; get('siphonAdditions').setAttribute('aria-busy', 'false'); updateFeatureState(); }
            }
        }
        async function addSearchItem(item) {
            if (!item.CanAdd || item.IsAdded || addingIds.has(item.Id) || removingIds.has(item.Id) || dirty || busy || !tasksKnown || running()) return;
            var expected = generation;
            var expectedSearch = searchRequest;
            var focused = document.activeElement;
            var restoreFocus = focused && focused.dataset && focused.dataset.searchAdd === item.Id;
            addingIds.add(item.Id); updateFeatureState();
            try {
                var result = await request('Siphon/Search/Items/' + encodeURIComponent(item.Id) + '/Add', 'POST');
                if (expected !== generation || !visible) return;
                searchItems = searchItems.map(function (entry) { return entry.Id === result.Id ? result : entry; });
                renderSearchItems(false);
                if (expectedSearch === searchRequest) bindText(get('siphonSearchStatus'), function () { return result.Name + t('isNowInYourLibrary'); });
                refreshAdditions();
            } catch (error) {
                if (expected !== generation || !visible || expectedSearch !== searchRequest) return;
                page.querySelectorAll('[data-search-outcome]').forEach(function (outcome) {
                    if (outcome.dataset.searchOutcome === item.Id) {
                        bindText(outcome, function () { return featureError(error, t('couldNotAddThisTitleRetryWhen')); });
                        outcome.dataset.kind = 'error';
                    }
                });
            } finally {
                addingIds.delete(item.Id); updateFeatureState();
                if (expected === generation && expectedSearch === searchRequest && visible && restoreFocus
                    && (document.activeElement === focused || document.activeElement === document.body)) {
                    var next = Array.from(get('siphonSearchResults').querySelectorAll('[data-search-add], [data-search-remove]'))
                        .find(function (control) { return control.dataset.searchAdd === item.Id || control.dataset.searchRemove === item.Id; });
                    if (next && !next.disabled) next.focus();
                }
            }
        }
        async function removeSearchItem(item) {
            if (!item.IsAdded || addingIds.has(item.Id) || removingIds.has(item.Id) || dirty || busy || !tasksKnown || running()) return;
            if (!window.confirm(t('confirmRemoveSavedPrefix') + item.Name + t('confirmRemoveSavedSuffix'))) return;
            var expected = generation;
            var focused = document.activeElement;
            var restoreFocus = focused && focused.dataset && focused.dataset.searchRemove === item.Id;
            removingIds.add(item.Id); updateFeatureState();
            try {
                var result = await request('Siphon/Search/Items/' + encodeURIComponent(item.Id), 'DELETE');
                if (expected !== generation || !visible) return;
                restoreFocus = restoreFocus && (document.activeElement === focused || document.activeElement === document.body);
                searchRequest++; additionsRequest++;
                searchLoading = false; additionsLoading = false;
                get('siphonSearchResults').setAttribute('aria-busy', 'false');
                get('siphonAdditions').setAttribute('aria-busy', 'false');
                searchItems = searchItems.filter(function (entry) { return entry.Id !== result.Id || result.RemainingItem; })
                    .map(function (entry) { return entry.Id === result.Id ? result.RemainingItem : entry; });
                searchOffset = Math.min(searchOffset, Math.max(0, Math.ceil(searchItems.length / pageSize) - 1) * pageSize);
                additionsItems = additionsItems.filter(function (entry) { return entry.Id !== result.Id; });
                additionsOffset = Math.min(additionsOffset, Math.max(0, Math.ceil(additionsItems.length / pageSize) - 1) * pageSize);
                renderSearchItems(false); renderSearchItems(true);
                bindText(get('siphonAdditionsStatus'), function () { return result.RemainingItem ? t('savedAdditionRemovedStillCatalog') : t('savedAdditionRemoved'); });
                if (restoreFocus) {
                    var next = get('siphonAdditions').querySelector('[data-search-remove]') || get('siphonRefreshAdditions');
                    next.focus();
                }
            } catch (error) {
                if (expected !== generation || !visible) return;
                bindText(get('siphonAdditionsStatus'), function () { return featureError(error, t('couldNotRemoveSavedTitle')); });
                page.querySelectorAll('[data-search-outcome]').forEach(function (outcome) {
                    if (outcome.dataset.searchOutcome === item.Id) {
                        bindText(outcome, function () { return featureError(error, t('couldNotRemoveSavedTitle')); });
                        outcome.dataset.kind = 'error';
                    }
                });
            } finally {
                removingIds.delete(item.Id); updateFeatureState();
                if (expected === generation && visible && restoreFocus && document.activeElement === document.body
                    && focused.isConnected && !focused.disabled && !focused.closest('[hidden]')) focused.focus();
            }
        }
        var tabs = Array.from(page.querySelectorAll('[role="tab"]'));

        function get(id) { return page.querySelector('#' + id); }
        function node(tag, text, parent) {
            var element = document.createElement(tag);
            if (text !== undefined) bindText(element, function () { return text; });
            if (parent) parent.appendChild(element);
            return element;
        }
        function message(text, kind) {
            bindText(get('siphonMessage'), function () { return text; });
            get('siphonMessage').dataset.kind = kind || 'info';
        }
        function selectPanel(id, focus) {
            if (id !== 'siphonPanelSearch' && !get('siphonPanelSearch').hidden) invalidateSearch();
            if (id !== 'siphonPanelTasks' && !get('siphonPanelTasks').hidden) invalidateHistory();
            tabs.forEach(function (tab) {
                var selected = tab.getAttribute('aria-controls') === id;
                tab.setAttribute('aria-selected', String(selected));
                tab.tabIndex = selected ? 0 : -1;
                get(tab.getAttribute('aria-controls')).hidden = !selected;
                if (selected && focus) tab.focus();
            });
            if (id === 'siphonPanelDiagnostics' && config && !diagnostics && !diagnosticsLoading) refreshDiagnostics();
            if (id === 'siphonPanelTasks' && visible) { refreshStatus(); refreshHistory(); }
            if (id === 'siphonPanelSearch' && visible && config) refreshAdditions();
            if (id === 'siphonPanelProfiles' && visible && config && !users && !usersLoading) loadUsers();
            if (id === 'siphonPanelPlayback' && visible && config && !p2pKnown && !p2pLoading) refreshP2p();
            if ((id === 'siphonPanelCatalogs' || id === 'siphonPanelTasks') && visible && config && !targets && !targetsLoading) refreshTargets();
        }
        function taskFor(definition) { return nativeTasks.find(function (task) { return task.Key === definition.key; }); }
        function activeTask() {
            var definition = taskDefinitions.find(function (entry) { return entry.kind === currentRunKind; });
            var current = definition && taskFor(definition);
            if (current && current.State !== 'Idle') return current;
            var candidate = null;
            for (var task of nativeTasks) {
                if (task.State === 'Idle') continue;
                if (candidate) return null;
                candidate = task;
            }
            return candidate;
        }
        function running() { return nativeTasks.some(function (task) { return task.State !== 'Idle'; }); }
        function taskBlocked(definition) { return !tasksKnown || !taskFor(definition) || !config || dirty || running() || !config.PublicBaseUrl; }
        function updateState() {
            get('siphonFields').disabled = busy || !config;
            get('siphonRetry').hidden = Boolean(config) || busy;
            get('siphonSave').disabled = busy || !config || !dirty || running() || !tasksKnown;
            get('siphonSaveBar').dataset.dirty = String(dirty);
            bindText(get('siphonSaveState'), function () { return !config ? t('settingsUnavailable') : busy ? t('working') : !tasksKnown && dirty ? t('refreshTaskStatusBeforeSavingChanges') : running() && dirty ? t('stopTheActiveTaskBeforeSavingChanges') : dirty ? t('youHaveUnsavedChanges') : t('allChangesSaved'); });
            bindText(get('siphonReload'), function () { return dirty ? t('discardChanges') : t('reloadSaved'); });
            get('siphonRefresh').disabled = busy || statusLoading;
            taskDefinitions.forEach(function (definition) {
                var control = get(definition.button);
                control.disabled = busy || taskBlocked(definition);
                bindText(control, function () { return dirty ? t('saveOrDiscardChangesFirstThisTask') : !tasksKnown ? t('refreshTaskStatusBeforeStarting') : !taskFor(definition) ? t('nativeTaskUnavailable') : running() ? t('waitForTheActiveSiphonTaskTo') : !config || !config.PublicBaseUrl ? t('saveThePublicServerURLFirst') : ''; }, 'title');
            });
            get('siphonConfirmFull').disabled = busy || taskBlocked(taskDefinitions[2]);
            if (dirty || running() || !tasksKnown) {
                get('siphonFullConfirm').hidden = true;
                get('siphonFull').setAttribute('aria-expanded', 'false');
            }
            var active = activeTask();
            get('siphonStop').hidden = !running();
            get('siphonStop').disabled = busy || !tasksKnown || !active || active.State === 'Cancelling';
            bindText(get('siphonStop'), function () { return active && active.State === 'Cancelling' ? t('stopping') : t('stopActiveTask'); });
            page.querySelectorAll('[data-saved-action]').forEach(function (control) {
                control.disabled = busy || !config || dirty;
                bindText(control, function () { return dirty ? t('saveOrDiscardChangesFirstThisAction') : ''; }, 'title');
            });
            get('siphonPreviewCleanup').disabled = busy || !config || dirty || running() || !tasksKnown;
            get('siphonExecuteCleanup').hidden = !cleanupPreview || cleanupPreview.TotalEligible === 0;
            get('siphonExecuteCleanup').disabled = busy || dirty || running() || !tasksKnown || !cleanupPreview || Date.parse(cleanupPreview.ExpiresAtUtc) <= Date.now();
            get('siphonMoreCleanup').disabled = busy || dirty || running() || !tasksKnown;
            get('siphonRefreshDiagnostics').disabled = busy || !config || diagnosticsLoading;
            get('siphonExportDiagnostics').disabled = busy || diagnosticsLoading || !diagnostics;
            updateFeatureState();
            renderGuide();
            if (config) {
                var enabled = config.Addons.filter(function (addon) { return addon.Enabled; });
                var catalogs = enabled.reduce(function (count, addon) { return count + addon.Catalogs.filter(function (catalog) { return catalog.Enabled; }).length; }, 0);
                bindText(get('siphonOverview'), function () { return enabled.length + t('of') + config.Addons.length + t('addonsEnabled') + catalogs + t('catalogsSelected'); });
            }
        }
        function markDirty() {
            dirty = true;
            if (recoveryPreview) invalidateRecovery(function () { return t('settingsChangedSaveOrDiscardThenPreview'); });
            if (cleanupPreview) {
                cleanupPreview = null;
                bindText(get('siphonCleanupStatus'), function () { return t('settingsChangedSaveOrDiscardThenGenerate'); });
                get('siphonMoreCleanup').hidden = true;
            }
            updateState();
        }
        function uuid() {
            var bytes = crypto.getRandomValues(new Uint8Array(16));
            bytes[6] = (bytes[6] & 15) | 64;
            bytes[8] = (bytes[8] & 63) | 128;
            return Array.from(bytes, function (byte) { return byte.toString(16).padStart(2, '0'); }).join('');
        }
        async function request(path, method, data) {
            var options = { url: ApiClient.getUrl(path), type: method, dataType: 'json' };
            if (data !== undefined) { options.contentType = 'application/json'; options.data = JSON.stringify(data); }
            try { return await ApiClient.ajax(options); }
            catch (error) { throw await ui.error(error, t('requestFailed')); }
        }
        async function readError(error, fallback) {
            var failure = await ui.error(error, resolveText(fallback));
            return { Code: failure.code, get Message() { return ui.errorText(failure, resolveText(fallback)); } };
        }
        async function work(action) {
            if (busy) return;
            busy = true;
            updateState();
            try { await action(); }
            catch (error) { var failure = await ui.error(error, t('operationFailed')); message(function () { return ui.errorText(failure, t('operationFailed')); }, 'error'); }
            finally { busy = false; updateState(); }
        }
        function input(parent, title, value, type) {
            var container = node('div', undefined, parent);
            container.className = 'inputContainer';
            var id = 'siphonDynamic' + (++nextId);
            var label = node('label', title, container);
            label.className = 'inputLabel';
            label.htmlFor = id;
            var control = document.createElement('input');
            // This control owns its paired label; emby-input would try to create another.
            control.className = 'emby-input';
            control.id = id;
            control.type = type || 'text';
            control.value = value == null ? '' : value;
            container.appendChild(control);
            return control;
        }
        function checkbox(parent, title, value, changed) {
            var container = node('div', undefined, parent);
            container.className = 'checkboxContainer';
            var label = node('label', undefined, container);
            label.className = 'emby-checkbox-label';
            var control = document.createElement('input', 'emby-checkbox');
            control.setAttribute('is', 'emby-checkbox');
            control.type = 'checkbox';
            control.checked = value;
            label.appendChild(control);
            node('span', title, label);
            control.addEventListener('change', function () { changed(control.checked); markDirty(); });
            return control;
        }
        function button(parent, title, action, icon) {
            var control = document.createElement('button');
            control.type = 'button';
            control.className = 'siphon-text-button' + (icon ? ' siphon-icon-button' : '');
            if (icon) {
                bindText(control, title, 'title');
                bindText(control, title, 'aria-label');
                var glyph = node('span', function () { return icon; }, control);
                glyph.className = 'material-icons';
                glyph.setAttribute('aria-hidden', 'true');
            } else bindText(control, function () { return title; });
            control.addEventListener('click', action);
            parent.appendChild(control);
            return control;
        }
        function selectInput(parent, title, options, value) {
            var container = node('div', undefined, parent);
            container.className = 'inputContainer';
            var control = node('select', undefined, container);
            control.id = 'siphonDynamic' + (++nextId);
            control.setAttribute('is', 'emby-select'); control.className = 'emby-select';
            var label = node('label', title);
            label.className = 'inputLabel'; label.htmlFor = control.id;
            container.insertBefore(label, control);
            options.forEach(function (entry) { node('option', entry[1], control).value = entry[0]; });
            control.value = value;
            return control;
        }
        function featureError(error, fallback) { return ui.errorText(error, fallback); }
        async function loadUsers() {
            if (!config || !visible || usersLoading) return;
            var id = ++usersRequest; var expected = generation;
            usersLoading = true; updateFeatureState();
            bindText(get('siphonUsersStatus'), function () { return t('loadingJellyfinUsers'); });
            try {
                var result = await request('Users', 'GET');
                if (id !== usersRequest || expected !== generation || !visible) return;
                users = result;
                renderProfileUsers();
                bindText(get('siphonUsersStatus'), function () { return users.length ? users.length + t('jellyfinUsersChooseOneToEditTheir') : t('noJellyfinUsersReturned'); });
            } catch (error) {
                if (id === usersRequest && expected === generation) bindText(get('siphonUsersStatus'), function () { return featureError(error, t('usersCouldNotBeLoadedReloadUsers')); });
            } finally {
                if (id === usersRequest) { usersLoading = false; updateFeatureState(); }
            }
        }
        function renderProfileUsers() {
            var select = get('siphonProfileUser'); var selected = select.value;
            select.replaceChildren();
            node('option', function () { return t('chooseAUser'); }, select).value = '';
            (users || []).forEach(function (user) { node('option', function () { return user.Name; }, select).value = user.Id; });
            config.UserProfiles.forEach(function (profile) {
                if (!(users || []).some(function (user) { return canonicalId(user.Id) === canonicalId(profile.UserId); }))
                    node('option', function () { return t('unavailableUser') + profile.UserId; }, select).value = profile.UserId;
            });
            select.value = selected;
            renderProfile();
        }
        function renderProfile() {
            var parent = get('siphonProfileEditor'); parent.replaceChildren();
            var userId = get('siphonProfileUser').value;
            if (!config || !userId) return;
            var profile = config.UserProfiles.find(function (entry) { return canonicalId(entry.UserId) === canonicalId(userId); });
            var exists = (users || []).some(function (user) { return canonicalId(user.Id) === canonicalId(userId); });
            if (!exists) {
                node('p', function () { return t('thisSavedProfileNoLongerMatchesA'); }, parent).className = 'siphon-help';
                button(parent, function () { return t('removeUnavailableProfile'); }, function () {
                    if (!window.confirm(t('removeTheSavedPlaybackProfileForThis'))) return;
                    config.UserProfiles = config.UserProfiles.filter(function (entry) { return entry !== profile; });
                    renderProfileUsers(); markDirty();
                });
                return;
            }
            var mode = selectInput(parent, function () { return t('playbackAddonSource'); }, [['inherit', function () { return t('inheritGlobalAddons'); }], ['override', function () { return t('useOnlyThisUserSAddons'); }]], profile && profile.OverrideAddons ? 'override' : 'inherit');
            mode.dataset.transient = '';
            mode.addEventListener('change', function () {
                if (!profile) {
                    if (config.UserProfiles.length >= 256) { message(function () { return t('the256ProfileLimitHasBeenReached'); }, 'error'); renderProfile(); return; }
                    profile = { UserId: userId, OverrideAddons: false, Addons: [] }; config.UserProfiles.push(profile);
                }
                profile.OverrideAddons = mode.value === 'override';
                markDirty(); renderProfile();
            });
            if (profile) button(parent, function () { return t('deleteThisSavedProfile'); }, function () {
                if (!window.confirm(t('deleteThisUserSSavedOverrideAnd'))) return;
                config.UserProfiles = config.UserProfiles.filter(function (entry) { return entry !== profile; });
                markDirty(); renderProfile();
            });
            if (!profile || !profile.OverrideAddons) {
                node('p', function () { return t('thisUserInheritsTheEnabledGlobalPlayback'); }, parent).className = 'siphon-help';
                return;
            }
            if (!profile.Addons.length) node('p', function () { return t('emptyOverrideThisUserHasNoPlayback'); }, parent).className = 'siphon-message';
            profile.Addons.forEach(function (addon, index) {
                var row = node('article', undefined, parent); row.className = 'siphon-addon';
                node('h3', function () { return addon.DisplayName || t('unnamedAddon'); }, row);
                checkbox(row, function () { return t('enabled'); }, addon.Enabled, function (value) { addon.Enabled = value; });
                var label = input(row, function () { return t('displayName'); }, addon.DisplayName);
                label.maxLength = 512; label.addEventListener('input', function () { addon.DisplayName = label.value; });
                var url = input(row, function () { return t('manifestURLMasked'); }, addon.ManifestUrl, 'password');
                url.readOnly = true; url.autocomplete = 'new-password';
                var actions = node('div', undefined, row); actions.className = 'siphon-actions';
                function move(offset) { profile.Addons.splice(index, 1); profile.Addons.splice(index + offset, 0, addon); markDirty(); renderProfile(); }
                button(actions, function () { return t('moveAddonUp'); }, function () { move(-1); }, 'arrow_upward').disabled = index === 0;
                button(actions, function () { return t('moveAddonDown'); }, function () { move(1); }, 'arrow_downward').disabled = index === profile.Addons.length - 1;
                button(actions, function () { return t('removeAddon'); }, function () {
                    if (!window.confirm(t('removeThisAddonFromThisUserS'))) return;
                    profile.Addons.splice(index, 1); markDirty(); renderProfile();
                });
            });
            var add = node('section', undefined, parent); add.className = 'siphon-add';
            node('h3', function () { return t('addAPlaybackOrSubtitleProvider'); }, add);
            var manifestUrl = input(add, function () { return t('manifestURL'); }, '', 'password');
            manifestUrl.autocomplete = 'new-password'; manifestUrl.spellcheck = false; manifestUrl.maxLength = 16384; manifestUrl.dataset.transient = '';
            button(add, function () { return t('validateAddToThisUser'); }, function () {
                work(async function () {
                    var url = manifestUrl.value.trim();
                    if (!url) { manifestUrl.focus(); return; }
                    if (profile.Addons.length >= 16) { message(function () { return t('theProfileHasReachedThe16Addon'); }, 'error'); return; }
                    if (config.UserProfiles.reduce(function (total, entry) { return total + entry.Addons.length; }, 0) >= 256) { message(function () { return t('theCombined256ProfileAddonLimitHas'); }, 'error'); return; }
                    if (profile.Addons.some(function (entry) { return manifestKey(entry.ManifestUrl) === manifestKey(url); })) { message(function () { return t('thisAddonURLIsAlreadyInThe'); }); return; }
                    var expected = generation;
                    try {
                        var manifest = await request('Siphon/Addons/Validate', 'POST', { ManifestUrl: url });
                        if (expected !== generation || !config.UserProfiles.includes(profile)) return;
                        if (!(manifest.Resources || []).some(function (resource) { return resource.Name === 'stream' || resource.Name === 'subtitles'; })) {
                            message(function () { return t('thisManifestDoesNotDeclareStreamsOr'); }, 'error'); return;
                        }
                        profile.Addons.push({ Id: uuid(), ManifestUrl: url, DisplayName: manifest.Name, Enabled: true, Catalogs: [] });
                        markDirty(); renderProfile(); message(function () { return t('playbackAddonAddedToThisDraftProfile'); });
                    } catch (error) { message(function () { return featureError(error, t('addonValidationFailedCheckTheManifestURL')); }, 'error'); }
                });
            });
            node('p', function () { return t('toUseDifferentCredentialsRemoveThatProfile'); }, add).className = 'siphon-help';
        }
        function bytes(value) {
            return Number.isFinite(value) && value >= 0 ? (value / 1048576).toLocaleString(ui.locale(), { maximumFractionDigits: 1 }) + t('mib') : t('notReported');
        }
        async function refreshP2p() {
            if (!config || !visible || p2pLoading) return;
            var expected = generation;
            p2pLoading = true; updateFeatureState();
            bindText(get('siphonP2pStatus'), function () { return t('loadingSavedP2PEngineStatus'); });
            try {
                var result = await request('Siphon/P2P/Status', 'GET');
                if (expected !== generation || !visible) return;
                p2pKnown = true;
                var facts = get('siphonP2pFacts'); facts.replaceChildren();
                fact(facts, function () { return t('engine'); }, function () { return result.Enabled ? t('enabled') : t('disabled'); });
                fact(facts, function () { return t('activeReaders'); }, function () { return result.ActiveStreams + ' / ' + result.MaximumStreams; });
                fact(facts, function () { return t('cacheOnDisk'); }, function () { return result.CacheMeasuredAtUtc ? bytes(result.CacheBytes) : t('cacheNotMeasured'); });
                fact(facts, function () { return t('cacheMeasuredAt'); }, function () { return formattedDate(result.CacheMeasuredAtUtc); });
                fact(facts, function () { return t('reservedLimit'); }, function () { return bytes(result.ReservedBytes) + ' / ' + bytes(result.MaximumCacheBytes); });
                fact(facts, function () { return t('downloadUploadLimits'); }, function () { return Math.round(result.DownloadLimitBytesPerSecond / 1024) + ' / ' + Math.round(result.UploadLimitBytesPerSecond / 1024) + t('kibS'); });
                fact(facts, function () { return t('trackerlessDHT'); }, function () { return result.DhtEnabled ? t('enabled') : t('disabled'); });
                (result.Sessions || []).forEach(function (session, index) {
                    fact(facts, function () { return t('session') + (index + 1); }, function () { return p2pState(session.State) + t('reserved') + bytes(session.ReservedBytes) + t('down') + Math.round(session.DownloadBytesPerSecond / 1024) + t('kibSUp') + Math.round(session.UploadBytesPerSecond / 1024) + t('kibS'); });
                });
                bindText(get('siphonP2pStatus'), function () { return (result.Sessions || []).length ? t('currentEngineSnapshotRefreshForUpdatedTransfer') : t('noP2PSessionsNoHashesTrackersOr'); });
            } catch (error) {
                if (expected === generation) {
                    p2pKnown = false; get('siphonP2pFacts').replaceChildren();
                    bindText(get('siphonP2pStatus'), function () { return featureError(error, t('p2pStatusUnavailableRefreshToRetry')); });
                }
            } finally { p2pLoading = false; updateFeatureState(); }
        }
        async function cleanupP2p() {
            if (dirty || !p2pKnown) return;
            if (!window.confirm(t('removeInactiveSiphonP2PScratchCacheDirectories'))) return;
            var expected = generation;
            bindText(get('siphonP2pStatus'), function () { return t('cleaningInactiveP2PCache'); });
            try {
                var result = await request('Siphon/P2P/Cleanup', 'POST');
                if (expected !== generation || !visible) return;
                await refreshP2p();
                if (expected === generation && visible) bindText(get('siphonP2pStatus'), function () { return count(result.RemovedDirectories) + t('inactiveDirectoriesRemoved') + bytes(result.RemovedBytes) + t('freed') + count(result.SkippedActive) + t('activeDirectoriesPreserved'); });
            } catch (error) {
                if (expected === generation) bindText(get('siphonP2pStatus'), function () { return featureError(error, t('cleanupCouldNotBeConfirmedRefreshStatus')); });
            }
        }
        function renderIntroTypes() {
            var parent = get('siphonIntroTypes'); parent.replaceChildren();
            [['intro', function () { return t('openingIntroNativeSkipSegment'); }], ['recap', function () { return t('recapNativeSkipSegment'); }], ['outro', function () { return t('endCreditsSkipOnlyOutsidePostCredit'); }], ['post-credits', function () { return t('postCreditSceneNavigableChapterNeverSkip'); }]].forEach(function (entry) {
                var control = checkbox(parent, entry[1], config.IntroDbSegments.includes(entry[0]), function () {});
                control.dataset.introType = entry[0];
            });
        }
        function itemIdFromInput(value) {
            var text = value.trim();
            if (/^[0-9a-f]{32}$/i.test(text) || /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(text)) return text;
            var match = /[?&]id=([0-9a-f-]{32,36})(?:[&#]|$)/i.exec(text);
            return match && /^[0-9a-f]{32}$/i.test(canonicalId(match[1])) ? match[1] : null;
        }
        async function inspectItem(kind) {
            if (!config || inspectionLoading || (kind !== 'Inspection' && dirty)) return;
            var itemId = itemIdFromInput(get('siphonInspectItem').value);
            if (!itemId) { bindText(get('siphonInspectionStatus'), function () { return t('enterAValidJellyfinItemIDOr'); }); get('siphonInspectItem').focus(); return; }
            var id = ++inspectionRequest; var expected = generation;
            inspectionLoading = true; updateFeatureState(); get('siphonInspection').replaceChildren();
            get('siphonInspection').setAttribute('aria-busy', 'true');
            bindText(get('siphonInspectionStatus'), function () { return kind === 'Inspection' ? t('readingPersistedProvenanceAndNativeLocks') : t('resolvingSourcesForYourCurrentProfile'); });
            try {
                var result = await request('Siphon/Items/' + encodeURIComponent(itemId) + '/' + kind, kind === 'Sources/Refresh' ? 'POST' : 'GET');
                if (id !== inspectionRequest || expected !== generation || !visible) return;
                if (kind === 'Inspection') renderInspection(result); else renderSources(result, get('siphonInspection'));
                bindText(get('siphonInspectionStatus'), function () { return kind === 'Inspection' ? t('readOnlySnapshot') + formattedDate(result.GeneratedAtUtc) : t('currentUserSourceResultsSourceRefreshNever'); });
            } catch (error) {
                if (id === inspectionRequest && expected === generation) bindText(get('siphonInspectionStatus'), function () { return featureError(error, t('itemDiagnosticsFailedCheckThatTheItem')); });
            } finally {
                if (id === inspectionRequest) { inspectionLoading = false; get('siphonInspection').setAttribute('aria-busy', 'false'); updateFeatureState(); }
            }
        }
        function renderInspection(result) {
            var parent = get('siphonInspection');
            node('h3', function () { return result.Title || t('managedItem'); }, parent);
            node('p', function () { return mediaType(result.ItemType) + ' · ' + (result.IsLocked ? t('allMetadataLocked') : t('nativeFieldLocks') + ((result.LockedFields || []).map(function (field) { return metadataFields[field] || t('unknownReason'); }).join(', ') || t('none'))); }, parent).className = 'siphon-help';
            var origins = { get SelectedAddon() { return t('selectedMetadataAddon'); }, get Addon() { return t('catalogAddonMetadata'); }, Tmdb: 'TMDB', Tvdb: 'TheTVDB', Fanart: 'Fanart', MdbList: 'MDBList', get Mixed() { return t('multipleObservedSources'); }, get ManualOrNative() { return t('nativeOrManuallyChangedEditorUnknown'); }, get Unknown() { return t('originNotRecorded'); } };
            var preservation = { get ItemLocked() { return t('allMetadataLockedInJellyfin'); }, get FieldLocked() { return t('thisFieldIsLockedInJellyfin'); }, get NativeArtworkChangedOrLocalized() { return t('nativeArtworkDiffersOrWasLocalizedRetained'); }, get NativeValueChangedSincePublication() { return t('nativeValueChangedSinceSiphonPublishedIt'); }, get NoNativePublicationEvidence() { return t('noRecordedEvidenceOfSiphonPublishingThis'); }, get MetadataUpdatePolicy() { return t('retainedUnderTheConfiguredMetadataUpdatePolicy'); }, get NativeVersionRuntimePreserved() { return t('nativeStreamVersionRuntimeRetained'); }, get IncomingValueMissing() { return t('incomingValueWasMissingPreviousValueRetained'); }, get MergedWithRetainedValues() { return t('observedSourceMergedWithRetainedValues'); }, get NativeLockOrPreservationPolicy() { return t('nativeLockOrPreservationPolicyPreventedReplacement'); } };
            (result.Fields || []).forEach(function (field) {
                var row = node('section', undefined, parent); row.className = 'siphon-diagnostic';
                node('h3', function () { return metadataFields[field.Field] || t('unknownReason'); }, row);
                var facts = node('dl', undefined, row); facts.className = 'siphon-facts';
                fact(facts, function () { return t('value'); }, function () { return field.HasValue ? t('present') : t('missing'); });
                fact(facts, function () { return t('origin'); }, function () { return origins[field.Origin] || t('originNotRecorded'); });
                fact(facts, function () { return t('observedSource'); }, function () { return (field.SourceLabels || []).join(' · ') || t('notRecorded'); });
                fact(facts, function () { return t('observed'); }, function () { return formattedDate(field.ObservedAtUtc); });
                fact(facts, function () { return t('cacheCreatedExpiry'); }, function () { return formattedDate(field.CacheCreatedAtUtc) + ' / ' + formattedDate(field.CacheExpiresAtUtc); });
                fact(facts, function () { return t('cacheAge'); }, function () { return Number.isFinite(field.CacheAgeSeconds) ? Math.round(field.CacheAgeSeconds).toLocaleString(ui.locale()) + t('seconds') : t('notRecorded'); });
                fact(facts, function () { return t('nativeLock'); }, function () { return field.IsLocked ? t('locked') : t('notLocked'); });
                fact(facts, function () { return t('preservation'); }, function () { return preservation[field.PreservationReason] || t('noPreservationReasonRecorded'); });
            });
            if (!(result.Fields || []).length) node('p', function () { return t('noFieldLevelProvenanceHasBeenRecorded'); }, parent);
        }
        function renderSources(result, parent) {
            var facts = node('dl', undefined, parent); facts.className = 'siphon-facts';
            fact(facts, function () { return t('resolved'); }, function () { return formattedDate(result.CreatedAt); });
            fact(facts, function () { return t('cacheExpiry'); }, function () { return formattedDate(result.ExpiresAt); });
            fact(facts, function () { return t('cacheAge'); }, function () { return Number.isFinite(Date.parse(result.CreatedAt)) ? Math.max(0, Math.floor((Date.now() - Date.parse(result.CreatedAt)) / 1000)) + t('seconds') : t('notRecorded'); });
            node('h3', function () { return t('orderedNativeVersions'); }, parent);
            node('p', function () { return t('addonPriorityAndEachAddonSStream'); }, parent).className = 'siphon-help';
            var sources = node('ol', undefined, parent); sources.className = 'siphon-cleanup-list';
            (result.Sources || []).forEach(function (source) {
                var row = node('li', undefined, sources);
                node('strong', function () { return source.Name || t('streamVersion'); }, row);
                node('p', function () { return (source.Kind === 'P2p' ? t('p2pServerUploadsAndExposesItsIP') : t('httpSource')) + (source.Size == null ? '' : ' · ' + bytes(source.Size)); }, row).className = 'siphon-help';
                if (source.CanDownload === false && source.DownloadUnavailableReason) node('p', function () { return t('downloadUnavailable'); }, row).className = 'siphon-help';
            });
            if (!(result.Sources || []).length) node('p', function () { return t('noAcceptedSourcesForYourCurrentPlayback'); }, parent);
            node('h3', function () { return t('addonOutcomes'); }, parent);
            var addons = node('ul', undefined, parent); addons.className = 'siphon-cleanup-list';
            var outcomes = { get Available() { return t('sourcesAvailable'); }, get Empty() { return t('noStreamsReturned'); }, get Unsupported() { return t('resourceOrItemUnsupported'); }, get Unavailable() { return t('addonUnavailable'); } };
            (result.Addons || []).forEach(function (addon) {
                var row = node('li', undefined, addons);
                node('strong', function () { return addon.Name || t('addon'); }, row);
                node('p', function () { return (outcomes[addon.Outcome] || t('outcomeUnavailable')) + ' · ' + count(addon.ElapsedMilliseconds) + t('ms') + count(addon.Accepted) + t('accepted'); }, row);
                Object.keys(addon.Rejected || {}).forEach(function (reason) { node('p', function () { return rejectionReason(reason) + ': ' + count(addon.Rejected[reason]); }, row).className = 'siphon-help'; });
            });
            if (!(result.Addons || []).length) node('li', function () { return t('noEnabledPlaybackAddonsWereUsed'); }, addons);
            node('p', function () { return t('openThePersonalPortalForAPermission'); }, parent).className = 'siphon-help';
        }
        function invalidateRecovery(text) {
            recoveryPreview = null; recoverySelection.clear();
            get('siphonRecoveryNext').hidden = true;
            get('siphonRecoveryItems').replaceChildren();
            if (text) bindText(get('siphonRecoveryStatus'), function () { return text; });
        }
        async function previewRecovery(next) {
            if (dirty || running() || !tasksKnown) return;
            var offset = next && recoveryPreview ? recoveryOffset + recoveryPreview.Limit : 0;
            var expected = generation;
            invalidateRecovery(function () { return t('readingOrphanedWatchStateNoChangesAre'); });
            try {
                if (!users && !usersLoading) await loadUsers();
                if (expected !== generation || !visible) return;
                var result = await request('Siphon/Recovery/Preview', 'POST', { StartIndex: offset, Limit: 50 });
                if (expected !== generation || !visible) return;
                recoveryPreview = result; recoveryOffset = offset;
                var actions = { get RestoreHistory() { return t('restoreHistoryToAnExistingManagedItem'); }, get RestoreContent() { return t('restoreMissingManagedTitleAndHistory'); }, get None() { return t('notRecoverable'); } };
                var reasons = { get Ready() { return t('readyForExplicitRecovery'); }, get AmbiguousOwnership() { return t('ownershipIsAmbiguousCannotSafelyRestore'); }, get AmbiguousTarget() { return t('moreThanOnePossibleTargetCannotSafely'); }, get LiveHistoryPresent() { return t('meaningfulLiveHistoryExistsAndWillNot'); }, get EmptyHistory() { return t('noMeaningfulHistoryToRecover'); }, get SelectMetadataAddon() { return t('chooseAndSaveAnAuthoritativeMetadataAddon'); }, get OwnershipUnknown() { return t('siphonOwnershipCannotBeEstablished'); } };
                result.Items.forEach(function (entry) {
                    var row = node('li', undefined, get('siphonRecoveryItems'));
                    var label = node('label', undefined, row); label.className = 'emby-checkbox-label';
                    var control = node('input', undefined, label); control.type = 'checkbox'; control.setAttribute('is', 'emby-checkbox');
                    control.disabled = !entry.CanRestore || entry.Ownership !== 'ProvenSiphon';
                    control.addEventListener('change', function () { if (control.checked) recoverySelection.add(entry.Id); else recoverySelection.delete(entry.Id); updateFeatureState(); });
                    node('span', function () { return entry.Name || t('unattributedRecord'); }, label);
                    node('p', function () { return mediaType(entry.Type) + ' · ' + (entry.Ownership === 'ProvenSiphon' ? t('provenSiphonOwnership') : t('unattributedCannotBeSelected')); }, row).className = 'siphon-help';
                    node('p', function () { return actions[entry.Action] || t('notRecoverable'); }, row);
                    node('p', function () { return reasons[entry.Reason] || t('noReasonRecorded'); }, row).className = 'siphon-help';
                    var user = (users || []).find(function (candidate) { return canonicalId(candidate.Id) === canonicalId(entry.UserId); });
                    node('p', function () { return t('user') + (user ? user.Name : entry.UserId) + ' · ' + (entry.Played ? t('watched') : t('notMarkedWatched')) + ' · ' + (entry.IsFavorite ? t('favorite') : t('notFavorite')) + t('resume') + Math.floor((entry.PlaybackPositionTicks || 0) / 600000000) + t('minPlays') + count(entry.PlayCount) + t('lastPlayed') + formattedDate(entry.LastPlayedDate); }, row).className = 'siphon-help';
                });
                get('siphonRecoveryNext').hidden = !result.HasMore;
                bindText(get('siphonRecoveryStatus'), function () { return result.Items.length ? t('showing') + result.Items.length + t('candidatesFromRow') + (offset + 1) + t('selectOnlyTheRowsYouWantTo') + formattedDate(result.ExpiresAtUtc) + t('movingToAnotherPageClearsSelections') : t('noOrphanedCandidatesInThisPreview'); });
            } catch (error) {
                if (expected === generation) invalidateRecovery(function () { return featureError(error, t('recoveryPreviewFailedNoChangesWereMade')); });
            }
            updateFeatureState();
        }
        async function executeRecovery() {
            if (!recoveryPreview || !recoverySelection.size || dirty || running() || !tasksKnown) return;
            if (Date.parse(recoveryPreview.ExpiresAtUtc) <= Date.now()) { invalidateRecovery(function () { return t('previewExpiredGenerateANewPreview'); }); return; }
            if (!window.confirm(t('recoverExactly') + recoverySelection.size + t('selectedUserStateRecordsMissingTitlesMay'))) return;
            var preview = recoveryPreview; var ids = Array.from(recoverySelection); var expected = generation;
            invalidateRecovery(function () { return t('applyingSelectedRepairs'); });
            try {
                var result = await request('Siphon/Recovery/Execute', 'POST', { PreviewToken: preview.PreviewToken, Ids: ids, Confirmed: true });
                if (expected !== generation || !visible) return;
                var statuses = { get Restored() { return t('restored'); }, get Changed() { return t('stateChangedSkipped'); }, get Collision() { return t('liveStateOrIdentityConflictPreserved'); }, get MetadataUnavailable() { return t('authoritativeMetadataUnavailableOriginalStateRetained'); }, get Failed() { return t('failedReviewAFreshPreview'); } };
                result.Items.forEach(function (entry) {
                    var original = preview.Items.find(function (item) { return item.Id === entry.Id; });
                    node('li', function () { return (original && original.Name || t('record')) + ' · ' + (statuses[entry.Status] || t('outcomeUnavailable')); }, get('siphonRecoveryItems'));
                });
                bindText(get('siphonRecoveryStatus'), function () { return t('selectedRecoveryFinishedReviewEachResultGenerate'); });
                refreshStatus();
            } catch (error) {
                if (expected === generation) bindText(get('siphonRecoveryStatus'), function () { return featureError(error, t('recoveryWasNotConfirmedSomeRepairsMay')); });
            }
        }
        function mergeCatalogs(addon, manifest) {
            manifest.Catalogs.forEach(function (catalog) {
                if (!addon.Catalogs.some(function (saved) { return saved.Type === catalog.Type && saved.Id === catalog.Id; })) {
                    var candidate = { Key: uuid(), Type: catalog.Type, Id: catalog.Id, Enabled: false, MaxItems: null, Extras: [], Presentation: 'Library' };
                    discoveredKeys.add(candidate.Key);
                    addon.Catalogs.push(candidate);
                }
            });
        }
        function snapshot() {
            var result = JSON.parse(JSON.stringify(config));
            result.Addons.forEach(function (addon) {
                addon.Catalogs = addon.Catalogs.filter(function (catalog) {
                    return !discoveredKeys.has(catalog.Key) || catalog.Enabled || catalog.MaxItems !== null || catalog.Extras.length > 0 || catalog.Presentation !== 'Library';
                });
            });
            return result;
        }
        function manifestKey(url) { return url.replace(/^stremio:\/\//i, 'https://'); }
        function isConfigured(url) { return config.Addons.some(function (addon) { return manifestKey(addon.ManifestUrl) === manifestKey(url); }); }
        function supportsCatalog(catalog) { return catalog.Type === 'movie' || catalog.Type === 'series' || catalog.Type === 'anime'; }
        function showPrivateHosts() {
            selectPanel('siphonPanelConnection');
            get('siphonAllowedPrivateHosts').focus();
        }
        function applyFilters() {
            var query = get('siphonCatalogSearch').value.trim().toLocaleLowerCase();
            var selectedOnly = get('siphonCatalogFilter').value === 'selected';
            page.querySelectorAll('.siphon-catalog-group').forEach(function (addonElement) {
                var rows = Array.from(addonElement.querySelectorAll('.siphon-catalog'));
                var shown = 0;
                rows.forEach(function (row) {
                    row.hidden = (selectedOnly && row.dataset.selected !== 'true') || (query && !row.dataset.search.includes(query));
                    if (!row.hidden) shown++;
                });
                var empty = addonElement.querySelector('.siphon-filter-empty');
                empty.hidden = rows.length === 0 || shown > 0;
                bindText(empty, function () { return query ? t('noCatalogsMatchYourSearch') : t('noSelectedCatalogsHereChooseAllAvailable'); });
                var matches = addonElement.querySelector('.siphon-catalog-matches');
                matches.hidden = !query;
                bindText(matches, function () { return shown + t('matchingCatalogs'); });
            });
        }
        function renderCatalog(group, addon, catalog, manifest) {
            var definition = manifest && manifest.Catalogs.find(function (entry) { return entry.Type === catalog.Type && entry.Id === catalog.Id; });
            var title = definition ? definition.Name : catalog.Id;
            var row = node('div', undefined, group);
            row.className = 'siphon-catalog';
            row.dataset.selected = String(catalog.Enabled);
            row.dataset.search = [title, catalog.Id, catalog.Type, addon.DisplayName].join(' ').toLocaleLowerCase();
            var heading = node('div', undefined, row);
            heading.className = 'siphon-catalog-title';
            var controls = [];
            var selected = checkbox(heading, title, catalog.Enabled, function (checked) {
                catalog.Enabled = checked;
                row.dataset.selected = String(checked);
                controls.forEach(function (control) { control.disabled = !addon.Enabled || !checked; });
                updateAddonSummary(addon);
            });
            selected.disabled = !addon.Enabled || (!supportsCatalog(catalog) && !catalog.Enabled);
            var type = node('span', function () { return catalog.Type === 'movie' ? t('movies') : catalog.Type === 'series' ? t('series') : catalog.Type === 'anime' ? t('anime') : catalog.Type; }, heading);
            type.className = 'siphon-catalog-type';
            if (!supportsCatalog(catalog)) {
                node('p', function () { return t('thisMediaTypeIsNotImportedSiphon'); }, row).className = 'siphon-help';
                return;
            }
            var sync = button(row, function () { return t('syncThisCatalog'); }, function () {
                var target = catalogTarget(addon, catalog);
                if (target) startTarget({ CatalogKey: target.Key });
            });
            sync.dataset.catalogSync = '';
            sync.dataset.installationId = addon.Id;
            sync.dataset.catalogId = catalog.Key;
            bindText(sync, function () { return  t('syncCatalog') + title; }, 'aria-label');
            sync.disabled = true;
            if (manifest && !definition) node('p', function () { return t('noLongerAdvertisedByThisAddonYour'); }, row).className = 'siphon-help';
            var details = node('details', undefined, row);
            details.className = 'siphon-catalog-options';
            node('summary', function () { return t('presentationLimitsFilters'); }, details);
            var fields = node('div', undefined, details);
            fields.className = 'siphon-grid';
            var presentation = selectInput(fields, function () { return t('presentation'); }, [['Library', function () { return t('library'); }], ['Collection', function () { return t('collection'); }], ['Both', function () { return t('libraryAndCollection'); }]], catalog.Presentation || 'Library');
            presentation.disabled = !addon.Enabled || !catalog.Enabled;
            controls.push(presentation);
            presentation.addEventListener('change', function () { catalog.Presentation = presentation.value; markDirty(); });
            var limit = input(fields, function () { return t('itemLimitOverride'); }, catalog.MaxItems, 'number');
            bindText(limit, function () { return t('useTheDefault'); }, 'placeholder');
            limit.min = '1'; limit.max = '10000'; limit.step = '1';
            limit.disabled = !addon.Enabled || !catalog.Enabled;
            controls.push(limit);
            limit.addEventListener('input', function () { catalog.MaxItems = limit.value === '' ? null : Number(limit.value); markDirty(); });
            var extras = definition ? definition.Extras.filter(function (extra) { return extra.Name !== 'skip'; }) : [];
            catalog.Extras.forEach(function (saved) {
                if (saved.Name !== 'skip' && !extras.some(function (entry) { return entry.Name === saved.Name; })) extras.push({ Name: saved.Name, IsRequired: false, Options: [] });
            });
            extras.forEach(function (extra, index) {
                var saved = catalog.Extras.find(function (entry) { return entry.Name === extra.Name; });
                var required = extra.IsRequired && !extra.Default;
                function filterLabel() { return t('catalogFilter') + (extras.length > 1 ? ' ' + (index + 1) : ''); }
                var control = input(fields, function () { return filterLabel() + (required ? t('required') : ''); }, saved ? saved.Value : (extra.Default || ''));
                var help = node('p', function () { return t('availableValuesAreDefinedByTheAddon'); }, control.parentElement);
                help.className = 'siphon-help';
                help.id = control.id + 'Help';
                control.setAttribute('aria-describedby', help.id);
                control.required = required;
                control.maxLength = 16384;
                control.disabled = !addon.Enabled || !catalog.Enabled;
                control.autocomplete = 'off';
                controls.push(control);
                if (extra.Options && extra.Options.length) {
                    var choices = node('datalist', undefined, fields);
                    choices.id = control.id + 'Options';
                    extra.Options.forEach(function (option) { node('option', undefined, choices).value = option; });
                    control.setAttribute('list', choices.id);
                }
                control.addEventListener('input', function () {
                    catalog.Extras = catalog.Extras.filter(function (entry) { return entry.Name !== extra.Name; });
                    if (control.value !== '') catalog.Extras.push({ Name: extra.Name, Value: control.value });
                    markDirty();
                });
            });
        }
        function updateAddonSummary(addon) {
            var elements = [get('siphonAddon' + addon.Id), get('siphonCatalogGroup' + addon.Id)];
            var state = manifestStates.get(addon.Id);
            var selected = addon.Catalogs.filter(function (catalog) { return catalog.Enabled; }).length;
            var count = addon.Catalogs.filter(supportsCatalog).length;
            function summaryText() {
            var status = selected + t('selected') + count + t('supportedCatalogs');
            if (state === 'loading') status += t('checkingDefinitions');
            if (state === 'failed') status += t('definitionUnavailableSavedSettingsRetained');
            if (!addon.Enabled) status = t('disabledSelectionsRetained') + status;
                return status;
            }
            elements.forEach(function (element) { if (element) bindText(element.querySelector('.siphon-addon-summary'), function () { return summaryText(); }); });
        }
        function renderDiscovery(section, addon) {
            var discovery = discoveries.get(addon.Id);
            if (!discovery) return;
            var group = node('div', undefined, section);
            group.className = 'siphon-discovery';
            node('h3', function () { return t('discoverAddons'); }, group);
            if (!discovery.Catalogs.length) { node('p', function () { return t('thisAddonDoesNotAdvertiseADiscovery'); }, group); return; }
            var actions = node('div', undefined, group);
            actions.className = 'siphon-actions';
            discovery.Catalogs.forEach(function (catalog) {
                button(actions, function () { return catalog.Name + ' (' + catalog.Type + ')'; }, function () {
                    work(async function () {
                        var result = await request('Siphon/Addons/Discover', 'POST', { ManifestUrl: addon.ManifestUrl, Type: catalog.Type, Id: catalog.Id });
                        discoveries.set(addon.Id, { Catalogs: result.Catalogs, Addons: result.Addons, Selected: catalog, Visible: 25 });
                        renderAddon(addon);
                        message(function () { return t('chooseADiscoveredAddonToValidateIt'); });
                    });
                });
            });
            if (!discovery.Selected) return;
            node('p', function () { return discovery.Addons.length + t('addonsIn') + discovery.Selected.Name; }, group);
            discovery.Addons.slice(0, discovery.Visible).forEach(function (entry) {
                var item = node('div', undefined, group);
                item.className = 'siphon-discovery-result';
                node('strong', function () { return entry.Name; }, item);
                if (entry.Description) node('p', function () { return entry.Description; }, item).className = 'siphon-help';
                var installed = isConfigured(entry.TransportUrl);
                button(item, function () { return installed ? t('alreadyAdded') : t('addAddon'); }, function () { work(function () { return installAddon(entry.TransportUrl); }); }).disabled = installed;
            });
            if (discovery.Addons.length > discovery.Visible) button(group, function () { return t('showMoreAddons'); }, function () { discovery.Visible += 25; renderAddon(addon); });
        }
        function catalogVisibilityState() {
            get('siphonHideCatalogs').disabled = !page.querySelector('.siphon-catalog-group[open]');
            get('siphonCatalogsEmpty').hidden = config.Addons.some(function (addon) { return addon.Catalogs.length > 0; });
        }
        function showCatalogs(addon) {
            var group = get('siphonCatalogGroup' + addon.Id);
            if (!group) return;
            selectPanel('siphonPanelCatalogs');
            openCatalogGroups.add(addon.Id);
            group.open = true;
            group.querySelector('summary').focus();
            group.scrollIntoView({ block: 'nearest' });
        }
        function renderCatalogGroup(addon) {
            var previous = get('siphonCatalogGroup' + addon.Id);
            if (!addon.Catalogs.length) {
                if (previous) previous.remove();
                catalogVisibilityState();
                return;
            }
            var section = node('details');
            section.id = 'siphonCatalogGroup' + addon.Id;
            section.className = 'siphon-addon siphon-catalog-group';
            section.open = openCatalogGroups.has(addon.Id);
            var summary = node('summary', undefined, section);
            node('span', function () { return addon.DisplayName || t('unnamedAddon'); }, summary).className = 'siphon-catalog-group-name';
            node('span', function () { return ''; }, summary).className = 'siphon-addon-summary siphon-muted';
            node('span', function () { return ''; }, summary).className = 'siphon-catalog-matches siphon-muted';
            var label = node('span', function () { return section.open ? t('hideCatalogs') : t('showCatalogs'); }, summary);
            label.className = 'siphon-catalog-toggle-label';
            section.addEventListener('toggle', function () {
                if (!section.isConnected) return;
                if (section.open) openCatalogGroups.add(addon.Id); else openCatalogGroups.delete(addon.Id);
                bindText(label, function () { return section.open ? t('hideCatalogs') : t('showCatalogs'); });
                catalogVisibilityState();
            });
            var group = node('div', undefined, section);
            group.className = 'siphon-catalogs';
            var manifest = manifests.get(addon.Id);
            addon.Catalogs.forEach(function (catalog) { renderCatalog(group, addon, catalog, manifest); });
            node('p', function () { return ''; }, section).className = 'siphon-filter-empty siphon-help';
            if (previous) previous.replaceWith(section); else get('siphonCatalogs').appendChild(section);
            catalogVisibilityState();
        }
        function renderAddon(addon) {
            var previous = get('siphonAddon' + addon.Id);
            var toolsOpen = previous && previous.querySelector('.siphon-addon-tools').open;
            var section = node('article');
            section.id = 'siphonAddon' + addon.Id;
            section.className = 'siphon-addon';
            var header = node('div', undefined, section);
            header.className = 'siphon-addon-header';
            var title = node('div', undefined, header);
            title.className = 'siphon-addon-title';
            node('h3', function () { return addon.DisplayName || t('unnamedAddon'); }, title);
            node('p', function () { return ''; }, title).className = 'siphon-addon-summary siphon-muted';
            var actions = node('div', undefined, header);
            actions.className = 'siphon-actions';
            checkbox(actions, function () { return t('enabled'); }, addon.Enabled, function (checked) { addon.Enabled = checked; renderAddon(addon); });
            var index = config.Addons.indexOf(addon);
            function move(offset) {
                config.Addons.splice(index, 1);
                config.Addons.splice(index + offset, 0, addon);
                renderAddons();
                markDirty();
            }
            button(actions, function () { return t('move') + (addon.DisplayName || t('addon2')) + t('up'); }, function () { move(-1); }, 'arrow_upward').disabled = index === 0;
            button(actions, function () { return t('move') + (addon.DisplayName || t('addon2')) + t('down2'); }, function () { move(1); }, 'arrow_downward').disabled = index === config.Addons.length - 1;
            var tools = node('details', undefined, section);
            tools.className = 'siphon-addon-tools';
            tools.open = Boolean(toolsOpen);
            node('summary', function () { return t('addonDetailsActions'); }, tools);
            var manifest = manifests.get(addon.Id);
            if (manifest && manifest.Description) node('p', function () { return manifest.Description; }, tools).className = 'siphon-help';
            if (manifest) renderCapabilities(section, tools, manifest);
            var url = input(tools, function () { return t('manifestURLMasked'); }, addon.ManifestUrl, 'password');
            url.readOnly = true; url.autocomplete = 'new-password'; url.spellcheck = false;
            var toolsActions = node('div', undefined, tools);
            toolsActions.className = 'siphon-actions';
            button(toolsActions, function () { return t('refreshDefinition'); }, function () { work(function () { return refreshManifest(addon, generation); }); });
            button(toolsActions, function () { return t('discoverAddons'); }, function () {
                work(async function () {
                    var result = await request('Siphon/Addons/Discover', 'POST', { ManifestUrl: addon.ManifestUrl });
                    discoveries.set(addon.Id, { Catalogs: result.Catalogs, Addons: [], Selected: null, Visible: 25 });
                    renderAddon(addon);
                });
            });
            button(toolsActions, function () { return t('removeAddon'); }, function () {
                if (!window.confirm(t('removeThisAddonFromTheConfigurationSave'))) return;
                config.Addons = config.Addons.filter(function (entry) { return entry !== addon; });
                manifests.delete(addon.Id); manifestStates.delete(addon.Id); discoveries.delete(addon.Id);
                renderAddons(); markDirty();
                message(function () { return t('addonRemovedFromThisDraftSaveChanges'); });
            });
            renderDiscovery(tools, addon);
            var footer = node('div', undefined, section);
            footer.className = 'siphon-addon-footer';
            footer.appendChild(tools);
            if (addon.Catalogs.length) button(footer, function () { return t('manageCatalogs'); }, function () { showCatalogs(addon); });
            if (previous) previous.replaceWith(section); else get('siphonAddons').appendChild(section);
            renderCatalogGroup(addon);
            updateAddonSummary(addon);
            renderMetadataAddons();
            applyFilters();
            updateFeatureState();
        }
        function renderAddons() {
            get('siphonAddons').replaceChildren();
            get('siphonCatalogs').replaceChildren();
            if (!config.Addons.length) {
                var empty = node('div', undefined, get('siphonAddons'));
                empty.className = 'siphon-empty';
                node('h3', function () { return t('noAddonsConfigured'); }, empty);
                node('p', function () { return t('addAConfiguredManifestBelowToUse'); }, empty).className = 'siphon-help';
            }
            config.Addons.forEach(renderAddon);
            renderMetadataAddons();
            catalogVisibilityState();
            updateState();
        }
        async function refreshManifest(addon, expectedGeneration) {
            manifestStates.set(addon.Id, 'loading');
            updateAddonSummary(addon);
            renderMetadataAddons();
            try {
                var manifest = await request('Siphon/Addons/Validate', 'POST', { ManifestUrl: addon.ManifestUrl });
                if (expectedGeneration !== generation || !config.Addons.includes(addon)) return;
                manifests.set(addon.Id, manifest);
                mergeCatalogs(addon, manifest);
                manifestStates.set(addon.Id, 'ready');
                renderMetadataAddons();
                var element = [get('siphonAddon' + addon.Id), get('siphonCatalogGroup' + addon.Id)].find(function (candidate) {
                    return candidate && candidate.contains(document.activeElement);
                });
                if (element) {
                    updateAddonSummary(addon);
                    element.addEventListener('focusout', function settled() {
                        setTimeout(function () {
                            if (expectedGeneration !== generation || !config.Addons.includes(addon)) {
                                element.removeEventListener('focusout', settled);
                            } else if (!element.contains(document.activeElement)) {
                                element.removeEventListener('focusout', settled);
                                renderAddon(addon);
                            }
                        }, 0);
                    });
                } else renderAddon(addon);
            } catch (error) {
                if (expectedGeneration !== generation) return;
                manifestStates.set(addon.Id, 'failed');
                message(function () { return featureError(error, t('theAddonCouldNotBeValidatedCheck')); }, 'error');
                updateAddonSummary(addon);
                renderMetadataAddons();
            }
        }
        async function installAddon(url) {
            if (isConfigured(url)) { message(function () { return t('thisAddonIsAlreadyConfigured'); }); return false; }
            if (config.Addons.length >= 64) { message(function () { return t('the64AddonLimitHasBeenReached'); }, 'error'); return false; }
            message(function () { return t('validatingTheAddonManifest'); });
            try {
                var manifest = await request('Siphon/Addons/Validate', 'POST', { ManifestUrl: url });
                var addon = { Id: uuid(), ManifestUrl: url, DisplayName: manifest.Name, Enabled: true, Catalogs: [] };
                mergeCatalogs(addon, manifest);
                config.Addons.push(addon);
                manifests.set(addon.Id, manifest); manifestStates.set(addon.Id, 'ready');
                get('siphonCatalogFilter').value = 'all'; get('siphonCatalogSearch').value = '';
                renderAddons(); markDirty();
                if (addon.Catalogs.length) showCatalogs(addon);
                message(function () { return addon.Catalogs.length ? t('addonValidatedSelectItsCatalogsThenSave') : t('addonValidatedSaveChangesToEnableIts'); });
                return true;
            } catch (error) {
                var failure = await readError(error, function () { return t('theAddonCouldNotBeValidatedCheck'); });
                if (failure.Code === 'PrivateHostBlocked') showPrivateHosts();
                message(function () { return failure.Message; }, 'error');
                return false;
            }
        }
        function fact(parent, label, value) {
            node('dt', label, parent);
            node('dd', value, parent);
        }
        function formattedDate(value) {
            return value && Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleString(ui.locale()) : t('notRecorded');
        }
        function renderCapabilities(section, tools, manifest) {
            var names = { get catalog() { return t('catalogs'); }, get meta() { return t('metadata'); }, get stream() { return t('streams'); }, get subtitles() { return t('subtitles'); }, get addon_catalog() { return t('addonDiscovery'); } };
            var resources = manifest.Resources || [];
            var declared = Array.from(new Set(resources.map(function (resource) { return resource.Name; })));
            if (manifest.Catalogs.length && !declared.includes('catalog')) declared.unshift('catalog');
            var summary = node('p', function () { return t('detected') + (declared.map(function (name) { return names[name] || t('unknownResource'); }).join(' · ') || t('noSupportedResourcesDeclared')); });
            summary.className = 'siphon-capabilities';
            section.insertBefore(summary, tools);
            var facts = node('dl', undefined, tools);
            facts.className = 'siphon-facts';
            fact(facts, function () { return t('declaredTypes'); }, function () { return (manifest.Types || []).map(mediaType).join(', ') || t('none2'); });
            resources.forEach(function (resource) {
                fact(facts, function () { return names[resource.Name] || t('unknownResource'); }, function () { return (resource.Types || []).map(mediaType).join(', ') || t('noEligibleTypes'); });
                if (resource.IdPrefixes != null) fact(facts, function () { return t('idRestrictions'); }, function () { return resource.IdPrefixes.length ? t('onlyMatchingResourceIDs') : t('noIDsAccepted'); });
            });
            if ((manifest.Types || []).includes('tv') || manifest.Catalogs.some(function (catalog) { return catalog.Type === 'tv'; }))
                node('p', function () { return t('liveTVIsDeclaredByThisManifest'); }, tools).className = 'siphon-help';
        }
        function metadataAddonCapability(addon) {
            var manifest = manifests.get(addon.Id);
            if (!manifest) return null;
            return (manifest.Resources || []).some(function (resource) { return resource.Name === 'meta'; });
        }
        function renderMetadataAddons(selectedId) {
            if (!config) return;
            var select = get('siphonMetadataAddonId');
            var selected = selectedId === undefined ? select.value : String(selectedId || '');
            var selectedAddon = config.Addons.find(function (addon) { return canonicalId(addon.Id) === canonicalId(selected); });
            var options = [{ value: '', get label() { return t('automaticCatalogOriginAddonFirst'); } }];
            var eligible = config.Addons.filter(function (addon) { return addon.Enabled && metadataAddonCapability(addon) !== false; });
            eligible.sort(function (left, right) { return Number(metadataAddonCapability(right) === true) - Number(metadataAddonCapability(left) === true); });
            eligible.forEach(function (addon) {
                options.push({ value: addon === selectedAddon ? selected : addon.Id, label: addon.DisplayName || t('unnamedAddon') });
            });
            if (selected && !eligible.includes(selectedAddon)) {
                options.push({
                    value: selected,
                    label: selectedAddon ? (selectedAddon.DisplayName || t('unnamedAddon')) + (selectedAddon.Enabled ? t('metadataNotSupported') : t('disabled2')) : t('previouslySelectedAddonRemoved')
                });
            }
            // Keep the native select and unchanged options in place while manifests hydrate.
            options.forEach(function (entry, index) {
                var option = Array.from(select.options).find(function (candidate) { return candidate.value === entry.value; });
                if (!option) { option = document.createElement('option'); option.value = entry.value; }
                if (option.textContent !== entry.label) bindText(option, function () { return entry.label; });
                if (select.options[index] !== option) select.insertBefore(option, select.options[index] || null);
            });
            while (select.options.length > options.length) select.remove(select.options.length - 1);
            select.value = selected;
            metadataControlsState();
        }
        function renderMetadataFields() {
            get('siphonMetadataFieldChoices').replaceChildren();
            Object.keys(metadataFields).forEach(function (field) {
                var control = checkbox(get('siphonMetadataFieldChoices'), function () { return metadataFields[field]; }, (config.MetadataRefreshFields || []).includes(field), metadataControlsState);
                control.dataset.metadataField = field;
            });
        }
        function metadataControlsState() {
            var refreshSelected = get('siphonMetadataUpdateMode').value === 'RefreshSelected';
            get('siphonMetadataFields').hidden = !refreshSelected;
            var source = get('siphonMetadataAddonId');
            var selected = Boolean(source.value);
            var addon = config && config.Addons.find(function (entry) { return canonicalId(entry.Id) === canonicalId(source.value); });
            var problem = !selected ? '' : !addon ? t('theSelectedMetadataAddonWasRemovedChoose')
                : !addon.Enabled ? t('theSelectedMetadataAddonIsDisabledEnable')
                : metadataAddonCapability(addon) === false ? t('theSelectedAddonDoesNotDeclareMetadata') : '';
            source.setCustomValidity(problem);
            source.setAttribute('aria-invalid', problem ? 'true' : 'false');
            var status = get('siphonMetadataAddonStatus');
            status.dataset.kind = problem ? 'error' : 'info';
            bindText(status, function () { return problem || (!selected ? t('automaticSelectionIsActiveVerifiedMetadataCapable')
                : metadataAddonCapability(addon) === null ? t('selectedInstallationRetainedMetadataCapabilityIsNot')
                : t('selectedInstallationSuppliesDetailMetadataAndArtwork')); });
            bindText(get('siphonDirectProvidersHelp'), function () { return selected
                ? t('theSelectedAddonSuppliesDetailMetadataAnd')
                : t('directProvidersAreOptInAndSupplement'); });
            bindText(get('siphonTmdbEnableLabel'), function () { return selected ? t('useTMDBOnlyForMissingSeasonPosters') : t('enableTMDBMetadata'); });
            var enabled = selected;
            providers.forEach(function (provider) {
                var toggle = get('siphonEnable' + provider.name + 'Metadata');
                var credential = get('siphon' + provider.credential);
                var inactive = selected && provider.name !== 'Tmdb';
                var active = !inactive && toggle.checked;
                toggle.disabled = inactive;
                credential.disabled = inactive;
                credential.required = active;
                enabled = enabled || active;
            });
            get('siphonTvdbSubscriberPin').disabled = selected;
            var first = page.querySelector('[data-metadata-field]');
            if (first) first.setCustomValidity(refreshSelected && enabled && !page.querySelector('[data-metadata-field]:checked') ? t('selectAtLeastOneMetadataFieldTo') : '');
        }
        function renderSchedule(definition, task) {
            var link = get(definition.link);
            link.hidden = !tasksKnown || !task;
            if (link.hidden) { link.removeAttribute('href'); bindText(get(definition.schedule), function () { return ''; }); return; }
            link.href = '#/dashboard/tasks/' + encodeURIComponent(task.Id);
            function timeOfDay(ticks) {
                var minutes = Math.floor(Number(ticks || 0) / 600000000);
                return String(Math.floor(minutes / 60) % 24).padStart(2, '0') + ':' + String(minutes % 60).padStart(2, '0');
            }
            function triggers() { return (task.Triggers || []).map(function (trigger) {
                if (trigger.Type === 'IntervalTrigger') {
                    var minutes = Number(trigger.IntervalTicks) / 600000000;
                    return t('every') + (minutes >= 60 ? (minutes / 60).toLocaleString(ui.locale()) + t('hours') : minutes.toLocaleString(ui.locale()) + t('minutes'));
                }
                if (trigger.Type === 'DailyTrigger') return t('dailyAt') + timeOfDay(trigger.TimeOfDayTicks);
                if (trigger.Type === 'WeeklyTrigger') return (trigger.DayOfWeek ? t(trigger.DayOfWeek) : t('weekly')) + t('at') + timeOfDay(trigger.TimeOfDayTicks);
                if (trigger.Type === 'StartupTrigger') return t('atServerStartup');
                return t('unknownTrigger');
            }); }
            bindText(get(definition.schedule), function () { return triggers().length ? t('nativeSchedule') + triggers().join(' · ') : t('manualOnlyNoAutomaticTriggers'); });
        }
        function count(value) { return Number.isSafeInteger(value) && value >= 0 ? value.toLocaleString(ui.locale()) : t('notReported'); }
        function taskState(state) {
            return { get Idle() { return t('idle'); }, get Running() { return t('running'); }, get Cancelling() { return t('stopping2'); }, get Completed() { return t('completed'); }, get Cancelled() { return t('cancelled'); }, get Aborted() { return t('interrupted'); }, get Failed() { return t('failed'); } }[state] || t('unknownState');
        }
        function providerName(name) {
            return { Tmdb: 'TMDB', Tvdb: 'TheTVDB', Fanart: 'Fanart', MdbList: 'MDBList' }[name] || t('provider');
        }
        function providerError(code) {
            return { get Timeout() { return t('requestTimedOut'); }, get NetworkError() { return t('networkError'); }, get ProviderUnavailable() { return t('providerUnavailable'); }, get RateLimited() { return t('rateLimited'); }, get AuthenticationFailed() { return t('authenticationFailed'); }, get InvalidResponse() { return t('invalidResponse'); }, get NotFound() { return t('noMatchingRecord'); }, get MissingIdentifier() { return t('missingIdentifier'); }, get MissingCredentials() { return t('missingCredentials'); }, get NotConfigured() { return t('notConfigured'); }, get RequestFailed() { return t('requestFailed2'); }, get HttpError() { return t('httpError'); }, get ResponseTooLarge() { return t('responseTooLarge'); }, get Cancelled() { return t('cancelled'); }, get CircuitOpen() { return t('providerPaused'); } }[code] || t('providerError');
        }
        function renderNativeTasks() {
            taskDefinitions.forEach(function (definition) {
                var task = taskFor(definition);
                var waiting = task && task.State === 'Running' && currentRunKind && currentRunKind !== definition.kind;
                var progress = task && !waiting && task.State !== 'Idle' && Number.isFinite(task.CurrentProgressPercentage)
                    ? ' · ' + Math.round(Math.max(0, Math.min(100, task.CurrentProgressPercentage))) + '%' : '';
                bindText(get(definition.state), function () { return !tasksKnown ? t('taskStatusUnavailableRefreshBeforeStarting') : waiting ? t('waitingForAnotherSiphonTask') : task ? taskState(task.State) + progress : t('nativeTaskUnavailable'); });
                var last = tasksKnown && task && task.LastExecutionResult;
                bindText(get(definition.last), function () { return last ? t('lastRun') + taskState(last.Status) + ' · ' + formattedDate(last.EndTimeUtc) : tasksKnown && task ? t('noCompletedRunYet') : ''; });
                renderSchedule(definition, task);
            });
        }
        function renderRunStatus(status) {
            var facts = get('siphonRunFacts');
            var providerList = get('siphonRunProviders');
            var progress = get('siphonRunProgress');
            facts.replaceChildren(); providerList.replaceChildren(); progress.hidden = true;
            bindText(get('siphonRunTitle'), function () { return t('runDetails'); });
            if (!status) {
                bindText(get('siphonRunStatus'), function () { return t('runDetailsUnavailableNativeTaskControlsRemain'); });
                node('li', function () { return t('providerActivityUnavailable'); }, providerList);
                return;
            }
            var run = status.CurrentRun || status.LastRun;
            if (!run) {
                bindText(get('siphonRunStatus'), function () { return running() ? t('theNativeTaskIsActiveWaitingFor') : t('noRunDetailsRecordedYet'); });
                node('li', function () { return t('noProviderActivityRecordedYet'); }, providerList);
                return;
            }
            var definition = taskDefinitions.find(function (entry) { return entry.kind === run.Kind; });
            var current = Boolean(status.CurrentRun);
            bindText(get('siphonRunTitle'), function () { return current ? t('currentRun') : t('lastRunSummary'); });
            bindText(get('siphonRunStatus'), function () { return (run.Kind === 'Startup' ? t('startupRestoration') : definition ? definition.name : t('siphonTask')) + ' · ' + taskState(run.State)
                + (run.State === 'Failed' ? t(run.Kind === 'Startup' ? 'startupRestorationFailed' : 'openDiagnosticsForAddonResults') : run.State === 'Cancelled' ? t('countsReflectWorkCompletedBeforeCancellation') : ''); });
            function stage() { return { get Preparing() { return t('preparing'); }, get Catalogs() { return t('fetchingCatalogs'); }, get Metadata() { return t('enrichingMetadata'); }, get Saving() { return t('saving'); }, get Publishing() { return t('publishingToJellyfin'); }, get Credits() { return t('updatingCredits'); }, get Finalizing() { return t('finalizing'); } }[run.Stage] || t('notReported'); }
            function unit() { return { get Catalogs() { return t('catalogs2'); }, get Titles() { return t('titles'); }, get Series() { return t('series'); }, get Items() { return t('items'); }, get Percent() { return t('percent'); } }[run.Unit] || t('units'); }
            fact(facts, function () { return current ? t('currentStage') : t('finalStage'); }, function () { return stage; });
            fact(facts, function () { return t('stageProgress'); }, function () { return count(run.Completed) + ' / ' + count(run.Total) + ' ' + unit(); });
            if (current && Number.isSafeInteger(run.Total) && run.Total > 0 && Number.isSafeInteger(run.Completed) && run.Completed >= 0) {
                progress.max = run.Total; progress.value = Math.min(run.Completed, run.Total); progress.hidden = false;
                bindText(progress, function () { return  stage() + ': ' + count(run.Completed) + t('of') + count(run.Total) + ' ' + unit(); }, 'aria-label');
            }
            fact(facts, function () { return t('prioritySeries'); }, function () { return count(run.PrioritySeries); });
            fact(facts, function () { return t('started2'); }, function () { return formattedDate(run.StartedAtUtc); });
            if (run.FinishedAtUtc) fact(facts, function () { return t('finished'); }, function () { return formattedDate(run.FinishedAtUtc); });
            [[function () { return t('added2'); }, 'Added'], [function () { return t('updated2'); }, 'Updated'], [function () { return t('unchanged2'); }, 'Unchanged'], [function () { return t('removed2'); }, 'Removed'], [function () { return t('preserved2'); }, 'Preserved'], [function () { return t('failedSubscriptions'); }, 'FailedSubscriptions']].forEach(function (entry) {
                fact(facts, function () { return entry[0]; }, function () { return count(run[entry[1]]); });
            });
            (run.Providers || []).forEach(function (provider) {
                var row = node('li', function () { return providerName(provider.Provider) + ' · ' + count(provider.Requests) + t('requests') + count(provider.CacheHits) + t('cacheHits'); }, providerList);
                var errors = Object.keys(provider.Errors || {}).filter(function (code) { return provider.Errors[code] > 0; });
                if (errors.length) {
                    var list = node('ul', undefined, row);
                    errors.forEach(function (code) { node('li', function () { return providerError(code) + ': ' + count(provider.Errors[code]); }, list); });
                } else node('span', function () { return t('0Errors'); }, row);
            });
            if (!providerList.childElementCount) node('li', function () { return t('noProviderRequestsOrCacheHitsRecorded'); }, providerList);
        }
        function showResult(element, result, successText) {
            var failures = {
                get NotConfigured() { return t('saveAValueBeforeTesting'); }, get MissingCredentials() { return t('aSavedCredentialIsRequired'); },
                get InvalidConfiguredUrl() { return t('theSavedServerAddressIsInvalid'); }, get ServerIdentityUnavailable() { return t('theLocalServerIdentityIsUnavailable'); },
                get RedirectBlocked() { return t('theServerAddressRedirectsSaveItsFinal'); }, get HttpError() { return t('theRemoteServerReturnedAnHTTPError'); },
                get ResponseTooLarge() { return t('theResponseExceededTheSafetyLimit'); }, get InvalidServerResponse() { return t('theAddressDidNotReturnValidJellyfin'); },
                get WrongServer() { return t('theAddressReachesADifferentJellyfinServer'); }, get Timeout() { return t('theRequestDidNotFinishWithinIts'); },
                get Unreachable() { return t('theDestinationCouldNotBeReached'); }, get NetworkError() { return t('theProviderCouldNotBeReached'); },
                get AuthenticationFailed() { return t('authenticationFailedCheckTheKeyAndAny'); }, get RateLimited() { return t('theProviderRateLimitWasReached'); },
                get PrivateHostBlocked() { return t('theDestinationNeedsAnExactPrivateHost'); }, get RequestFailed() { return t('theRequestFailedCheckTheSavedConfiguration'); },
                get ManifestUnavailable() { return t('theAddonManifestCouldNotBeLoaded'); }, get AddonNotFound() { return t('thisAddonIsNoLongerSaved'); }, get Cancelled() { return t('theCheckWasCancelled'); },
                get NotFound() { return t('theProviderHasNoMatchingRecord'); }, get MissingIdentifier() { return t('noSupportedIdentifierIsAvailableForThis'); },
                get InvalidResponse() { return t('theProviderReturnedAnInvalidResponse'); }, get ProviderUnavailable() { return t('theProviderIsUnavailable'); }
            };
            var elapsed = Number.isFinite(result.ElapsedMilliseconds) ? ' · ' + result.ElapsedMilliseconds.toLocaleString(ui.locale()) + t('ms2') : '';
            bindText(element, function () { return (result.Success ? resolveText(successText) : failures[result.Code] || t('checkFailed') + (result.Code || t('unavailable2'))) + elapsed; });
            element.dataset.kind = result.Success ? 'info' : 'error';
        }
        function providerAvailability(status) {
            if (!status) return t('statusUnavailable');
            if (!status.Enabled) return t('disabledInSavedSettings');
            if (!status.Configured) return t('noSavedCredential');
            var until = Date.parse(status.PausedUntilUtc);
            var failures = status.ConsecutiveFailures > 0 ? ' · ' + count(status.ConsecutiveFailures) + t('consecutiveFailures') : '';
            if (Number.isFinite(until) && until > Date.now()) {
                return t('pausedUntil') + formattedDate(status.PausedUntilUtc) + ' · ' + providerError(status.PauseReason) + failures
                    + (status.PauseReason === 'AuthenticationFailed' ? t('checkSavedCredentials') : t('recoveryWillBeCheckedOnALater'));
            }
            if (status.PauseReason || Number.isFinite(until)) return t('cooldownEndedRecoveryWillBeCheckedOn') + failures;
            if (status.LastResult && status.LastResult.Success) return t('availableLastRequestSucceeded');
            if (status.LastResult) return providerError(status.LastResult.Code) + failures + t('notCurrentlyPaused');
            return t('readyNotTestedInThisServerSession');
        }
        async function refreshProviderStatus(expectedGeneration) {
            var statuses;
            var statusError = null;
            try { statuses = await request('Siphon/Metadata/Providers/Status', 'GET'); }
            catch (error) { statuses = null; statusError = error; }
            if (expectedGeneration !== generation) return;
            if (!Array.isArray(statuses)) statuses = null;
            var list = get('siphonProviderHealth');
            list.replaceChildren();
            providers.forEach(function (provider) {
                var status = statuses && statuses.find(function (entry) { return entry.Provider === provider.name; });
                function text() { return statusError ? featureError(statusError, t('statusUnavailable')) : providerAvailability(status) + quotaObservation(status); }
                node('li', function () { return providerName(provider.name) + ' · ' + text(); }, list);
                var element = get('siphonProvider' + provider.name);
                var health = get('siphonHealth' + provider.name);
                if (!health) {
                    health = node('p', undefined, element.parentElement);
                    health.id = 'siphonHealth' + provider.name;
                    health.className = 'siphon-result';
                    health.setAttribute('role', 'status');
                }
                bindText(health, function () { return text; });
                if (!element.textContent && status) {
                    if (status.LastResult) showResult(element, status.LastResult, function () { return t('lastProviderRequestSucceeded'); });
                    else bindText(element, function () { return status.Configured ? t('savedCredentialNotTestedInThisServer') : t('noSavedCredential'); });
                }
            });
        }
        async function refreshDiagnostics() {
            if (!config) return;
            var requestId = ++diagnosticsRequestId;
            var expectedGeneration = generation;
            diagnosticsLoading = true; updateState();
            bindText(get('siphonDiagnosticsStatus'), function () { return t('loadingTheSavedDiagnosticReport'); });
            try {
                var report = await request('Siphon/Diagnostics/Health', 'GET');
                if (expectedGeneration !== generation || requestId !== diagnosticsRequestId) return;
                diagnostics = report;
                renderDiagnostics();
                renderHealth();
                bindText(get('siphonDiagnosticsStatus'), function () { return t('reportGenerated') + formattedDate(report.GeneratedAtUtc) + (report.StorageCode && report.StorageCode !== 'Ok' ? t('diagnosticStorage') + report.StorageCode : ''); });
            } catch (error) {
                if (expectedGeneration === generation && requestId === diagnosticsRequestId) bindText(get('siphonDiagnosticsStatus'), function () { return featureError(error, t('theDiagnosticReportCouldNotBeLoaded')); });
            } finally {
                if (requestId === diagnosticsRequestId) { diagnosticsLoading = false; updateState(); }
            }
        }
        function renderDiagnostics() {
            var parent = get('siphonDiagnosticAddons');
            parent.replaceChildren();
            var savedAddons = JSON.parse(savedConfiguration).Addons || [];
            if (!diagnostics.Addons.length) node('p', function () { return t('noSavedAddonsToDiagnose'); }, parent).className = 'siphon-help';
            diagnostics.Addons.forEach(function (entry) {
                var addon = savedAddons.find(function (candidate) { return candidate.Id === entry.InstallationId; });
                var section = node('section', undefined, parent);
                section.className = 'siphon-diagnostic';
                node('h3', function () { return addon && addon.DisplayName || entry.InstallationId; }, section);
                var facts = node('dl', undefined, section);
                facts.className = 'siphon-facts';
                fact(facts, function () { return t('state'); }, function () { return entry.Enabled ? t('enabled') : t('disabled'); });
                fact(facts, function () { return t('catalogsSelected2'); }, function () { return String(entry.EnabledCatalogCount); });
                fact(facts, function () { return t('lastSuccessfulSync'); }, function () { return formattedDate(entry.LastSuccessUtc); });
                fact(facts, function () { return t('lastFailedSync'); }, function () { return formattedDate(entry.LastFailureUtc) + (entry.LastErrorCode ? ' · ' + entry.LastErrorCode : ''); });
                var check = entry.LastManifestCheck;
                fact(facts, function () { return t('lastConnectivityCheck'); }, function () { return check ? formattedDate(check.CheckedAtUtc) + ' · ' + (check.Success ? t('successful') : check.Code) + ' · ' + check.ElapsedMilliseconds.toLocaleString(ui.locale()) + t('ms2') : t('notTested'); });
                if (check && check.Capabilities) {
                    fact(facts, function () { return t('resources'); }, function () { return check.Capabilities.Resources.map(function (resource) { return resourceName(resource.Name); }).join(', ') || t('none2'); });
                    fact(facts, function () { return t('types'); }, function () { return check.Capabilities.Types.map(mediaType).join(', ') || t('none2'); });
                }
                var outcome = node('p', function () { return ''; }, section);
                outcome.className = 'siphon-result'; outcome.setAttribute('role', 'status');
                var test = button(section, function () { return t('testSavedAddon'); }, function () {
                    work(async function () {
                        if (dirty) return;
                        bindText(outcome, function () { return t('testingFromTheJellyfinServer'); });
                        var result = await request('Siphon/Diagnostics/Addons/' + encodeURIComponent(entry.InstallationId), 'POST');
                        showResult(outcome, result, function () { return t('addonManifestIsReachable'); });
                        await refreshDiagnostics();
                    });
                });
                test.dataset.savedAction = '';
            });
            updateState();
        }
        async function previewCleanup(append) {
            var offset = append ? cleanupItems.length : 0;
            var path = 'Siphon/Cleanup/Preview?startIndex=' + offset + '&limit=50';
            if (append && cleanupPreview) path += '&previewToken=' + encodeURIComponent(cleanupPreview.PreviewToken);
            bindText(get('siphonCleanupStatus'), function () { return t('checkingConfirmedAbsenceAndUserProtections'); });
            try {
                var result = await request(path, 'GET');
                cleanupPreview = result;
                cleanupItems = append ? cleanupItems.concat(result.Items) : result.Items;
                var list = get('siphonCleanupItems');
                list.replaceChildren();
                var reasons = { get Eligible() { return t('eligibleForCleanup'); }, get GracePeriod() { return t('withinTheGracePeriod'); }, get OwnershipUnconfirmed() { return t('catalogOwnershipIsNotConfirmed'); }, get Favorite() { return t('protectedFavorite'); }, get ResumePosition() { return t('protectedResumePosition'); }, get ProtectionUnavailable() { return t('protectionCouldNotBeVerifiedRetained'); }, get SynchronizationUnconfirmed() { return t('absenceHasNotBeenConfirmedByA'); } };
                cleanupItems.forEach(function (entry) {
                    var row = node('li', undefined, list);
                    node('strong', function () { return entry.Name; }, row);
                    node('p', function () { return mediaType(entry.Type) + ' · ' + (reasons[entry.Reason] || t('unknownReason')); }, row).className = 'siphon-help';
                    node('p', function () { return t('missingSince') + formattedDate(entry.MissingSinceUtc) + t('eligibleAfter') + formattedDate(entry.EligibleAfterUtc); }, row).className = 'siphon-help';
                });
                bindText(get('siphonCleanupStatus'), function () { return result.TotalMissing.toLocaleString(ui.locale()) + t('missing2') + result.TotalEligible.toLocaleString(ui.locale()) + t('eligible') + result.TotalProtected.toLocaleString(ui.locale()) + t('protectedPreviewExpires') + formattedDate(result.ExpiresAtUtc) + '.'; });
                get('siphonMoreCleanup').hidden = cleanupItems.length >= result.TotalMissing;
            } catch (error) {
                cleanupPreview = null;
                get('siphonMoreCleanup').hidden = true;
                bindText(get('siphonCleanupStatus'), function () { return featureError(error, t('previewUnavailableOrStaleWaitForSynchronization')); });
            }
            updateState();
        }
        function credentialsState() {
            page.querySelectorAll('[data-credential]').forEach(function (element) {
                bindText(element, function () { return get('siphon' + element.dataset.credential).value ? t('configuredMasked') : t('notConfigured'); });
            });
        }
        function refreshStatus() {
            if (statusPromise) return statusPromise;
            statusPromise = fetchStatus().finally(function () { statusPromise = null; });
            return statusPromise;
        }
        async function fetchStatus() {
            statusLoading = true; updateState();
            clearTimeout(statusTimer);
            var previouslyActive = activeTask();
            var expectedGeneration = generation;
            try {
                var results = await Promise.allSettled([
                    request('Siphon/Status', 'GET'), request('ScheduledTasks', 'GET'),
                    request('Siphon/Sync/Status', 'GET'), refreshProviderStatus(generation)
                ]);
                if (expectedGeneration !== generation || !visible) return;
                tasksKnown = results[1].status === 'fulfilled' && Array.isArray(results[1].value);
                if (tasksKnown) nativeTasks = results[1].value.filter(function (entry) {
                    return taskDefinitions.some(function (definition) { return definition.key === entry.Key; });
                });
                var runStatus = results[2].status === 'fulfilled' ? results[2].value : null;
                latestRunStatus = runStatus;
                currentRunKind = runStatus && runStatus.CurrentRun ? runStatus.CurrentRun.Kind : null;
                renderNativeTasks();
                var active = activeTask();
                var definition = active && taskDefinitions.find(function (entry) { return entry.key === active.Key; });
                function statusText() {
                var text = !tasksKnown ? t('taskStatusUnavailableCheckYourAdministratorSession')
                    : active ? definition.name + ' · ' + taskState(active.State) : running() ? t('siphonTasksAreActiveWaitingForRun') : t('noActiveSiphonTask');
                if (results[0].status === 'fulfilled') {
                    text += ' · ' + count(results[0].value.MovieCount) + t('movies2') + count(results[0].value.EpisodeCount) + t('episodes');
                } else text += t('libraryCountsUnavailable');
                    return text;
                }
                bindText(get('siphonStatus'), function () { return statusText; });
                renderRunStatus(runStatus);
                var historyRun = runStatus && runStatus.LastRun;
                var historyKey = historyRun && (historyRun.Id + ':' + historyRun.State + ':' + historyRun.FinishedAtUtc);
                if (historyKey && historyKey !== lastHistoryRun) {
                    lastHistoryRun = historyKey;
                    refreshHistory();
                    refreshTargets();
                }
                if (tasksKnown && previouslyActive && !running()) {
                    var finished = nativeTasks.find(function (task) { return task.Id === previouslyActive.Id; });
                    var last = finished && finished.LastExecutionResult;
                    if (last) message(function () { return last.Status === 'Completed' ? t('siphonTaskCompleted')
                        : last.Status === 'Failed' ? t('siphonTaskFailedOpenDiagnosticsForAddon')
                        : t('siphonTaskStopped') + taskState(last.Status) + '.'; }, last.Status === 'Failed' ? 'error' : 'info');
                    cleanupPreview = null;
                    get('siphonMoreCleanup').hidden = true;
                    if (cleanupItems.length) bindText(get('siphonCleanupStatus'), function () { return t('aTaskChangedLibraryStateGenerateA'); });
                    if (recoveryPreview) invalidateRecovery(function () { return t('aTaskChangedLibraryStateGenerateA2'); });
                    if (diagnostics) refreshDiagnostics();
                }
            } catch (error) {
                if (expectedGeneration !== generation || !visible) return;
                tasksKnown = false;
                currentRunKind = null;
                bindText(get('siphonStatus'), function () { return featureError(error, t('statusUnavailableCheckYourAdministratorSessionAnd')); });
                renderNativeTasks(); renderRunStatus(null);
            } finally {
                statusLoading = false; updateState();
                if (visible) statusTimer = setTimeout(refreshStatus, expectedGeneration !== generation ? 0 : running() ? 3000 : 15000);
            }
        }
        async function load() {
            var expectedGeneration = ++generation;
            invalidateSearch(); invalidateHistory();
            targetsRequest++; targetsLoading = false; targets = null;
            seriesOffset = 0; renderSeries();
            historySelection = null; historyOffset = 0; historyTotal = 0;
            get('siphonHistoryDetails').hidden = true;
            get('siphonHistory').replaceChildren();
            bindText(get('siphonHistoryStatus'), function () { return t('openTasksToLoadRecentRuns'); });
            searchItems = []; additionsItems = []; searchOffset = 0; additionsOffset = 0;
            renderSearchItems(false); renderSearchItems(true);
            bindText(get('siphonSearchStatus'), function () { return t('enterATitleAndChooseSearchAddons'); });
            message(function () { return t('loadingSavedSettings'); });
            try {
                var loaded;
                try { loaded = await ApiClient.getPluginConfiguration(pluginId); }
                catch (error) { throw await ui.error(error, t('settingsLoadFailed')); }
                if (expectedGeneration !== generation) return;
                savedConfiguration = JSON.stringify(loaded);
                config = loaded;
                config.Addons = config.Addons || [];
                var defaults = { DefaultSearchMode: 'All', UnreleasedBufferDays: 0, IntroDbCacheHours: 24, IntroDbSegments: ['intro', 'recap', 'outro', 'post-credits'], P2pMaxConcurrentStreams: 2, P2pMaxCacheMiB: 20480, P2pDownloadLimitKiB: 4096, P2pUploadLimitKiB: 256, P2pIdleMinutes: 5, P2pMetadataTimeoutSeconds: 90, P2pListenPort: 0, P2pEnableDht: true, DownloadMaxConcurrentJobs: 2, DownloadMaxStorageMiB: 51200, DownloadMaxFileMiB: 10240, DownloadMaxJobsPerUser: 20, DownloadRetentionDays: 7, DownloadMaxStoragePerUserMiB: 20480, DownloadWindowEnabled: false, DownloadWindowStartUtcHour: 0, DownloadWindowEndUtcHour: 6 };
                Object.keys(defaults).forEach(function (key) { if (config[key] == null) config[key] = defaults[key]; });
                config.UserProfiles = config.UserProfiles || [];
                config.UserProfiles.forEach(function (profile) { profile.Addons = profile.Addons || []; });
                renderProfileUsers(); renderIntroTypes();
                invalidateRecovery(function () { return ''; });
                p2pKnown = false;
                config.Addons.forEach(function (addon) {
                    addon.Catalogs = addon.Catalogs || [];
                    addon.Catalogs.forEach(function (catalog) { catalog.Extras = catalog.Extras || []; catalog.Presentation = catalog.Presentation || 'Library'; });
                });
                manifests.clear(); manifestStates.clear(); discoveries.clear(); discoveredKeys.clear();
                renderMetadataAddons(config.MetadataAddonId);
                textFields.concat(numberFields).forEach(function (key) { get('siphon' + key).value = config[key] == null ? '' : config[key]; });
                if (config.MetadataCacheHours == null) get('siphonMetadataCacheHours').value = 24;
                get('siphonAllowedPrivateHosts').value = (config.AllowedPrivateHosts || []).join('\n');
                booleanFields.forEach(function (key) { get('siphon' + key).checked = Boolean(config[key]); });
                renderMetadataFields();
                metadataControlsState();
                cleanupPreview = null; cleanupItems = [];
                get('siphonCleanupItems').replaceChildren();
                bindText(get('siphonCleanupStatus'), function () { return ''; });
                get('siphonMoreCleanup').hidden = true;
                diagnostics = null;
                get('siphonDiagnosticAddons').replaceChildren();
                bindText(get('siphonDiagnosticsStatus'), function () { return t('openDiagnosticsToLoadTheSavedReport'); });
                providers.forEach(function (provider) { bindText(get('siphonProvider' + provider.name), function () { return ''; }); });
                dirty = false;
                if (!config.Addons.some(function (addon) { return addon.Catalogs.some(function (catalog) { return catalog.Enabled; }); })) get('siphonCatalogFilter').value = 'all';
                renderAddons(); credentialsState();
                get('siphonHealthFacts').replaceChildren();
                get('siphonHealthProviders').replaceChildren();
                message(function () { return config.PublicBaseUrl ? '' : t('setAndSaveThePublicJellyfinURL'); });
                get('siphonAddons').setAttribute('aria-busy', 'false');
                get('siphonCatalogs').setAttribute('aria-busy', 'false');
                refreshProviderStatus(expectedGeneration);
                refreshTargets();
                refreshHistory();
                refreshAdditions();
                if (!get('siphonPanelProfiles').hidden) loadUsers();
                if (!get('siphonPanelPlayback').hidden) refreshP2p();
                if (!get('siphonPanelDiagnostics').hidden) refreshDiagnostics();
            } catch (error) {
                if (expectedGeneration !== generation) return;
                config = null;
                bindText(get('siphonOverview'), function () { return featureError(error, t('configurationUnavailable')); });
                message(function () { return featureError(error, t('settingsLoadFailed')); }, 'error');
            }
        }
        function revealInvalid(control) {
            get('siphonCatalogSearch').value = ''; get('siphonCatalogFilter').value = 'all'; applyFilters();
            var parent = control.parentElement;
            while (parent && parent !== page) {
                if (parent.tagName === 'DETAILS') parent.open = true;
                if (parent.getAttribute('role') === 'tabpanel') selectPanel(parent.id);
                parent = parent.parentElement;
            }
            control.focus(); control.reportValidity();
        }
        get('siphonIcon').src = ApiClient.getUrl('Siphon/Icon');
        get('siphonUserPortal').href = ApiClient.getUrl('Siphon/User');
        get('siphonCopyPortal').addEventListener('click', async function () {
            var url = new URL(ApiClient.getUrl('Siphon/User'), window.location.href).href;
            var copied = await ui.copyText(url);
            var fallback = get('siphonPortalCopyFallback'); fallback.hidden = copied; fallback.value = url;
            get('siphonPortalCopyLabel').hidden = copied;
            message(function () { return copied ? t('portalCopied') : t('portalManualCopy'); });
            if (!copied) { fallback.focus(); fallback.select(); }
        });
        get('siphonGuideConnection').addEventListener('click', function () { selectPanel('siphonPanelConnection'); get('siphonPublicBaseUrl').focus(); });
        get('siphonGuideAddAddon').addEventListener('click', function () { selectPanel('siphonPanelAddons'); get('siphonManifestUrl').focus(); });
        get('siphonGuideSelectCatalog').addEventListener('click', function () { selectPanel('siphonPanelCatalogs'); get('siphonCatalogFilter').value = 'all'; applyFilters(); get('siphonCatalogSearch').focus(); });
        get('siphonGuideTestConnection').addEventListener('click', function () { work(testGuideConnection); });
        get('siphonGuideTestManifest').addEventListener('click', function () { work(testGuideManifest); });
        get('siphonGuideCatalog').addEventListener('change', function () { guidePreview = null; renderGuide(); });
        get('siphonGuidePreview').addEventListener('click', previewGuideCatalog);
        get('siphonGuideImport').addEventListener('click', function () {
            if (!guidePreview || guidePreview.configuration !== savedConfiguration || get('siphonGuideImport').disabled) return;
            startTarget({ CatalogKey: guidePreview.key });
        });
        get('siphonReloadUsers').addEventListener('click', loadUsers);
        get('siphonProfileUser').addEventListener('change', renderProfile);
        get('siphonInspect').addEventListener('click', function () { inspectItem('Inspection'); });
        get('siphonP2pRefresh').addEventListener('click', refreshP2p);
        get('siphonP2pCleanup').addEventListener('click', function () { work(cleanupP2p); });
        get('siphonSources').addEventListener('click', function () { inspectItem('Sources'); });
        get('siphonSourcesRefresh').addEventListener('click', function () { inspectItem('Sources/Refresh'); });
        get('siphonInspectItem').addEventListener('keydown', function (event) { if (event.key === 'Enter') { event.preventDefault(); inspectItem('Inspection'); } });
        get('siphonInspectItem').addEventListener('input', function () {
            inspectionRequest++; inspectionLoading = false;
            get('siphonInspection').replaceChildren(); get('siphonInspection').setAttribute('aria-busy', 'false');
            bindText(get('siphonInspectionStatus'), function () { return t('chooseMetadataInspectionOrSourceDiagnosisFor'); });
            updateFeatureState();
        });
        get('siphonInspectCancel').addEventListener('click', function () {
            inspectionRequest++; inspectionLoading = false;
            get('siphonInspection').setAttribute('aria-busy', 'false');
            bindText(get('siphonInspectionStatus'), function () { return t('viewRequestCancelledLateResultsWillBe'); });
            updateFeatureState();
        });
        get('siphonRecoveryPreview').addEventListener('click', function () { work(function () { return previewRecovery(false); }); });
        get('siphonRecoveryNext').addEventListener('click', function () { work(function () { return previewRecovery(true); }); });
        get('siphonRecoveryExecute').addEventListener('click', function () { work(executeRecovery); });
        get('siphonRefreshTargets').addEventListener('click', refreshTargets);
        get('siphonTargetSeries').addEventListener('change', updateFeatureState);
        get('siphonTarget').addEventListener('click', function () { startTarget({ ContentKey: get('siphonTargetSeries').value }); });
        get('siphonSeriesFilter').addEventListener('input', function () { seriesOffset = 0; renderSeries(); });
        get('siphonSeriesPrevious').addEventListener('click', function () { seriesOffset = Math.max(0, seriesOffset - pageSize); renderSeries(); });
        get('siphonSeriesNext').addEventListener('click', function () { seriesOffset += pageSize; renderSeries(); });
        get('siphonRefreshHistory').addEventListener('click', refreshHistory);
        get('siphonHistoryPrevious').addEventListener('click', function () { loadHistoryDetails(Math.max(0, historyOffset - pageSize)); });
        get('siphonHistoryNext').addEventListener('click', function () { loadHistoryDetails(historyOffset + pageSize); });
        get('siphonHistoryRetry').addEventListener('click', function () { loadHistoryDetails(historyOffset); });
        get('siphonSearchForm').addEventListener('submit', function (event) { event.preventDefault(); searchTitles(); });
        get('siphonSearchTerm').addEventListener('input', function () {
            searchRequest++; searchLoading = false; searchItems = []; searchOffset = 0;
            get('siphonSearchResults').setAttribute('aria-busy', 'false');
            bindText(get('siphonSearchStatus'), function () { return dirty ? t('saveOrDiscardSettingsChangesBeforeSearching') : t('chooseSearchAddonsToSubmitThisTitle'); });
            renderSearchItems(false);
        });
        get('siphonSearchCancel').addEventListener('click', function () {
            searchRequest++; searchLoading = false;
            get('siphonSearchResults').setAttribute('aria-busy', 'false');
            bindText(get('siphonSearchStatus'), function () { return t('searchCancelledAnyLateResultsWillBe'); });
            updateFeatureState();
        });
        get('siphonRefreshAdditions').addEventListener('click', refreshAdditions);
        get('siphonSearchPrevious').addEventListener('click', function () { searchOffset = Math.max(0, searchOffset - pageSize); renderSearchItems(false); });
        get('siphonSearchNext').addEventListener('click', function () { searchOffset += pageSize; renderSearchItems(false); });
        get('siphonAdditionsPrevious').addEventListener('click', function () { additionsOffset = Math.max(0, additionsOffset - pageSize); renderSearchItems(true); });
        get('siphonAdditionsNext').addEventListener('click', function () { additionsOffset += pageSize; renderSearchItems(true); });
        get('siphonMetadataUpdateMode').addEventListener('change', metadataControlsState);
        get('siphonMetadataAddonId').addEventListener('change', function () { renderMetadataAddons(); markDirty(); });
        get('siphonRefreshDiagnostics').addEventListener('click', refreshDiagnostics);
        get('siphonTestConnection').addEventListener('click', function () {
            work(async function () {
                if (dirty) return;
                bindText(get('siphonConnectionResult'), function () { return t('testingTheSavedAddressFromTheServer'); });
                showResult(get('siphonConnectionResult'), await request('Siphon/Diagnostics/Connection', 'POST'), function () { return t('thisAddressReachesTheCurrentJellyfinServer'); });
            });
        });
        get('siphonExportDiagnostics').addEventListener('click', function () {
            if (!diagnostics) return;
            var url = URL.createObjectURL(new Blob([JSON.stringify(diagnostics, null, 2)], { type: 'application/json' }));
            var link = node('a', undefined, page);
            link.href = url; link.download = 'siphon-diagnostics-' + new Date().toISOString().slice(0, 10) + '.json';
            link.click(); link.remove();
            setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
        });
        page.querySelectorAll('[data-provider-test]').forEach(function (control) {
            control.addEventListener('click', function () {
                work(async function () {
                    if (dirty) return;
                    var provider = control.dataset.providerTest;
                    var outcome = get('siphonProvider' + provider);
                    bindText(outcome, function () { return t('testingSavedCredentials'); });
                    showResult(outcome, await request('Siphon/Metadata/Providers/' + encodeURIComponent(provider) + '/Test', 'POST'), function () { return t('providerCredentialsAccepted'); });
                    await refreshProviderStatus(generation);
                });
            });
        });
        get('siphonPreviewCleanup').addEventListener('click', function () { work(function () { return previewCleanup(false); }); });
        get('siphonMoreCleanup').addEventListener('click', function () { work(function () { return previewCleanup(true); }); });
        get('siphonExecuteCleanup').addEventListener('click', function () {
            work(async function () {
                if (dirty || running() || !tasksKnown || !cleanupPreview || cleanupPreview.TotalEligible === 0) return;
                if (Date.parse(cleanupPreview.ExpiresAtUtc) <= Date.now()) {
                    cleanupPreview = null; bindText(get('siphonCleanupStatus'), function () { return t('previewExpiredGenerateANewPreviewBefore'); }); return;
                }
                if (!window.confirm(t('removeAll') + cleanupPreview.TotalEligible + t('eligibleMissingSiphonMediaItemsUserProtections'))) return;
                try {
                    var result = await request('Siphon/Cleanup/Execute', 'POST', { PreviewToken: cleanupPreview.PreviewToken, Confirmed: true });
                    cleanupPreview = null; cleanupItems = [];
                    get('siphonCleanupItems').replaceChildren(); get('siphonMoreCleanup').hidden = true;
                    bindText(get('siphonCleanupStatus'), function () { return result.DeletedCount.toLocaleString(ui.locale()) + t('missingSiphonMediaItemsRemovedPersonalFiles'); });
                    await refreshStatus();
                } catch (error) {
                    cleanupPreview = null;
                    bindText(get('siphonCleanupStatus'), function () { return featureError(error, t('cleanupWasNotConfirmedLibraryStateOr')); });
                }
            });
        });
        tabs.forEach(function (tab, index) {
            tab.addEventListener('click', function () { selectPanel(tab.getAttribute('aria-controls')); });
            tab.addEventListener('keydown', function (event) {
                var target = event.key === 'ArrowRight' ? (index + 1) % tabs.length : event.key === 'ArrowLeft' ? (index + tabs.length - 1) % tabs.length : event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : -1;
                if (target >= 0) { event.preventDefault(); selectPanel(tabs[target].getAttribute('aria-controls'), true); }
            });
        });
        get('siphonCatalogSearch').addEventListener('input', applyFilters);
        get('siphonCatalogFilter').addEventListener('change', applyFilters);
        get('siphonHideCatalogs').addEventListener('click', function () {
            openCatalogGroups.clear();
            page.querySelectorAll('.siphon-catalog-group').forEach(function (group) { group.open = false; });
            catalogVisibilityState();
        });
        get('siphonPrivateHelp').addEventListener('click', showPrivateHosts);
        get('siphonUseServer').addEventListener('click', function () { get('siphonPublicBaseUrl').value = ApiClient.getUrl(''); markDirty(); });
        get('siphonAdd').addEventListener('click', function () {
            work(async function () {
                var url = get('siphonManifestUrl').value.trim();
                if (!url) { message(function () { return t('pasteAnAddonManifestURLFirst'); }, 'error'); get('siphonManifestUrl').focus(); return; }
                if (await installAddon(url)) get('siphonManifestUrl').value = '';
            });
        });
        get('SiphonConfigForm').addEventListener('input', function (event) {
            if (event.target.id.indexOf('siphonDownload') === 0) { get('siphonDownloadMaxFileMiB').setCustomValidity(''); get('siphonDownloadWindowEndUtcHour').setCustomValidity(''); }
            if (!['siphonManifestUrl', 'siphonCatalogSearch', 'siphonCatalogFilter', 'siphonProfileUser', 'siphonInspectItem'].includes(event.target.id) && !event.target.hasAttribute('data-transient')) { markDirty(); credentialsState(); metadataControlsState(); }
        });
        get('SiphonConfigForm').addEventListener('submit', function (event) {
            event.preventDefault();
            if (!config || busy || !dirty || running() || !tasksKnown) return;
            metadataControlsState();
            var port = Number(get('siphonP2pListenPort').value);
            var streams = Number(get('siphonP2pMaxConcurrentStreams').value);
            get('siphonP2pListenPort').setCustomValidity(port !== 0 && (port < 1024 || port + streams - 1 > 65535) ? t('chooseZeroOrAPortFrom1024') : '');
            get('siphonDownloadMaxFileMiB').setCustomValidity(Number(get('siphonDownloadMaxFileMiB').value) > Math.min(Number(get('siphonDownloadMaxStorageMiB').value), Number(get('siphonDownloadMaxStoragePerUserMiB').value)) ? t('budgetMustFit') : '');
            get('siphonDownloadWindowEndUtcHour').setCustomValidity(get('siphonDownloadWindowEnabled').checked && Number(get('siphonDownloadWindowStartUtcHour').value) === Number(get('siphonDownloadWindowEndUtcHour').value) ? t('windowMustDiffer') : '');
            var invalid = get('SiphonConfigForm').querySelector('input:invalid, select:invalid, textarea:invalid');
            if (invalid) { revealInvalid(invalid); message(function () { return t('checkTheHighlightedSettingBeforeSaving'); }, 'error'); return; }
            work(async function () {
                var current = await ApiClient.getPluginConfiguration(pluginId);
                if (JSON.stringify(current) !== savedConfiguration) { message(function () { return t('settingsChangedInAnotherSessionReloadThe'); }, 'error'); return; }
                textFields.forEach(function (key) { config[key] = get('siphon' + key).value; });
                numberFields.forEach(function (key) { config[key] = Number(get('siphon' + key).value); });
                booleanFields.forEach(function (key) { config[key] = get('siphon' + key).checked; });
                config.MetadataRefreshFields = Array.from(page.querySelectorAll('[data-metadata-field]:checked')).map(function (control) { return control.dataset.metadataField; });
                config.IntroDbSegments = Array.from(page.querySelectorAll('[data-intro-type]:checked')).map(function (control) { return control.dataset.introType; });
                config.AllowedPrivateHosts = get('siphonAllowedPrivateHosts').value.split(/\r?\n/).map(function (host) { return host.trim(); }).filter(Boolean);
                var submitted = snapshot();
                try {
                    try { await ApiClient.updatePluginConfiguration(pluginId, submitted); }
                    catch (error) { throw await ui.error(error, t('settingsSaveFailed')); }
                    savedConfiguration = JSON.stringify(submitted);
                    submitted.Addons.forEach(function (addon) { addon.Catalogs.forEach(function (catalog) { discoveredKeys.delete(catalog.Key); }); });
                    dirty = false;
                    message(function () { return t('settingsSavedSynchronizeCatalogsWhenReady'); });
                    invalidateSearch();
                    searchItems = []; searchOffset = 0; renderSearchItems(false);
                    bindText(get('siphonSearchStatus'), function () { return t('savedAddonsChangedSubmitANewSearch'); });
                    await refreshTargets();
                    await refreshStatus();
                } catch (error) { message(function () { return featureError(error, t('settingsWereNotSavedCheckTheSelected')); }, 'error'); }
            });
        });
        get('siphonRefresh').addEventListener('click', refreshStatus);
        get('siphonRetry').addEventListener('click', function () { work(load); });
        get('siphonReload').addEventListener('click', function () {
            if (!dirty || window.confirm(t('discardYourUnsavedChangesAndReloadThe'))) work(load);
        });
        function startTask(definition) {
            work(async function () {
                if (dirty) { message(function () { return t('saveOrDiscardYourChangesBeforeStarting'); }); return; }
                await refreshStatus();
                if (taskBlocked(definition)) { message(function () { return t('thisTaskIsUnavailableAnotherTaskIs'); }); return; }
                var task = taskFor(definition);
                await ApiClient.ajax({ url: ApiClient.getUrl('ScheduledTasks/Running/' + encodeURIComponent(task.Id)), type: 'POST' });
                message(function () { return definition.name + t('requestedProgressUpdatesAutomaticallyWhileThisPage'); });
                await refreshStatus();
            });
        }
        get('siphonSync').addEventListener('click', function () { startTask(taskDefinitions[0]); });
        get('siphonFollowed').addEventListener('click', function () { startTask(taskDefinitions[1]); });
        get('siphonFull').addEventListener('click', function () {
            if (busy || taskBlocked(taskDefinitions[2])) return;
            get('siphonFullConfirm').hidden = false;
            get('siphonFull').setAttribute('aria-expanded', 'true');
            get('siphonConfirmFull').focus();
        });
        get('siphonCancelFull').addEventListener('click', function () {
            get('siphonFullConfirm').hidden = true;
            get('siphonFull').setAttribute('aria-expanded', 'false');
            get('siphonFull').focus();
        });
        get('siphonConfirmFull').addEventListener('click', function () {
            get('siphonFullConfirm').hidden = true;
            get('siphonFull').setAttribute('aria-expanded', 'false');
            startTask(taskDefinitions[2]);
        });
        get('siphonStop').addEventListener('click', function () {
            work(async function () {
                await refreshStatus();
                var task = activeTask();
                if (!tasksKnown || !task || task.State === 'Cancelling') return;
                await ApiClient.ajax({ url: ApiClient.getUrl('ScheduledTasks/Running/' + encodeURIComponent(task.Id)), type: 'DELETE' });
                message(function () { return t('cancellationRequestedWaitForTheActiveTask'); });
                await refreshStatus();
            });
        });
        get('siphonLocale').addEventListener('change', function () { ui.setLocale(this.value); });
        function onLanguageChange() { if (page.isConnected) translatePage(); }
        document.addEventListener('siphonlanguagechange', onLanguageChange);
        page.addEventListener('viewdestroy', function () {
            translationObserver.disconnect();
            textBindings.clear();
            document.removeEventListener('siphonlanguagechange', onLanguageChange);
            window.removeEventListener('beforeunload', beforeUnload);
            clearTimeout(statusTimer);
        });
        translatePage();
        function beforeUnload(event) {
            if (visible && dirty) { event.preventDefault(); event.returnValue = ''; }
        }
        function onViewShow() {
            visible = true;
            window.addEventListener('beforeunload', beforeUnload);
            if (!config || !dirty) work(load);
            else message(function () { return t('yourUnsavedDraftIsStillHereSave'); });
            if (config && dirty) { refreshTargets(); refreshHistory(); refreshAdditions(); }
            refreshStatus();
        }
        page.addEventListener('viewshow', onViewShow);
        page.addEventListener('viewhide', function () {
            visible = false;
            generation++;
            invalidateSearch(); invalidateHistory();
            usersRequest++; usersLoading = false;
            inspectionRequest++; inspectionLoading = false;
            get('siphonInspection').setAttribute('aria-busy', 'false');
            invalidateRecovery(function () { return t('previewClearedAfterLeavingThePageGenerate'); });
            targetsRequest++; targetsLoading = false; targets = null;
            clearTimeout(statusTimer);
            window.removeEventListener('beforeunload', beforeUnload);
        });
        if (initiallyVisible) onViewShow();
    };

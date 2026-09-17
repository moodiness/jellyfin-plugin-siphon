/* Shared by the native settings page and the personal portal. No account data is persisted. */
(function (global) {
    'use strict';
    if (global.SiphonUi) return;
    var messages = { en: Object.create(null), fr: Object.create(null) };
    var current = 'en';
    var preferenceKey = 'Siphon.UiLanguage';
    var maximumErrorBytes = 16384;
    var own = Object.prototype.hasOwnProperty;

    function language(value) {
        if (typeof value !== 'string') return null;
        var primary = value.trim().replace('_', '-').split('-')[0].toLowerCase();
        return primary === 'en' || primary === 'fr' ? primary : null;
    }
    function init(options) {
        var chosen = language(options && options.locale);
        if (!chosen) {
            try { chosen = language(global.localStorage.getItem(preferenceKey)); } catch (_) { /* Storage can be disabled. */ }
        }
        if (!chosen) {
            var preferred = global.navigator.languages || [global.navigator.language];
            for (var index = 0; index < preferred.length && !chosen; index++) chosen = language(preferred[index]);
        }
        current = chosen || 'en';
        return current;
    }
    function setLocale(value) {
        var chosen = language(value);
        if (!chosen) return current;
        try { global.localStorage.setItem(preferenceKey, chosen); } catch (_) { /* In-memory language remains usable. */ }
        if (chosen !== current) {
            current = chosen;
            global.document.dispatchEvent(new CustomEvent('siphonlanguagechange', { detail: { locale: current } }));
        }
        return current;
    }
    function register(namespace, dictionaries) {
        if (!/^[a-z][a-z0-9-]{0,40}$/.test(namespace)) throw new TypeError('Invalid translation namespace.');
        ['en', 'fr'].forEach(function (locale) {
            var dictionary = dictionaries[locale] || {};
            Object.keys(dictionary).forEach(function (key) {
                if (typeof dictionary[key] !== 'string') throw new TypeError('Translation values must be text.');
                messages[locale][namespace + '.' + key] = dictionary[key];
            });
        });
    }
    function t(key, values, fallback) {
        var text = own.call(messages[current], key) ? messages[current][key]
            : own.call(messages.en, key) ? messages.en[key] : typeof fallback === 'string' ? fallback : key;
        return text.replace(/\{([A-Za-z0-9_]+)\}/g, function (match, name) {
            return values && own.call(values, name) ? String(values[name]) : match;
        });
    }
    function translate(root) {
        if (!root) return;
        var owner = root.nodeType === 9 ? root.documentElement : root;
        if (owner && owner.setAttribute) owner.setAttribute('lang', current);
        var attributes = [['data-i18n', null], ['data-i18n-placeholder', 'placeholder'], ['data-i18n-title', 'title'], ['data-i18n-label', 'aria-label']];
        attributes.forEach(function (pair) {
            var nodes = Array.from(root.querySelectorAll('[' + pair[0] + ']'));
            if (root.matches && root.matches('[' + pair[0] + ']')) nodes.unshift(root);
            nodes.forEach(function (node) {
                var value = t(node.getAttribute(pair[0]));
                if (pair[1]) node.setAttribute(pair[1], value);
                else node.textContent = value;
            });
        });
    }
    function safeText(value) {
        if (typeof value !== 'string' || !value.trim() || value.length > 500) return null;
        if (/[\u0000-\u001f\u007f-\u009f\u202a-\u202e\u2066-\u2069]/.test(value)
            || /(?:[a-z][a-z0-9+.-]*:\/\/|<\/?[a-z!]|bearer\s+|(?:authorization|password|secret|token|api[-_ ]?key)\s*[:=]\s*\S)/i.test(value)
            || /(?:[a-z0-9-]+\.)+[a-z]{2,}(?=[:\/\s]|$)|(?:^|\s)\/(?:[\w.-]+\/)+|[a-z]:\\|[A-Za-z0-9_+=/-]{48,}/i.test(value)) return null;
        return value.trim();
    }
    async function boundedBody(response) {
        if (!response || typeof response !== 'object') return null;
        if (response.headers && typeof response.headers.get === 'function') {
            var contentType = response.headers.get('Content-Type') || '';
            if (!/^application\/(?:[a-z0-9.+-]*\+)?json(?:\s*;|$)/i.test(contentType)) return null;
            var length = response.headers.get('Content-Length');
            if (length && (!/^\d+$/.test(length) || Number(length) > maximumErrorBytes)) return null;
            if (!response.body || typeof response.body.getReader !== 'function') return null;
            var reader = response.body.getReader();
            var decoder = new TextDecoder('utf-8', { fatal: true });
            var size = 0;
            var text = '';
            try {
                while (true) {
                    var part = await reader.read();
                    if (part.done) break;
                    size += part.value.byteLength;
                    if (size > maximumErrorBytes) { await reader.cancel(); return null; }
                    text += decoder.decode(part.value, { stream: true });
                }
                text += decoder.decode();
                return JSON.parse(text);
            } catch (_) {
                try { await reader.cancel(); } catch (_) { /* Preserve the original HTTP failure. */ }
                return null;
            } finally { reader.releaseLock(); }
        }
        // Jellyfin's API client may reject with a jqXHR-shaped value instead of Response.
        if (typeof response.responseText === 'string') {
            if (response.responseText.length > maximumErrorBytes || new TextEncoder().encode(response.responseText).byteLength > maximumErrorBytes) return null;
            try { return JSON.parse(response.responseText); } catch (_) { return null; }
        }
        if (response.responseJSON && typeof response.responseJSON === 'object' && !Array.isArray(response.responseJSON)) {
            // Do not reserialize an already parsed response: only the supported scalar fields can enter UI errors.
            var result = Object.create(null);
            var bytes = 0;
            var keys = ['Code', 'code', 'Message', 'message', 'Title', 'title'];
            for (var index = 0; index < keys.length; index++) {
                var key = keys[index];
                var value = response.responseJSON[key];
                if (typeof value !== 'string') continue;
                if (value.length > maximumErrorBytes) return null;
                bytes += new TextEncoder().encode(value).byteLength;
                if (bytes > maximumErrorBytes) return null;
                result[key] = value;
            }
            return result;
        }
        return null;
    }
    async function error(response, fallback) {
        if (response && response.siphonSafeError) return response;
        var status = response && Number.isInteger(response.status) ? response.status : 0;
        var result = new Error(fallback || t('common.RequestFailed'));
        result.status = status;
        result.code = null;
        result.safeMessage = null;
        result.siphonSafeError = true;
        var body = await boundedBody(response);
        if (body && typeof body === 'object' && !Array.isArray(body)) {
            var code = body.Code || body.code;
            if (typeof code === 'string' && /^[A-Za-z][A-Za-z0-9]{0,79}$/.test(code)) result.code = code;
            result.safeMessage = safeText(body.Message || body.message || body.Title || body.title);
        }
        result.message = errorText(result, fallback);
        return result;
    }
    function errorText(value, fallback) {
        // The authentication boundary always wins over arbitrary server wording.
        if (value && value.status === 401) return t('common.SessionExpired');
        if (value && value.code && own.call(messages.en, 'common.' + value.code)) return t('common.' + value.code);
        if (value && value.siphonSafeError && value.safeMessage) return value.safeMessage;
        var key = value && { 400: 'InvalidRequest', 403: 'PermissionDenied', 404: 'NotFound', 409: 'Conflict', 413: 'TooLarge', 429: 'RateLimited', 502: 'UpstreamUnavailable', 503: 'Unavailable', 504: 'TimedOut' }[value.status];
        return key ? t('common.' + key) : fallback || t('common.RequestFailed');
    }
    async function copyText(text) {
        if (typeof text !== 'string' || text.length > 16384 || !global.navigator.clipboard) return false;
        try { await global.navigator.clipboard.writeText(text); return true; } catch (_) { return false; }
    }

    register('common', {
        en: {
            RequestFailed: 'The request could not be completed. Check the connection and try again.',
            SessionExpired: 'Your session expired. Sign in again.',
            InvalidRequest: 'Check the selected values and try again.',
            PermissionDenied: 'Your current account is not allowed to perform this action.',
            NotFound: 'This item or saved selection is no longer available. Refresh the list.',
            Conflict: 'The state changed or another operation is active. Refresh before trying again.',
            TooLarge: 'The request exceeds the supported limit. Select fewer items.',
            RateLimited: 'Too many requests. Wait before trying again.',
            UpstreamUnavailable: 'The selected source did not return a supported response. Refresh its versions or choose another source.',
            Unavailable: 'The operation is temporarily unavailable. Check server health before trying again.',
            TimedOut: 'The operation timed out. Check the connection before trying again.',
            DownloadQuotaExceeded: 'The download storage or job limit has been reached. Remove retained downloads or ask the administrator to raise the limit.',
            DownloadPermissionDenied: 'Downloads are not allowed for this account or item.',
            DownloadSourceUnavailable: 'The selected version is no longer available. Refresh versions; another version will not be substituted.',
            DownloadSelectionUnavailable: 'The requested audio or subtitle selection is unavailable. Review the advertised tracks.',
            DownloadPreviewExpired: 'The batch preview expired or no longer belongs to this session. Create a new preview.',
            DownloadPriorityInvalid: 'Choose a priority between -10 and 10.',
            DownloadBusy: 'Wait for the current download operation to stop, then retry the action.',
            DownloadWindowClosed: 'The download is waiting for the next permitted UTC window.',
            DownloadInvalidRequest: 'Check the selected item, version and download options.',
            DownloadUnavailable: 'Downloads are temporarily unavailable. Check the server settings and storage.',
            DownloadFormatUnsupported: 'This format cannot be exported with the selected options. Choose a supported source.',
            HlsRequiresOfflinePreparation: 'Add this HLS source to the server download queue so it can be prepared before export.',
            WebhookSettingsInvalid: 'Check the destination, adapter, selected events and digest interval.',
            WebhookUnavailable: 'Enable notifications and configure an allowed destination before sending.',
            WebhookRateLimited: 'Wait at least one minute before sending another notification test.',
            WebhookDeliveryFailed: 'The destination did not accept the notification. Check its adapter and destination settings.',
            WebhookCapacityReached: 'Notification storage is full. Wait for pending deliveries or disable the destination to clear them.'
        },
        fr: {
            RequestFailed: 'La requête n’a pas abouti. Vérifiez la connexion puis réessayez.',
            SessionExpired: 'Votre session a expiré. Reconnectez-vous.',
            InvalidRequest: 'Vérifiez les valeurs sélectionnées puis réessayez.',
            PermissionDenied: 'Votre compte actuel n’est pas autorisé à effectuer cette action.',
            NotFound: 'Cet élément ou cette sélection n’est plus disponible. Actualisez la liste.',
            Conflict: 'L’état a changé ou une autre opération est en cours. Actualisez avant de réessayer.',
            TooLarge: 'La requête dépasse la limite autorisée. Sélectionnez moins d’éléments.',
            RateLimited: 'Trop de requêtes. Patientez avant de réessayer.',
            UpstreamUnavailable: 'La source sélectionnée n’a pas renvoyé de réponse compatible. Actualisez ses versions ou choisissez une autre source.',
            Unavailable: 'L’opération est temporairement indisponible. Vérifiez la santé du serveur avant de réessayer.',
            TimedOut: 'Le délai de l’opération a été dépassé. Vérifiez la connexion avant de réessayer.',
            DownloadQuotaExceeded: 'La limite de stockage ou de tâches est atteinte. Supprimez des téléchargements conservés ou demandez à l’administrateur d’augmenter la limite.',
            DownloadPermissionDenied: 'Le téléchargement n’est pas autorisé pour ce compte ou cet élément.',
            DownloadSourceUnavailable: 'La version sélectionnée n’est plus disponible. Actualisez les versions ; aucune autre ne sera choisie automatiquement.',
            DownloadSelectionUnavailable: 'La sélection audio ou de sous-titres demandée est indisponible. Vérifiez les pistes proposées.',
            DownloadPreviewExpired: 'L’aperçu groupé a expiré ou n’appartient plus à cette session. Créez un nouvel aperçu.',
            DownloadPriorityInvalid: 'Choisissez une priorité comprise entre -10 et 10.',
            DownloadBusy: 'Attendez l’arrêt de l’opération de téléchargement en cours, puis réessayez.',
            DownloadWindowClosed: 'Le téléchargement attend la prochaine plage horaire autorisée en UTC.',
            DownloadInvalidRequest: 'Vérifiez l’élément, la version et les options de téléchargement.',
            DownloadUnavailable: 'Les téléchargements sont temporairement indisponibles. Vérifiez les paramètres et le stockage du serveur.',
            DownloadFormatUnsupported: 'Ce format ne peut pas être exporté avec les options choisies. Sélectionnez une source compatible.',
            HlsRequiresOfflinePreparation: 'Ajoutez cette source HLS à la file de téléchargement du serveur pour la préparer avant l’export.',
            WebhookSettingsInvalid: 'Vérifiez la destination, l’adaptateur, les événements sélectionnés et l’intervalle de regroupement.',
            WebhookUnavailable: 'Activez les notifications et configurez une destination autorisée avant l’envoi.',
            WebhookRateLimited: 'Patientez au moins une minute avant d’envoyer une nouvelle notification de test.',
            WebhookDeliveryFailed: 'La destination n’a pas accepté la notification. Vérifiez son adaptateur et ses paramètres.',
            WebhookCapacityReached: 'Le stockage des notifications est plein. Attendez les envois en attente ou désactivez la destination pour les supprimer.'
        }
    });
    global.SiphonUi = Object.freeze({ register: register, init: init, setLocale: setLocale, locale: function () { return current; }, t: t,
        translate: translate, error: error, errorText: errorText, copyText: copyText });
    init();
}(window));

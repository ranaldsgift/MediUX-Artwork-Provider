(function () {
    'use strict';

    var MEDIUX_INJECTED_ATTR = 'data-mediux-injected';
    var PLUGIN_UNIQUE_ID = 'c8e4f2a1-9b3d-4e6f-a1c2-7d8e9f0a1b2c';
    var DEFAULT_SET_DOWNLOAD_CONCURRENCY = 6;
    var currentItemId = null;

    var setDownloadQueue = [];
    var setDownloadRunning = false;
    var setDownloadToastDismissed = false;
    var setDownloadToastHideTimer = null;
    var setDownloadToastAnimTimer = null;
    var setDownloadActiveJob = null;
    var setDownloadConcurrencyCache = null;
    var setDownloadConcurrencyPromise = null;

    function getApiHeaders() {
        var token = window.ApiClient && window.ApiClient.accessToken && window.ApiClient.accessToken();
        var headers = { 'Content-Type': 'application/json' };
        if (token) {
            headers['Authorization'] = 'MediaBrowser Token="' + token + '"';
        }
        return headers;
    }

    var ITEM_ID_RE = /^[0-9a-f]{32}$|^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
    var ITEM_ID_CAPTURE = '([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})';

    function isItemId(value) {
        return !!(value && ITEM_ID_RE.test(value));
    }

    function setCurrentItemId(itemId, dialog) {
        if (!isItemId(itemId)) {
            return false;
        }

        currentItemId = itemId;
        if (dialog) {
            dialog.setAttribute('data-mediux-item-id', itemId);
        }
        return true;
    }

    function captureItemIdFromUrl(url) {
        if (!url) {
            return;
        }

        var text = String(url);
        var match = text.match(new RegExp('/Users/' + ITEM_ID_CAPTURE + '/Items/' + ITEM_ID_CAPTURE, 'i'));
        if (match) {
            setCurrentItemId(match[2]);
            return;
        }

        match = text.match(new RegExp('/Items/' + ITEM_ID_CAPTURE + '(?:/|$|\\?)', 'i'));
        if (match) {
            setCurrentItemId(match[1]);
        }
    }

    function observeItemIdRequests() {
        if (window._mediuxItemIdObserver) {
            return;
        }

        window._mediuxItemIdObserver = true;

        function processResourceEntry(entry) {
            if (entry && entry.name) {
                captureItemIdFromUrl(entry.name);
            }
        }

        if (typeof PerformanceObserver !== 'undefined') {
            try {
                var perfObserver = new PerformanceObserver(function (list) {
                    list.getEntries().forEach(processResourceEntry);
                });
                perfObserver.observe({ type: 'resource', buffered: true });
            } catch (e) {
                // PerformanceObserver unavailable or resource type unsupported
            }
        }

        if (window.performance && performance.getEntriesByType) {
            performance.getEntriesByType('resource').forEach(processResourceEntry);
        }
    }

    function extractItemIdFromLocation() {
        var sources = [
            window.location.hash,
            window.location.href,
            window.location.search
        ];

        for (var i = 0; i < sources.length; i++) {
            var source = sources[i];
            if (!source) continue;

            var match = source.match(new RegExp('[?&]id=' + ITEM_ID_CAPTURE, 'i'));
            if (match) {
                return match[1];
            }

            match = source.match(new RegExp('/' + ITEM_ID_CAPTURE + '(?:[/?&#]|$)', 'i'));
            if (match) {
                return match[1];
            }
        }

        return null;
    }

    function extractItemIdFromDom(dialog) {
        var roots = [dialog];

        if (dialog && dialog.parentElement) {
            roots.push(dialog.parentElement);
        }

        var openDialogs = document.querySelectorAll('.dialog.opened, .dialog[modal="modal"]');
        for (var d = 0; d < openDialogs.length; d++) {
            roots.push(openDialogs[d]);
        }

        var itemDetails = document.querySelector('.itemDetailsPage, .detailPageWrapper, .page');
        if (itemDetails) {
            roots.push(itemDetails);
        }

        for (var r = 0; r < roots.length; r++) {
            var root = roots[r];
            if (!root || !root.querySelectorAll) continue;

            var cards = root.querySelectorAll('[data-id]');
            for (var c = 0; c < cards.length; c++) {
                var id = cards[c].getAttribute('data-id');
                if (isItemId(id)) {
                    return id;
                }
            }
        }

        return null;
    }

    function tryCaptureItemId(dialog) {
        if (dialog && dialog.getAttribute('data-mediux-item-id')
            && dialog.getAttribute('data-mediux-sets-eligible') === 'true') {
            currentItemId = dialog.getAttribute('data-mediux-item-id');
            return currentItemId;
        }

        var fromDom = extractItemIdFromDom(dialog);
        if (fromDom) {
            return fromDom;
        }

        var fromLocation = extractItemIdFromLocation();
        if (fromLocation) {
            return fromLocation;
        }

        if (currentItemId) {
            return currentItemId;
        }

        return null;
    }

    /**
     * Resolve a captured Jellyfin item id to a Movie/Series id for MediUX sets.
     * Season/Episode → SeriesId; other types are not MediUX-valid.
     */
    function resolveMediuxBaseItem(rawItemId) {
        if (!rawItemId || !isItemId(rawItemId) || !window.ApiClient) {
            return Promise.resolve({ valid: false });
        }

        var userId = window.ApiClient.getCurrentUserId && window.ApiClient.getCurrentUserId();
        if (!userId) {
            return Promise.resolve({ valid: false });
        }

        return window.ApiClient.getItem(userId, rawItemId).then(function (item) {
            if (!item || !item.Type) {
                return { valid: false };
            }

            var type = item.Type;
            if (type === 'Movie' || type === 'Series') {
                return { valid: true, baseId: item.Id, mediaType: type };
            }

            if ((type === 'Season' || type === 'Episode') && isItemId(item.SeriesId)) {
                return { valid: true, baseId: item.SeriesId, mediaType: 'Series' };
            }

            return { valid: false };
        }).catch(function () {
            return { valid: false };
        });
    }

    function setFanartSetsOptionEnabled(dialog, enabled) {
        var select = dialog.querySelector('#selectMediuxBrowseBy');
        if (!select) {
            return;
        }

        var option = select.querySelector('option[value="fanartSets"]');
        if (option) {
            option.disabled = !enabled;
        }

        if (!enabled && select.value === 'fanartSets') {
            select.value = 'imageType';
            setBrowseMode(dialog, 'imageType');
        }
    }

    function applyMediuxBaseResult(dialog, result) {
        if (result && result.valid && result.baseId) {
            setCurrentItemId(result.baseId, dialog);
            dialog.setAttribute('data-mediux-sets-eligible', 'true');
            dialog.setAttribute('data-mediux-media-type', result.mediaType || '');
            setFanartSetsOptionEnabled(dialog, true);
            return;
        }

        dialog.setAttribute('data-mediux-sets-eligible', 'false');
        dialog.removeAttribute('data-mediux-media-type');
        setFanartSetsOptionEnabled(dialog, false);
    }

    function resolveMediuxBaseForDialog(dialog, attempt) {
        attempt = attempt || 0;

        if (dialog.getAttribute('data-mediux-sets-eligible') === 'true'
            || dialog.getAttribute('data-mediux-sets-eligible') === 'false') {
            return;
        }

        var rawId = tryCaptureItemId(dialog);
        if (!rawId) {
            if (attempt < 10) {
                setTimeout(function () {
                    resolveMediuxBaseForDialog(dialog, attempt + 1);
                }, 200);
                return;
            }

            applyMediuxBaseResult(dialog, { valid: false });
            return;
        }

        dialog.setAttribute('data-mediux-sets-eligible', 'pending');

        resolveMediuxBaseItem(rawId).then(function (result) {
            applyMediuxBaseResult(dialog, result);
        });
    }

    function getSlotLabel(img) {
        if (img.slotKind === 'Primary') return 'Poster';
        if (img.slotKind === 'Backdrop') return 'Backdrop';
        if (img.slotKind === 'Logo') return 'Logo';
        if (img.slotKind === 'AlbumArt') return 'Album Art';
        if (img.slotKind === 'SeasonPrimary') return 'S' + (img.seasonNumber != null ? img.seasonNumber : '?') + ' Poster';
        if (img.slotKind === 'EpisodeTitleCard') return 'S' + (img.seasonNumber != null ? img.seasonNumber : '?') + 'E' + (img.episodeNumber != null ? img.episodeNumber : '?');
        return img.slotKind;
    }

    function getImageType(img) {
        if (img.slotKind === 'Primary' || img.slotKind === 'SeasonPrimary' || img.slotKind === 'EpisodeTitleCard') return 'Primary';
        if (img.slotKind === 'Backdrop') return 'Backdrop';
        if (img.slotKind === 'Logo') return 'Logo';
        if (img.slotKind === 'AlbumArt') return 'Box';
        return 'Primary';
    }

    function episodeKey(seasonNumber, episodeNumber) {
        return String(seasonNumber) + '-' + String(episodeNumber);
    }

    var showChildrenCache = {};

    function fetchJson(url) {
        return fetch(url, { headers: getApiHeaders() }).then(function (resp) {
            if (!resp.ok) {
                throw new Error('Request failed');
            }
            return resp.json();
        });
    }

    function loadShowChildren(seriesId) {
        if (!seriesId || !window.ApiClient) {
            return Promise.resolve({ seasons: {}, episodes: {} });
        }

        if (showChildrenCache[seriesId]) {
            return showChildrenCache[seriesId];
        }

        var seasonsUrl = window.ApiClient.getUrl('Shows/' + seriesId + '/Seasons');
        var episodesUrl = window.ApiClient.getUrl('Shows/' + seriesId + '/Episodes');

        var promise = Promise.all([
            fetchJson(seasonsUrl).catch(function () { return { Items: [] }; }),
            fetchJson(episodesUrl).catch(function () { return { Items: [] }; })
        ]).then(function (results) {
            var seasonsPayload = results[0];
            var episodesPayload = results[1];
            var seasons = {};
            var episodes = {};

            var seasonItems = seasonsPayload.Items || seasonsPayload.items || (Array.isArray(seasonsPayload) ? seasonsPayload : []);
            for (var i = 0; i < seasonItems.length; i++) {
                var season = seasonItems[i];
                if (season && season.Id != null && season.IndexNumber != null) {
                    seasons[season.IndexNumber] = season.Id;
                }
            }

            var episodeItems = episodesPayload.Items || episodesPayload.items || (Array.isArray(episodesPayload) ? episodesPayload : []);
            for (var j = 0; j < episodeItems.length; j++) {
                var episode = episodeItems[j];
                if (episode && episode.Id != null && episode.IndexNumber != null) {
                    var parentSeason = episode.ParentIndexNumber != null ? episode.ParentIndexNumber : episode.SeasonIndexNumber;
                    if (parentSeason != null) {
                        episodes[episodeKey(parentSeason, episode.IndexNumber)] = episode.Id;
                    }
                }
            }

            return { seasons: seasons, episodes: episodes };
        });

        showChildrenCache[seriesId] = promise;
        return promise;
    }

    function needsShowChildren(images) {
        for (var i = 0; i < images.length; i++) {
            var kind = images[i].slotKind;
            if (kind === 'SeasonPrimary' || kind === 'EpisodeTitleCard') {
                return true;
            }
        }
        return false;
    }

    function resolveDownloadTarget(parentItemId, img, children) {
        var type = getImageType(img);

        if (img.slotKind === 'SeasonPrimary') {
            var seasonId = children && children.seasons ? children.seasons[img.seasonNumber] : null;
            if (!seasonId) {
                return null;
            }
            return { itemId: seasonId, type: type };
        }

        if (img.slotKind === 'EpisodeTitleCard') {
            var epId = children && children.episodes
                ? children.episodes[episodeKey(img.seasonNumber, img.episodeNumber)]
                : null;
            if (!epId) {
                return null;
            }
            return { itemId: epId, type: type };
        }

        return { itemId: parentItemId, type: type };
    }

    function sortImages(images) {
        var order = { Primary: 0, Backdrop: 1, Logo: 2, AlbumArt: 3, SeasonPrimary: 4, EpisodeTitleCard: 5 };
        return images.slice().sort(function (a, b) {
            var oa = order[a.slotKind] != null ? order[a.slotKind] : 99;
            var ob = order[b.slotKind] != null ? order[b.slotKind] : 99;
            if (oa !== ob) return oa - ob;
            if ((a.seasonNumber || 0) !== (b.seasonNumber || 0)) return (a.seasonNumber || 0) - (b.seasonNumber || 0);
            return (a.episodeNumber || 0) - (b.episodeNumber || 0);
        });
    }

    var previewObserver = null;
    var previewBlobCache = {};
    var previewInflight = {};
    var previewBlobUrls = [];

    function resolvePreviewUrl(previewPath) {
        if (!previewPath || !window.ApiClient) {
            return null;
        }

        if (previewPath.indexOf('http://') === 0 || previewPath.indexOf('https://') === 0) {
            return previewPath;
        }

        if (previewPath.indexOf('/') === 0) {
            var base = window.ApiClient.serverAddress ? window.ApiClient.serverAddress() : '';
            return base + previewPath;
        }

        return window.ApiClient.getUrl(previewPath);
    }

    function loadMediuxPreview(previewPath) {
        var url = resolvePreviewUrl(previewPath);
        if (!url) {
            return Promise.reject(new Error('No preview URL'));
        }

        if (previewBlobCache[url]) {
            return Promise.resolve(previewBlobCache[url]);
        }

        if (previewInflight[url]) {
            return previewInflight[url];
        }

        var promise = fetch(url, { headers: getApiHeaders() })
            .then(function (resp) {
                if (!resp.ok) {
                    throw new Error('Preview failed');
                }

                return resp.blob();
            })
            .then(function (blob) {
                var blobUrl = URL.createObjectURL(blob);
                previewBlobCache[url] = blobUrl;
                previewBlobUrls.push(blobUrl);
                delete previewInflight[url];
                return blobUrl;
            })
            .catch(function (err) {
                delete previewInflight[url];
                throw err;
            });

        previewInflight[url] = promise;
        return promise;
    }

    function revokePreviewBlobs() {
        previewBlobUrls.forEach(function (blobUrl) {
            try {
                URL.revokeObjectURL(blobUrl);
            } catch (e) {
                // ignore revoke errors
            }
        });

        previewBlobUrls = [];
        previewBlobCache = {};
        previewInflight = {};
    }

    function getImagePreviewPath(img) {
        if (window.ApiClient && img.assetId && img.version) {
            return window.ApiClient.getUrl('MediUX/Preview', {
                assetId: img.assetId,
                v: img.version,
                w: img.previewWidth || 240
            });
        }

        if (img.previewUrl) {
            return resolvePreviewUrl(img.previewUrl.charAt(0) === '/' ? img.previewUrl : '/' + img.previewUrl);
        }

        return null;
    }

    function markPreviewLoading(el) {
        var card = el.closest ? el.closest('.mediux-img-card') : null;
        if (card) {
            card.classList.add('mediux-preview-loading');
            return;
        }

        el.classList.add('mediux-preview-loading');
    }

    function markPreviewReady(el) {
        var card = el.closest ? el.closest('.mediux-img-card') : null;
        if (card) {
            card.classList.remove('mediux-preview-loading', 'mediux-preview-failed');
            card.classList.add('mediux-preview-ready');
            return;
        }

        el.classList.remove('mediux-preview-loading', 'mediux-preview-failed');
        el.classList.add('mediux-preview-ready');
    }

    function markPreviewFailed(el) {
        var card = el.closest ? el.closest('.mediux-img-card') : null;
        if (card) {
            card.classList.remove('mediux-preview-loading');
            card.classList.add('mediux-preview-failed');
            return;
        }

        el.classList.remove('mediux-preview-loading');
        el.classList.add('mediux-preview-failed');
    }

    function loadPreviewIntoImg(imgEl, previewPath) {
        markPreviewLoading(imgEl);

        loadMediuxPreview(previewPath)
            .then(function (blobUrl) {
                imgEl.onload = function () {
                    markPreviewReady(imgEl);
                };
                imgEl.onerror = function () {
                    markPreviewFailed(imgEl);
                };
                imgEl.src = blobUrl;
            })
            .catch(function () {
                markPreviewFailed(imgEl);
            });
    }

    function getPreviewObserver() {
        if (previewObserver || typeof IntersectionObserver === 'undefined') {
            return previewObserver;
        }

        previewObserver = new IntersectionObserver(function (entries) {
            entries.forEach(function (entry) {
                if (!entry.isIntersecting) {
                    return;
                }

                var imgEl = entry.target;
                var previewPath = imgEl.getAttribute('data-mediux-preview');
                previewObserver.unobserve(imgEl);

                if (previewPath && !imgEl.getAttribute('src')) {
                    loadPreviewIntoImg(imgEl, previewPath);
                }
            });
        }, { rootMargin: '100px' });

        return previewObserver;
    }

    function attachPreviewImage(imgEl, img) {
        var previewPath = getImagePreviewPath(img);
        if (!previewPath) {
            imgEl.src = img.url;
            return;
        }

        var observer = getPreviewObserver();
        if (observer) {
            imgEl.setAttribute('data-mediux-preview', previewPath);
            markPreviewLoading(imgEl);
            observer.observe(imgEl);
            return;
        }

        loadPreviewIntoImg(imgEl, previewPath);
    }

    function downloadImage(itemId, url, type, provider) {
        return window.ApiClient.ajax({
            type: 'POST',
            url: window.ApiClient.getUrl('Items/' + itemId + '/RemoteImages/Download', {
                Type: type,
                ImageUrl: url,
                ProviderName: provider
            })
        });
    }

    function downloadMediuxImage(parentItemId, img, children) {
        var target = resolveDownloadTarget(parentItemId, img, children);
        if (!target) {
            return Promise.reject(new Error('No matching Jellyfin item for ' + (img.slotKind || 'image')));
        }

        return downloadImage(target.itemId, img.url, target.type, 'MediUX');
    }

    function getImageBindingKind(img) {
        if (img.slotKind === 'Primary') return 'poster';
        if (img.slotKind === 'Backdrop') return 'backdrop';
        if (img.slotKind === 'Logo') return 'logo';
        if (img.slotKind === 'AlbumArt') return 'albumArt';
        if (img.slotKind === 'EpisodeTitleCard') return 'titlecards';
        if (img.slotKind === 'SeasonPrimary') {
            return Number(img.seasonNumber) === 0 ? 'specialsPoster' : 'seasonPosters';
        }
        return null;
    }

    function isMultiItemBindingKind(kind) {
        return kind === 'seasonPosters' || kind === 'titlecards';
    }

    function getProviderKey(itemId) {
        if (!itemId || !window.ApiClient) {
            return Promise.resolve(null);
        }

        return fetch(window.ApiClient.getUrl('MediUX/ProviderKey', { itemId: itemId }), { headers: getApiHeaders() })
            .then(function (resp) {
                if (!resp.ok) {
                    return null;
                }
                return resp.json();
            })
            .then(function (data) {
                return data && data.providerKey ? data.providerKey : null;
            })
            .catch(function () {
                return null;
            });
    }

    function fetchSetBindings(itemId) {
        if (!itemId || !window.ApiClient) {
            return Promise.resolve(null);
        }

        return fetch(window.ApiClient.getUrl('MediUX/SetBindings', { itemId: itemId }), { headers: getApiHeaders() })
            .then(function (resp) {
                if (!resp.ok) {
                    return null;
                }
                return resp.json();
            })
            .catch(function () {
                return null;
            });
    }

    function clampConcurrency(value) {
        var n = parseInt(value, 10);
        if (!isFinite(n) || isNaN(n)) {
            return DEFAULT_SET_DOWNLOAD_CONCURRENCY;
        }
        if (n < 1) {
            return 1;
        }
        if (n > 16) {
            return 16;
        }
        return n;
    }

    function getSetDownloadConcurrency() {
        if (setDownloadConcurrencyCache != null) {
            return Promise.resolve(setDownloadConcurrencyCache);
        }

        if (setDownloadConcurrencyPromise) {
            return setDownloadConcurrencyPromise;
        }

        if (!window.ApiClient || !ApiClient.getPluginConfiguration) {
            setDownloadConcurrencyCache = DEFAULT_SET_DOWNLOAD_CONCURRENCY;
            return Promise.resolve(setDownloadConcurrencyCache);
        }

        setDownloadConcurrencyPromise = ApiClient.getPluginConfiguration(PLUGIN_UNIQUE_ID).then(function (config) {
            setDownloadConcurrencyCache = clampConcurrency(config && config.SetDownloadConcurrency);
            return setDownloadConcurrencyCache;
        }).catch(function () {
            setDownloadConcurrencyCache = DEFAULT_SET_DOWNLOAD_CONCURRENCY;
            return setDownloadConcurrencyCache;
        }).then(function (value) {
            setDownloadConcurrencyPromise = null;
            return value;
        });

        return setDownloadConcurrencyPromise;
    }

    function postSetBindings(providerKey, updates) {
        if (!providerKey || !updates || !window.ApiClient) {
            return Promise.resolve();
        }

        var body = { providerKey: providerKey };
        var hasAny = false;
        Object.keys(updates).forEach(function (key) {
            if (updates[key]) {
                body[key] = updates[key];
                hasAny = true;
            }
        });

        if (!hasAny) {
            return Promise.resolve();
        }

        return fetch(window.ApiClient.getUrl('MediUX/SetBindings'), {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify(body)
        }).catch(function () {
            // Binding persistence is best-effort
        });
    }

    function updateBindingsForImages(itemId, setId, images, allowMultiItemKinds) {
        if (!setId || !images || !images.length) {
            return Promise.resolve();
        }

        var updates = {};
        images.forEach(function (img) {
            var kind = getImageBindingKind(img);
            if (!kind) {
                return;
            }
            if (!allowMultiItemKinds && isMultiItemBindingKind(kind)) {
                return;
            }
            updates[kind] = setId;
        });

        return getProviderKey(itemId).then(function (providerKey) {
            return postSetBindings(providerKey, updates);
        }).then(function () {
            refreshBindingsBannerForItem(itemId);
        });
    }

    function getDialogHelper() {
        return new Promise(function (resolve) {
            if (window.Dashboard && Dashboard.dialogHelper) {
                resolve(Dashboard.dialogHelper);
                return;
            }

            if (typeof require === 'function') {
                try {
                    require(['components/dialogHelper/dialogHelper'], function (dialogHelper) {
                        resolve(dialogHelper || null);
                    }, function () {
                        resolve(null);
                    });
                    return;
                } catch (e) {
                    resolve(null);
                    return;
                }
            }

            resolve(null);
        });
    }

    function buildDownloadKindOptions(images) {
        var counts = {
            poster: 0,
            seasonPosters: 0,
            specialsPoster: 0,
            backdrop: 0,
            titlecards: 0,
            albumArt: 0,
            logo: 0
        };

        (images || []).forEach(function (img) {
            var kind = getImageBindingKind(img);
            if (kind && counts[kind] != null) {
                counts[kind]++;
            }
        });

        var labels = {
            poster: 'Poster',
            seasonPosters: 'Season Posters',
            specialsPoster: 'Specials Poster',
            backdrop: 'Backdrop',
            titlecards: 'Titlecards',
            albumArt: 'Album Art',
            logo: 'Logo'
        };

        var order = ['poster', 'seasonPosters', 'specialsPoster', 'backdrop', 'titlecards', 'albumArt', 'logo'];
        var options = [];
        order.forEach(function (key) {
            if (counts[key] > 0) {
                options.push({ key: key, label: labels[key], count: counts[key] });
            }
        });
        return options;
    }

    function filterImagesByBindingKinds(images, selectedKeys) {
        var allowed = {};
        (selectedKeys || []).forEach(function (key) {
            allowed[key] = true;
        });

        return (images || []).filter(function (img) {
            var kind = getImageBindingKind(img);
            return kind && allowed[kind];
        });
    }

    function clearToastHideTimer() {
        if (setDownloadToastHideTimer) {
            clearTimeout(setDownloadToastHideTimer);
            setDownloadToastHideTimer = null;
        }
    }

    function clearToastAnimTimer() {
        if (setDownloadToastAnimTimer) {
            clearTimeout(setDownloadToastAnimTimer);
            setDownloadToastAnimTimer = null;
        }
    }

    function hideDownloadToastElement(toast) {
        if (!toast) {
            return;
        }

        clearToastAnimTimer();
        clearToastHideTimer();
        toast.classList.remove('toastVisible');
        toast.classList.add('toastHide');

        setDownloadToastAnimTimer = setTimeout(function () {
            toast.style.display = 'none';
            toast.classList.remove('toastHide');
            setDownloadToastAnimTimer = null;
            console.debug('[MediUX] toast hidden');
        }, 300);
    }

    function showDownloadToastElement(toast) {
        if (!toast) {
            return;
        }

        clearToastAnimTimer();
        clearToastHideTimer();
        toast.classList.remove('toastHide');
        toast.style.display = '';

        // Already on-screen — update content only, do not re-fly-in.
        if (toast.classList.contains('toastVisible')) {
            return;
        }

        // Match native toast.js: brief delay, then toastVisible for fly-up.
        setDownloadToastAnimTimer = setTimeout(function () {
            toast.classList.add('toastVisible');
            setDownloadToastAnimTimer = null;
            console.debug('[MediUX] toast shown');
        }, 300);
    }

    function ensureToastContainer() {
        var container = document.querySelector('.toastContainer');
        if (!container) {
            container = document.createElement('div');
            container.className = 'toastContainer mediux-toast-container';
            document.body.appendChild(container);
        }
        return container;
    }

    function getOrCreateDownloadToast() {
        var existing = document.getElementById('mediux-set-download-toast');
        if (existing) {
            return existing;
        }

        var toast = document.createElement('div');
        toast.id = 'mediux-set-download-toast';
        toast.className = 'toast mediux-set-download-toast';
        toast.setAttribute('role', 'status');
        toast.style.display = 'none';

        toast.innerHTML = [
            '<div class="mediux-toast-line">',
            '  <span class="mediux-toast-progress"></span>',
            '  <button type="button" is="paper-icon-button-light" class="mediux-toast-cancel paper-icon-button-light autoSize" title="Cancel">',
            '    <span class="material-icons cancel" aria-hidden="true"></span>',
            '  </button>',
            '</div>',
            '<div class="mediux-toast-queue"></div>',
            '<div class="mediux-toast-actions">',
            '  <button type="button" class="mediux-toast-link mediux-toast-dismiss">Dismiss</button>',
            '</div>'
        ].join('');

        toast.querySelector('.mediux-toast-cancel').addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            console.debug('[MediUX] toast cancel clicked');
            if (setDownloadActiveJob) {
                setDownloadActiveJob.cancelRequested = true;
                updateDownloadToast();
            }
        });

        toast.querySelector('.mediux-toast-dismiss').addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            setDownloadToastDismissed = true;
            hideDownloadToastElement(toast);
        });

        ensureToastContainer().appendChild(toast);
        console.debug('[MediUX] toast element created');
        return toast;
    }

    function updateDownloadToast() {
        var toast = getOrCreateDownloadToast();
        var job = setDownloadActiveJob;
        var queued = setDownloadQueue.length;
        var progressEl = toast.querySelector('.mediux-toast-progress');
        var queueEl = toast.querySelector('.mediux-toast-queue');
        var cancelBtn = toast.querySelector('.mediux-toast-cancel');

        clearToastHideTimer();

        if (!job && queued === 0) {
            if (progressEl) {
                progressEl.textContent = 'Set downloads finished.';
            }
            if (queueEl) {
                queueEl.textContent = '';
                queueEl.style.display = 'none';
            }
            if (cancelBtn) {
                cancelBtn.style.display = 'none';
            }

            if (!setDownloadToastDismissed) {
                showDownloadToastElement(toast);
                setDownloadToastHideTimer = setTimeout(function () {
                    hideDownloadToastElement(toast);
                    setDownloadToastHideTimer = null;
                }, 2500);
            }
            return;
        }

        if (!job) {
            return;
        }

        var setName = job.setTitle || 'MediUX';
        var done = job.done || 0;
        var total = job.total || 0;
        var suffix = job.errors > 0 ? ' (' + job.errors + ' failed)' : '';
        if (job.cancelRequested && done < total) {
            suffix = ' (cancelling…)';
        }

        if (progressEl) {
            progressEl.textContent = 'Downloaded ' + done + '/' + total + ' images from the ' + setName + '.' + suffix;
        }

        if (queueEl) {
            if (queued > 0) {
                queueEl.style.display = '';
                queueEl.textContent = queued + ' more set' + (queued === 1 ? '' : 's') + ' queued for download.';
            } else {
                queueEl.textContent = '';
                queueEl.style.display = 'none';
            }
        }

        if (cancelBtn) {
            cancelBtn.style.display = job.cancelRequested ? 'none' : '';
        }

        if (setDownloadToastDismissed) {
            return;
        }

        showDownloadToastElement(toast);
    }

    function showDownloadToast() {
        setDownloadToastDismissed = false;
        console.debug('[MediUX] showDownloadToast', {
            active: !!(setDownloadActiveJob && setDownloadActiveJob.setTitle),
            queued: setDownloadQueue.length
        });
        updateDownloadToast();
    }

    function enqueueSetDownload(job) {
        if (!job || !job.images || !job.images.length) {
            console.debug('[MediUX] enqueueSetDownload skipped (no images)', job && job.setTitle);
            return;
        }

        console.debug('[MediUX] enqueueSetDownload', job.setTitle, job.images.length, 'images');
        setDownloadQueue.push({
            setId: job.setId,
            setTitle: job.setTitle || 'MediUX',
            itemId: job.itemId,
            images: job.images.slice(),
            cancelRequested: false,
            done: 0,
            total: job.images.length,
            errors: 0
        });

        showDownloadToast();
        processSetDownloadQueue();
    }

    function processSetDownloadQueue() {
        if (setDownloadRunning) {
            updateDownloadToast();
            return;
        }

        var next = setDownloadQueue.shift();
        if (!next) {
            setDownloadActiveJob = null;
            updateDownloadToast();
            return;
        }

        console.debug('[MediUX] starting set download job', next.setTitle, next.total);
        setDownloadRunning = true;
        setDownloadActiveJob = next;
        showDownloadToast();

        runSetDownloadJob(next).then(function (result) {
            var completed = result && result.completed;
            console.debug('[MediUX] set download job finished', next.setTitle, 'completed=', !!completed);
            if (completed) {
                return updateBindingsForImages(next.itemId, next.setId, next.images, true);
            }
            return null;
        }).catch(function (err) {
            console.debug('[MediUX] set download job error', err);
        }).then(function () {
            setDownloadRunning = false;
            setDownloadActiveJob = null;
            updateDownloadToast();
            processSetDownloadQueue();
        });
    }

    function runSetDownloadJob(job) {
        return getSetDownloadConcurrency().then(function (concurrency) {
            console.debug('[MediUX] runSetDownloadJob concurrency=', concurrency);
            return ensureChildrenForImages(job.itemId, job.images).then(function (children) {
                var images = job.images;
                var index = 0;

                function runNextBatch() {
                    if (job.cancelRequested || index >= images.length) {
                        return Promise.resolve();
                    }

                    var batch = [];
                    while (batch.length < concurrency && index < images.length) {
                        if (job.cancelRequested) {
                            break;
                        }
                        (function (img) {
                            batch.push(
                                downloadMediuxImage(job.itemId, img, children).then(function () {
                                    job.done++;
                                    updateDownloadToast();
                                }).catch(function () {
                                    job.done++;
                                    job.errors++;
                                    updateDownloadToast();
                                })
                            );
                        })(images[index]);
                        index++;
                    }

                    if (!batch.length) {
                        return Promise.resolve();
                    }

                    return Promise.all(batch).then(runNextBatch);
                }

                updateDownloadToast();
                return runNextBatch().then(function () {
                    return { completed: !job.cancelRequested };
                });
            });
        });
    }

    function showDownloadSetDialog(set, itemId) {
        var images = set.images || [];
        var options = buildDownloadKindOptions(images);
        if (!options.length) {
            return Promise.resolve();
        }

        return getDialogHelper().then(function (dialogHelper) {
            if (!dialogHelper) {
                enqueueSetDownload({
                    setId: set.setId,
                    setTitle: set.setTitle,
                    itemId: itemId,
                    images: images
                });
                return;
            }

            var dlg = dialogHelper.createDialog({
                removeOnClose: true,
                scrollY: false
            });
            dlg.classList.add('ui-body-a', 'background-theme-a', 'formDialog', 'centeredDialog', 'mediux-download-set-dialog');

            var checksHtml = options.map(function (opt) {
                return [
                    '<label class="checkboxContainer emby-checkbox-label">',
                    '  <input type="checkbox" is="emby-checkbox" class="chkMediuxDownloadKind" data-kind="' + opt.key + '" checked />',
                    '  <span>' + opt.label + ' (' + opt.count + ')</span>',
                    '</label>'
                ].join('');
            }).join('');

            dlg.innerHTML = [
                '<div class="mediux-download-set-content" style="margin:0;padding:1.25em 1.5em 1.5em;">',
                '  <h2 style="margin:0 0 0.75em;">Download Set</h2>',
                checksHtml,
                '  <div style="display:flex;gap:0.75em;justify-content:flex-end;margin-top:1.25em;">',
                '    <button is="emby-button" type="button" class="btnCancel raised button-cancel">Cancel</button>',
                '    <button is="emby-button" type="button" class="btnConfirm raised button-submit">Download</button>',
                '  </div>',
                '</div>'
            ].join('');

            return new Promise(function (resolve) {
                var settled = false;

                function finish() {
                    if (settled) {
                        return;
                    }
                    settled = true;
                    resolve();
                }

                function closeDialog() {
                    dialogHelper.close(dlg);
                }

                // Backdrop click / Esc / removeOnClose all fire close — re-enable Download Set.
                dlg.addEventListener('close', finish);
                dlg.addEventListener('closed', finish);

                dlg.querySelector('.btnCancel').addEventListener('click', function () {
                    closeDialog();
                });

                dlg.querySelector('.btnConfirm').addEventListener('click', function () {
                    var selected = [];
                    dlg.querySelectorAll('.chkMediuxDownloadKind').forEach(function (chk) {
                        if (chk.checked) {
                            selected.push(chk.getAttribute('data-kind'));
                        }
                    });

                    if (selected.length) {
                        var filtered = filterImagesByBindingKinds(images, selected);
                        enqueueSetDownload({
                            setId: set.setId,
                            setTitle: set.setTitle,
                            itemId: itemId,
                            images: filtered
                        });
                    }

                    closeDialog();
                });

                var openResult = dialogHelper.open(dlg);
                if (openResult && typeof openResult.then === 'function') {
                    openResult.then(finish, finish);
                }
            });
        });
    }

    function ensureChildrenForImages(parentItemId, images) {
        if (!needsShowChildren(images)) {
            return Promise.resolve({ seasons: {}, episodes: {} });
        }

        return loadShowChildren(parentItemId);
    }

    function buildSetLookup(sets) {
        var map = {};
        (sets || []).forEach(function (set) {
            if (set && set.setId) {
                map[String(set.setId)] = set;
            }
        });
        return map;
    }

    function createBindingSlotCell(label, setId, setLookup) {
        var cell = document.createElement('div');
        cell.className = 'mediux-bindings-cell';

        var labelEl = document.createElement('span');
        labelEl.className = 'mediux-bindings-label';
        labelEl.textContent = label + ': ';
        cell.appendChild(labelEl);

        if (!setId) {
            var noneEl = document.createElement('span');
            noneEl.className = 'mediux-bindings-none';
            noneEl.textContent = 'None';
            cell.appendChild(noneEl);
            return cell;
        }

        var set = setLookup[String(setId)];
        var author = set && set.username ? set.username : ('Set ' + setId);
        var setTitle = set && set.setTitle ? set.setTitle : '';

        var link = document.createElement('a');
        link.className = 'mediux-bindings-author';
        link.href = 'https://mediux.pro/sets/' + encodeURIComponent(setId);
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.textContent = author;
        if (setTitle) {
            link.title = setTitle;
        }
        cell.appendChild(link);
        return cell;
    }

    function renderBindingsBanner(banner, bindings, sets, mediaType) {
        banner.innerHTML = '';
        banner.className = 'mediux-bindings-banner';

        var heading = document.createElement('div');
        heading.className = 'mediux-bindings-heading';
        heading.textContent = 'Selected Fanart Sets:';
        banner.appendChild(heading);

        var lookup = buildSetLookup(sets);
        var isSeries = mediaType === 'Series';

        var row1 = document.createElement('div');
        row1.className = 'mediux-bindings-row';
        row1.appendChild(createBindingSlotCell('Poster', bindings && bindings.poster, lookup));
        row1.appendChild(createBindingSlotCell('Backdrop', bindings && bindings.backdrop, lookup));
        row1.appendChild(createBindingSlotCell('Logo', bindings && bindings.logo, lookup));
        row1.appendChild(createBindingSlotCell('Album Art', bindings && bindings.albumArt, lookup));
        banner.appendChild(row1);

        if (isSeries) {
            var row2 = document.createElement('div');
            row2.className = 'mediux-bindings-row';
            row2.appendChild(createBindingSlotCell('Season Poster', bindings && bindings.seasonPosters, lookup));
            row2.appendChild(createBindingSlotCell('Specials Poster', bindings && bindings.specialsPoster, lookup));
            row2.appendChild(createBindingSlotCell('Titlecards', bindings && bindings.titlecards, lookup));
            banner.appendChild(row2);
        }
    }

    function refreshBindingsBannerForItem(itemId) {
        if (!itemId) {
            return;
        }

        var panels = document.querySelectorAll('.mediux-setbrowser-panel[data-mediux-loaded="true"]');
        for (var i = 0; i < panels.length; i++) {
            (function (panel) {
                if (panel.getAttribute('data-mediux-item-id') !== String(itemId)) {
                    return;
                }

                var banner = panel.querySelector('.mediux-bindings-banner');
                if (!banner) {
                    return;
                }

                var sets = [];
                try {
                    sets = JSON.parse(panel.getAttribute('data-mediux-sets-json') || '[]');
                } catch (e) {
                    sets = [];
                }

                var mediaType = panel.getAttribute('data-mediux-media-type') || '';
                fetchSetBindings(itemId).then(function (bindings) {
                    renderBindingsBanner(banner, bindings, sets, mediaType);
                });
            })(panels[i]);
        }
    }

    function injectStyles() {
        if (document.getElementById('mediux-setbrowser-styles')) return;
        var style = document.createElement('style');
        style.id = 'mediux-setbrowser-styles';
        style.textContent = [
            '.mediux-setbrowser-panel { display: none; padding: 0 0 1em; }',
            '.mediux-fanart-sets-mode .mediux-setbrowser-panel { display: block; }',
            '.mediux-fanart-sets-mode .mediux-image-search-list { display: none !important; }',
            '.mediux-fanart-sets-mode .mediux-image-search-standard {',
            '  opacity: 0.45;',
            '  pointer-events: none;',
            '  filter: grayscale(0.35);',
            '}',
            '.mediux-browse-by-wrap { margin: 0 1em 0 0; }',
            '.mediux-bindings-banner {',
            '  margin: 0 0 1em;',
            '  padding: 0.85em 1em;',
            '  border: 1px solid rgba(255,255,255,0.12);',
            '  border-radius: 8px;',
            '  background: rgba(0,0,0,0.25);',
            '}',
            '.mediux-bindings-heading { font-weight: 600; margin-bottom: 0.6em; }',
            '.mediux-bindings-row {',
            '  display: grid;',
            '  grid-template-columns: repeat(4, minmax(0, 1fr));',
            '  gap: 0.5em 1em;',
            '  margin-top: 0.35em;',
            '}',
            '.mediux-bindings-cell { font-size: 0.9em; min-width: 0; }',
            '.mediux-bindings-label { color: rgba(255,255,255,0.7); }',
            '.mediux-bindings-none { color: rgba(255,255,255,0.45); }',
            '.mediux-bindings-author { color: #00a4dc; text-decoration: none; }',
            '.mediux-bindings-author:hover { text-decoration: underline; }',
            '.mediux-set { margin-bottom: 1.5em; border: 1px solid rgba(255,255,255,0.1); border-radius: 8px; padding: 1em; background: rgba(0,0,0,0.2); }',
            '.mediux-set-header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 0.75em; flex-wrap: wrap; gap: 0.5em; }',
            '.mediux-setbrowser { overflow-x: hidden; }',
            '.mediux-set-title, .mediux-set-author { text-decoration: none; }',
            '.mediux-set-title { font-size: 1.1em; font-weight: bold; color: #fff; }',
            '.mediux-set-title:hover { color: #00a4dc; text-decoration: underline; }',
            '.mediux-set-author { color: #00a4dc; font-size: 0.9em; margin-left: 0.5em; }',
            '.mediux-set-author:hover { text-decoration: underline; }',
            '.mediux-set-meta { color: rgba(255,255,255,0.6); font-size: 0.85em; }',
            '.mediux-images-row { margin-bottom: 0.75em; position: relative; }',
            '.mediux-images-row .itemsContainer { white-space: nowrap; }',
            '.mediux-img-card { display: inline-block; vertical-align: top; text-align: center; margin-right: 0.5em; white-space: normal; overflow: visible !important; }',
            '@keyframes mediux-shimmer { 0% { background-position: -200% 0; } 100% { background-position: 200% 0; } }',
            '.mediux-img-card img { display: block; border-radius: 4px 4px 0 0; cursor: pointer; transition: opacity 0.2s; aspect-ratio: 2 / 3; width: 8vw; object-fit: cover; background: rgba(255,255,255,0.06); }',
            '.mediux-img-card.backdropArt img { aspect-ratio: 16 / 9; width: 14vw; }',
            '.mediux-img-card.album img { aspect-ratio: 1 / 1; }',
            '.mediux-img-card.mediux-preview-loading img:not([src]),',
            '.mediux-img-card.mediux-preview-loading img[src=""] {',
            '  background: linear-gradient(90deg, rgba(255,255,255,0.04) 25%, rgba(255,255,255,0.12) 50%, rgba(255,255,255,0.04) 75%);',
            '  background-size: 200% 100%;',
            '  animation: mediux-shimmer 1.2s ease-in-out infinite;',
            '}',
            '.mediux-img-card.mediux-preview-failed img { background: rgba(255,255,255,0.04); }',
            '.mediux-img-card img:hover { opacity: 0.8; }',
            '.mediux-img-footer { padding: 0.35em 0.25em 0.15em; }',
            '.mediux-img-label { font-size: 0.75em; color: rgba(255,255,255,0.7); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }',
            '.mediux-img-footer .btnDownloadRemoteImage { margin: 0 auto; display: inline-flex; }',
            '.mediux-btn { background: #00a4dc; color: #fff; border: none; padding: 0.5em 1em; border-radius: 4px; cursor: pointer; font-size: 0.9em; display: inline-flex; align-items: center; gap: 0.35em; }',
            '.mediux-btn .material-icons { font-size: 1.15em; }',
            '.mediux-btn:hover { background: #0088b8; }',
            '.mediux-btn:disabled { opacity: 0.5; cursor: not-allowed; }',
            '.mediux-loading { text-align: center; padding: 2em; color: rgba(255,255,255,0.7); }',
            '.mediux-empty { text-align: center; padding: 2em; color: rgba(255,255,255,0.5); }',
            '.toastContainer.mediux-toast-container,',
            '.toastContainer {',
            '  position: fixed;',
            '  left: 0;',
            '  bottom: 0;',
            '  z-index: 9999999;',
            '  pointer-events: none;',
            '  padding: 1em;',
            '  display: flex;',
            '  flex-direction: column;',
            '}',
            '.mediux-set-download-toast {',
            '  max-width: 28em;',
            '  white-space: normal;',
            '  line-height: 1.35;',
            '  pointer-events: initial;',
            '  transition: transform 0.3s ease-out, opacity 0.3s ease-out;',
            '  transform: translateY(16em);',
            '  opacity: 1;',
            '}',
            '.mediux-set-download-toast.toastVisible {',
            '  transform: none;',
            '}',
            '.mediux-set-download-toast.toastHide {',
            '  opacity: 0;',
            '}',
            '.mediux-toast-line { display: flex; align-items: center; gap: 0.35em; flex-wrap: nowrap; }',
            '.mediux-toast-progress { flex: 1 1 auto; min-width: 0; }',
            '.mediux-toast-cancel {',
            '  flex: 0 0 auto;',
            '  margin: -0.25em 0;',
            '  color: inherit;',
            '}',
            '.mediux-toast-cancel .material-icons { font-size: 1.25em; }',
            '.mediux-toast-queue { margin-top: 0.35em; opacity: 0.9; }',
            '.mediux-toast-actions { margin-top: 0.5em; }',
            '.mediux-toast-link {',
            '  background: none;',
            '  border: none;',
            '  color: #00a4dc;',
            '  cursor: pointer;',
            '  padding: 0;',
            '  font: inherit;',
            '  text-decoration: underline;',
            '}',
            '.mediux-download-set-dialog {',
            '  width: auto !important;',
            '  max-width: 90vw;',
            '}',
            '.mediux-download-set-content {',
                'display: grid;',
                'gap: 0.5em;',
            '}',
            '.mediux-download-set-content > * {',
            '  margin: 0;',
            '}',
        ].join('\n');
        document.head.appendChild(style);
    }

    function getDialogItemId(dialog) {
        if (!dialog || dialog.getAttribute('data-mediux-sets-eligible') !== 'true') {
            return null;
        }

        var resolvedId = dialog.getAttribute('data-mediux-item-id');
        return isItemId(resolvedId) ? resolvedId : null;
    }

    function createSetBrowserPanel() {
        var panel = document.createElement('div');
        panel.className = 'mediux-setbrowser-panel';

        var inner = document.createElement('div');
        inner.className = 'mediux-setbrowser';
        inner.setAttribute('data-mediux-sets-root', 'true');

        var loading = document.createElement('div');
        loading.className = 'mediux-loading';
        loading.textContent = 'Loading sets from MediUX...';
        inner.appendChild(loading);

        panel.appendChild(inner);
        return panel;
    }

    function countSlotKind(images, slotKind) {
        var count = 0;
        for (var i = 0; i < images.length; i++) {
            if (images[i].slotKind === slotKind) {
                count++;
            }
        }
        return count;
    }

    function buildRowLabel(parts) {
        var segments = [];
        for (var i = 0; i < parts.length; i++) {
            if (parts[i].count > 0) {
                segments.push(parts[i].label + ' (' + parts[i].count + ')');
            }
        }
        return segments.join(' / ');
    }

    function groupSetImages(images) {
        var posters = [];
        var wides = [];

        images.forEach(function (img) {
            if (img.slotKind === 'Backdrop'
                || img.slotKind === 'EpisodeTitleCard'
                || img.slotKind === 'AlbumArt'
                || img.slotKind === 'Logo') {
                wides.push(img);
            } else {
                posters.push(img);
            }
        });

        return { posters: posters, wides: wides };
    }

    function createCloudDownloadButton(title) {
        var dlBtn = document.createElement('button');
        dlBtn.setAttribute('is', 'paper-icon-button-light');
        dlBtn.className = 'btnDownloadRemoteImage autoSize paper-icon-button-light';
        dlBtn.type = 'button';
        dlBtn.title = title || 'Download';
        dlBtn.innerHTML = '<span class="material-icons cloud_download" aria-hidden="true"></span>';
        return dlBtn;
    }

    function createImageCard(img, itemId, setId) {
        var card = document.createElement('div');
        card.className = 'mediux-img-card card cardBox visualCardBox';
        if (img.slotKind === 'Backdrop' || img.slotKind === 'EpisodeTitleCard' || img.slotKind === 'Logo') {
            card.classList.add('backdropArt');
        } else if (img.slotKind === 'AlbumArt') {
            card.classList.add('album');
        }

        var imgEl = document.createElement('img');
        imgEl.alt = getSlotLabel(img);
        attachPreviewImage(imgEl, img);
        imgEl.addEventListener('click', function () {
            window.open(img.url, '_blank');
        });
        card.appendChild(imgEl);

        var footer = document.createElement('div');
        footer.className = 'mediux-img-footer cardFooter visualCardBox-cardFooter';

        var label = document.createElement('div');
        label.className = 'mediux-img-label cardText cardTextCentered';
        label.textContent = getSlotLabel(img);
        footer.appendChild(label);

        var dlWrap = document.createElement('div');
        dlWrap.className = 'cardText cardTextCentered';

        var dlBtn = createCloudDownloadButton('Download');
        dlBtn.addEventListener('click', function (e) {
            e.stopPropagation();
            dlBtn.disabled = true;
            ensureChildrenForImages(itemId, [img]).then(function (children) {
                return downloadMediuxImage(itemId, img, children);
            }).then(function () {
                return updateBindingsForImages(itemId, setId, [img], false);
            }).then(function () {
                dlBtn.querySelector('.material-icons').textContent = 'check';
                dlBtn.disabled = false;
            }).catch(function () {
                dlBtn.querySelector('.material-icons').textContent = 'error';
                dlBtn.disabled = false;
            });
        });
        dlWrap.appendChild(dlBtn);
        footer.appendChild(dlWrap);
        card.appendChild(footer);

        return card;
    }

    function createScrollButtons() {
        var buttons = document.createElement('div');
        buttons.className = 'emby-scrollbuttons';
        buttons.innerHTML = [
            '<button type="button" is="paper-icon-button-light" data-ripple="false" data-direction="left" title="Previous" class="emby-scrollbuttons-button paper-icon-button-light">',
            '  <span class="material-icons chevron_left" aria-hidden="true"></span>',
            '</button>',
            '<button type="button" is="paper-icon-button-light" data-ripple="false" data-direction="right" title="Next" class="emby-scrollbuttons-button paper-icon-button-light">',
            '  <span class="material-icons chevron_right" aria-hidden="true"></span>',
            '</button>'
        ].join('');
        return buttons;
    }

    function renderImageRow(parent, label, images, itemId, setId) {
        if (!images || images.length === 0 || !label) {
            return;
        }

        var row = document.createElement('div');
        row.className = 'verticalSection mediux-images-row emby-scroller-container';

        var rowLabel = document.createElement('h2');
        rowLabel.className = 'sectionTitle sectionTitle-cards mediux-images-row-label';
        rowLabel.textContent = label;
        row.appendChild(rowLabel);

        //row.appendChild(createScrollButtons());

        var scroller = document.createElement('div');
        scroller.className = 'padded-top-focusscale padded-bottom-focusscale emby-scroller no-padding';
        scroller.setAttribute('is', 'emby-scroller');
        scroller.setAttribute('data-centerfocus', 'true');
        scroller.setAttribute('data-scroll-mode-x', 'custom');

        var items = document.createElement('div');
        items.setAttribute('is', 'emby-itemscontainer');
        items.className = 'itemsContainer scrollSlider focuscontainer-x';

        sortImages(images).forEach(function (img) {
            items.appendChild(createImageCard(img, itemId, setId));
        });

        scroller.appendChild(items);
        row.appendChild(scroller);
        parent.appendChild(row);
    }

    function renderSets(root, sets, itemId) {
        revokePreviewBlobs();
        root.innerHTML = '';

        if (!sets || sets.length === 0) {
            var empty = document.createElement('div');
            empty.className = 'mediux-empty';
            empty.textContent = 'No MediUX sets found for this item.';
            root.appendChild(empty);
            return;
        }

        sets.forEach(function (set) {
            var setEl = document.createElement('div');
            setEl.className = 'mediux-set';

            var header = document.createElement('div');
            header.className = 'mediux-set-header';

            var titleArea = document.createElement('div');

            if (set.setId) {
                var titleLink = document.createElement('a');
                titleLink.className = 'mediux-set-title';
                titleLink.href = 'https://mediux.pro/sets/' + encodeURIComponent(set.setId);
                titleLink.target = '_blank';
                titleLink.rel = 'noopener noreferrer';
                titleLink.textContent = set.setTitle;
                titleArea.appendChild(titleLink);
            } else {
                var titleSpan = document.createElement('span');
                titleSpan.className = 'mediux-set-title';
                titleSpan.textContent = set.setTitle;
                titleArea.appendChild(titleSpan);
            }

            if (set.username) {
                var authorLink = document.createElement('a');
                authorLink.className = 'mediux-set-author';
                authorLink.href = 'https://mediux.pro/user/' + encodeURIComponent(set.username) + '/';
                authorLink.target = '_blank';
                authorLink.rel = 'noopener noreferrer';
                authorLink.textContent = 'by ' + set.username;
                titleArea.appendChild(authorLink);
            }

            var metaSpan = document.createElement('span');
            metaSpan.className = 'mediux-set-meta';
            metaSpan.textContent = ' \u2022 ' + set.imageCount + ' images';
            titleArea.appendChild(metaSpan);

            header.appendChild(titleArea);

            var actionArea = document.createElement('div');
            actionArea.style.display = 'flex';
            actionArea.style.alignItems = 'center';
            actionArea.style.gap = '0.5em';

            var dlAllBtn = document.createElement('button');
            dlAllBtn.className = 'emby-button raised button-submit';
            dlAllBtn.type = 'button';
            dlAllBtn.innerHTML = '<span class="material-icons cloud_download" aria-hidden="true" style="margin-right: 0.25em;"></span><span>Download Set</span>';
            dlAllBtn.addEventListener('click', function () {
                dlAllBtn.disabled = true;
                showDownloadSetDialog(set, itemId).then(function () {
                    dlAllBtn.disabled = false;
                });
            });
            actionArea.appendChild(dlAllBtn);

            header.appendChild(actionArea);
            setEl.appendChild(header);

            var images = set.images || [];
            var grouped = groupSetImages(images);
            var posterLabel = buildRowLabel([
                { label: 'Poster', count: countSlotKind(images, 'Primary') },
                { label: 'Season Posters', count: countSlotKind(images, 'SeasonPrimary') }
            ]);
            var wideLabel = buildRowLabel([
                { label: 'Backdrop', count: countSlotKind(images, 'Backdrop') },
                { label: 'Titlecards', count: countSlotKind(images, 'EpisodeTitleCard') },
                { label: 'Album Art', count: countSlotKind(images, 'AlbumArt') },
                { label: 'Logo', count: countSlotKind(images, 'Logo') }
            ]);

            renderImageRow(setEl, posterLabel, grouped.posters, itemId, set.setId);
            renderImageRow(setEl, wideLabel, grouped.wides, itemId, set.setId);

            root.appendChild(setEl);
        });
    }

    function loadSetsIntoPanel(dialog, panel, attempt) {
        attempt = attempt || 0;
        var root = panel.querySelector('[data-mediux-sets-root]');
        if (!root) {
            return;
        }

        var eligible = dialog.getAttribute('data-mediux-sets-eligible');
        if (eligible === 'false') {
            root.innerHTML = '<div class="mediux-empty">Fanart Sets are only available for movies and series.</div>';
            return;
        }

        if (eligible !== 'true') {
            if (attempt < 25) {
                setTimeout(function () {
                    loadSetsIntoPanel(dialog, panel, attempt + 1);
                }, 200);
                return;
            }

            root.innerHTML = '<div class="mediux-empty">Could not determine a valid movie or series for MediUX lookup.</div>';
            return;
        }

        var itemId = getDialogItemId(dialog);
        if (!itemId) {
            root.innerHTML = '<div class="mediux-empty">Could not determine item id for MediUX lookup.</div>';
            return;
        }

        if (panel.getAttribute('data-mediux-loaded') === 'true') {
            return;
        }

        var mediaType = dialog.getAttribute('data-mediux-media-type') || '';
        root.innerHTML = '<div class="mediux-loading">Loading sets from MediUX...</div>';

        Promise.all([
            fetch(window.ApiClient.getUrl('MediUX/Sets', { itemId: itemId }), { headers: getApiHeaders() })
                .then(function (resp) { return resp.json(); }),
            fetchSetBindings(itemId)
        ]).then(function (results) {
            var sets = results[0];
            var bindings = results[1];
            panel.setAttribute('data-mediux-loaded', 'true');
            panel.setAttribute('data-mediux-item-id', itemId);
            panel.setAttribute('data-mediux-media-type', mediaType);
            try {
                panel.setAttribute('data-mediux-sets-json', JSON.stringify((sets || []).map(function (s) {
                    return { setId: s.setId, setTitle: s.setTitle, username: s.username };
                })));
            } catch (e) {
                panel.setAttribute('data-mediux-sets-json', '[]');
            }

            root.innerHTML = '';
            var banner = document.createElement('div');
            banner.className = 'mediux-bindings-banner';
            root.appendChild(banner);
            renderBindingsBanner(banner, bindings, sets, mediaType);

            var setsRoot = document.createElement('div');
            setsRoot.className = 'mediux-sets-list';
            root.appendChild(setsRoot);
            renderSets(setsRoot, sets, itemId);
        }).catch(function (err) {
            root.innerHTML = '<div class="mediux-empty">Failed to load sets: ' + (err.message || err) + '</div>';
        });
    }

    function setBrowseMode(dialog, mode) {
        var inner = dialog.querySelector('.dialogContentInner');
        if (!inner) return;

        if (mode === 'fanartSets') {
            if (dialog.getAttribute('data-mediux-sets-eligible') !== 'true') {
                var browseSelect = dialog.querySelector('#selectMediuxBrowseBy');
                if (browseSelect) {
                    browseSelect.value = 'imageType';
                }
                return;
            }

            inner.classList.add('mediux-fanart-sets-mode');
            setStandardControlsEnabled(dialog, false);
            var panel = inner.querySelector('.mediux-setbrowser-panel');
            if (panel) {
                loadSetsIntoPanel(dialog, panel);
            }
        } else {
            inner.classList.remove('mediux-fanart-sets-mode');
            setStandardControlsEnabled(dialog, true);
        }
    }

    function setStandardControlsEnabled(dialog, enabled) {
        var controls = dialog.querySelectorAll('.mediux-image-search-standard');
        for (var i = 0; i < controls.length; i++) {
            var inputs = controls[i].querySelectorAll('select, input, button, textarea');
            for (var j = 0; j < inputs.length; j++) {
                inputs[j].disabled = !enabled;
            }
        }
    }

    function markStandardControls(dialog) {
        var inner = dialog.querySelector('.dialogContentInner');
        if (!inner) return;

        var provider = dialog.querySelector('#selectImageProvider');
        if (provider) {
            var sourceWrap = provider.closest('div[style]') || provider.closest('.selectContainer') || provider.parentElement;
            if (sourceWrap) sourceWrap.classList.add('mediux-image-search-standard');
        }

        var typeSelect = dialog.querySelector('#selectBrowsableImageType');
        if (typeSelect) {
            var typeWrap = typeSelect.closest('div[style]') || typeSelect.closest('.selectContainer') || typeSelect.parentElement;
            if (typeWrap) typeWrap.classList.add('mediux-image-search-standard');
        }

        var paging = dialog.querySelector('.availableImagesPaging');
        if (paging) paging.classList.add('mediux-image-search-standard');

        var allLang = dialog.querySelector('#chkAllLanguages');
        if (allLang) {
            var langLabel = allLang.closest('label');
            if (langLabel) langLabel.classList.add('mediux-image-search-standard');
        }

        var parentImages = dialog.querySelector('#lblShowParentImages');
        if (parentImages) parentImages.classList.add('mediux-image-search-standard');

        var list = dialog.querySelector('.availableImagesList');
        if (list) list.classList.add('mediux-image-search-list');
    }

    function injectBrowseByDropdown(dialog) {
        if (dialog.querySelector('#selectMediuxBrowseBy')) return;

        injectStyles();

        var inner = dialog.querySelector('.dialogContentInner');
        var controlsRow = dialog.querySelector('#selectImageProvider');
        if (!inner || !controlsRow) return;

        controlsRow = controlsRow.closest('.flex');
        if (!controlsRow) return;

        var browseWrap = document.createElement('div');
        browseWrap.className = 'mediux-browse-by-wrap';
        browseWrap.style.marginRight = '1em';
        browseWrap.setAttribute('data-mediux-browse-by-wrap', 'true');
        browseWrap.innerHTML = [
            '<div class="selectContainer">',
            '  <label class="selectLabel" for="selectMediuxBrowseBy">Browse By</label>',
            '  <select id="selectMediuxBrowseBy" is="emby-select" label="Browse By" class="emby-select-withcolor emby-select">',
            '    <option value="imageType">Image Type</option>',
            '    <option value="fanartSets">Fanart Sets</option>',
            '  </select>',
            '  <div class="selectArrowContainer">',
            '    <div style="visibility:hidden;display:none;">0</div>',
            '    <span class="selectArrow material-icons keyboard_arrow_down" aria-hidden="true"></span>',
            '  </div>',
            '</div>'
        ].join('');

        controlsRow.insertBefore(browseWrap, controlsRow.firstChild);

        var panel = createSetBrowserPanel();
        inner.appendChild(panel);

        markStandardControls(dialog);

        var select = browseWrap.querySelector('#selectMediuxBrowseBy');
        select.addEventListener('change', function () {
            setBrowseMode(dialog, select.value);
        });

        setFanartSetsOptionEnabled(dialog, false);
        dialog.setAttribute('data-mediux-sets-eligible', 'pending');
        resolveMediuxBaseForDialog(dialog);

        dialog.setAttribute(MEDIUX_INJECTED_ATTR, 'true');
    }

    function isImageSearchDialog(dialog) {
        return !!(dialog.querySelector('#selectImageProvider') && dialog.querySelector('#selectBrowsableImageType'));
    }

    function tryInjectIntoDialog(dialog) {
        if (!dialog || !isImageSearchDialog(dialog)) return;
        if (dialog.getAttribute(MEDIUX_INJECTED_ATTR)) return;

        injectBrowseByDropdown(dialog);
    }

    function observeDialogs() {
        var observer = new MutationObserver(function (mutations) {
            mutations.forEach(function (mutation) {
                for (var i = 0; i < mutation.addedNodes.length; i++) {
                    var node = mutation.addedNodes[i];
                    if (node.nodeType !== 1) continue;

                    if (node.classList && node.classList.contains('dialog')) {
                        setTimeout(function () { tryInjectIntoDialog(node); }, 300);
                    }

                    if (node.querySelectorAll) {
                        var dialogs = node.querySelectorAll('.dialog');
                        for (var j = 0; j < dialogs.length; j++) {
                            (function (d) {
                                setTimeout(function () { tryInjectIntoDialog(d); }, 300);
                            })(dialogs[j]);
                        }
                    }
                }
            });
        });

        observer.observe(document.body, { childList: true, subtree: true });
    }

    observeItemIdRequests();

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', observeDialogs);
    } else {
        observeDialogs();
    }
})();

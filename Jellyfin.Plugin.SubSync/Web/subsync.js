/**
 * SubSync — Jellyfin client injection script.
 *
 * Adds "Sync Subtitles" to the item menu of a movie/episode, and "Sync all episodes" to the menu of
 * a series or season. The bulk dialog offers what the main SubSync page offers for the same scope:
 * a choice of scope (whole series or one season), the languages that actually exist in it with
 * track/episode counts, and a batch that runs through the server queue with the configured mode
 * (so it runs in parallel exactly like a batch started from the main page).
 *
 * The menu item is injected wherever the item menu appears — the detail page's ⋮ button, the ⋮ on a
 * card/row anywhere in the interface (home rows, library grids, "see all" lists, episode lists) —
 * because the item id is taken from the card whose menu was opened and only falls back to the detail
 * page's id from the URL.
 */
(function () {
    'use strict';

    var SYNC_BASE = '/SubSync';
    var BULK_TYPES = { Series: 1, Season: 1 };
    var SINGLE_TYPES = { Movie: 1, Episode: 1, Video: 1, MusicVideo: 1, Trailer: 1 };

    function log(msg) {
        console.log('[SubSync] ' + msg);
    }

    function token() {
        if (typeof ApiClient !== 'undefined' && ApiClient.accessToken) {
            return ApiClient.accessToken();
        }
        return '';
    }

    // Jellyfin 12 disables the legacy authorization mechanisms by default, so the old
    // `X-Emby-Token` header is ignored there and every call comes back 401. The
    // `Authorization: MediaBrowser Token="..."` form is what jellyfin-web itself sends and
    // is accepted by 10.11 as well, so one form works on both server generations.
    function authHeader() {
        return 'MediaBrowser Token="' + token().replace(/"/g, '') + '"';
    }

    function apiUrl(path) {
        if (typeof ApiClient !== 'undefined' && ApiClient.getUrl) {
            return ApiClient.getUrl(path);
        }
        return '/' + path;
    }

    function api(path, options) {
        options = options || {};
        var headers = options.headers || {};
        headers['Authorization'] = authHeader();
        headers['Accept'] = 'application/json';
        if (options.body) {
            headers['Content-Type'] = 'application/json';
        }
        options.headers = headers;
        return fetch(path.indexOf('://') > 0 || path.charAt(0) === '/' ? path : apiUrl(path), options)
            .then(function (response) {
                if (!response.ok) {
                    return response.text().then(function (text) {
                        throw new Error(response.status + (text ? ': ' + text.slice(0, 200) : ''));
                    });
                }
                return response.status === 204 ? null : response.json();
            });
    }

    // ---------------------------------------------------------------- item metadata

    var metaCache = {};

    function fetchMeta(itemId) {
        if (metaCache[itemId]) {
            return Promise.resolve(metaCache[itemId]);
        }
        var uid = (typeof ApiClient !== 'undefined' && ApiClient.getCurrentUserId) ? ApiClient.getCurrentUserId() : '';
        var fields = 'Fields=SeriesId,SeasonId,IndexNumber,ParentId,SeriesName,MediaSources';
        var path = (uid ? 'Users/' + uid + '/Items/' + itemId + '?' : 'Items/' + itemId + '?') + fields;
        return api(path).then(function (item) {
            var meta = {
                id: itemId,
                type: item.Type || '',
                mediaType: item.MediaType || '',
                name: item.Name || '',
                seriesId: item.SeriesId || null,
                seriesName: item.SeriesName || null,
                seasonId: item.SeasonId || null,
                parentId: item.ParentId || null
            };
            metaCache[itemId] = meta;
            return meta;
        });
    }

    function fetchEpisodes(parentId) {
        var uid = (typeof ApiClient !== 'undefined' && ApiClient.getCurrentUserId) ? ApiClient.getCurrentUserId() : '';
        var collected = [];

        // Paged explicitly: a series can run to hundreds of episodes and the endpoint's own default
        // page size must not silently shorten the scope.
        function page(startIndex) {
            var params = [
                'ParentId=' + encodeURIComponent(parentId),
                'Recursive=true',
                'IncludeItemTypes=Episode',
                'SortBy=SortName',
                'SortOrder=Ascending',
                'Fields=SeasonId,IndexNumber,ParentId,SeriesId,SeriesName',
                'StartIndex=' + startIndex,
                'Limit=500'
            ].join('&');
            return api((uid ? 'Users/' + uid + '/' : '') + 'Items?' + params).then(function (data) {
                var items = (data && data.Items) || [];
                items.forEach(function (i) {
                    collected.push({
                        id: i.Id,
                        name: i.Name || '',
                        seasonId: i.SeasonId || i.ParentId || null,
                        seriesId: i.SeriesId || null,
                        seriesName: i.SeriesName || null,
                        index: typeof i.IndexNumber === 'number' ? i.IndexNumber : null
                    });
                });
                if (items.length >= 500 && collected.length < 20000) {
                    return page(startIndex + items.length);
                }
                return collected;
            });
        }

        return page(0);
    }

    function fetchSeasons(seriesId) {
        if (!seriesId) {
            return Promise.resolve([]);
        }
        var params = [
            'ParentId=' + encodeURIComponent(seriesId),
            'Recursive=false',
            'IncludeItemTypes=Season',
            'SortBy=SortName',
            'SortOrder=Ascending'
        ].join('&');
        var uid = (typeof ApiClient !== 'undefined' && ApiClient.getCurrentUserId) ? ApiClient.getCurrentUserId() : '';
        return api((uid ? 'Users/' + uid + '/' : '') + 'Items?' + params).then(function (data) {
            return ((data && data.Items) || []).map(function (i) {
                return { id: i.Id, name: i.Name || 'Season' };
            });
        });
    }

    /**
     * Subtitle tracks for many items at once. The bulk endpoint is what makes a whole-series
     * language list cheap: one request per 500 episodes instead of one per episode.
     */
    function fetchTracks(itemIds, onProgress) {
        var chunks = [];
        for (var i = 0; i < itemIds.length; i += 500) {
            chunks.push(itemIds.slice(i, i + 500));
        }
        var tracksById = {};
        var done = 0;
        var sequence = Promise.resolve();
        chunks.forEach(function (chunk) {
            sequence = sequence.then(function () {
                return api(SYNC_BASE + '/Subtitles/Batch', {
                    method: 'POST',
                    body: JSON.stringify({ itemIds: chunk, expandSeries: false })
                }).then(function (result) {
                    var items = (result && (result.items || result.Items)) || [];
                    items.forEach(function (entry) {
                        var id = entry.Id || entry.id;
                        var tracks = entry.Tracks || entry.tracks || [];
                        tracksById[id] = tracks.filter(function (t) {
                            return t && (typeof (t.Index !== undefined ? t.Index : t.index) === 'number');
                        });
                    });
                    done += chunk.length;
                    if (onProgress) {
                        onProgress(Math.min(done, itemIds.length), itemIds.length);
                    }
                });
            });
        });
        return sequence.then(function () { return tracksById; });
    }

    // ---------------------------------------------------------------- helpers

    function trackIndex(t) {
        return t.Index !== undefined ? t.Index : t.index;
    }

    function trackLanguage(t) {
        var lang = t.Language || t.language;
        return (lang && lang !== 'und') ? String(lang).toLowerCase() : 'und';
    }

    function trackTitle(t) {
        return t.Title || t.title || '';
    }

    function trackIsExternal(t) {
        return !!(t.IsExternal !== undefined ? t.IsExternal : t.isExternal);
    }

    function trackForced(t) {
        return !!(t.IsForced !== undefined ? t.IsForced : t.isForced);
    }

    /**
     * Lower is better when two tracks share a language: a forced track carries only on-screen signs
     * and text (two cues over a whole episode in the case that prompted this), so it must never win
     * over the full track, and an external file is preferred among equals.
     */
    function trackRank(t) {
        return (trackForced(t) ? 2 : 0) + (trackIsExternal(t) ? 0 : 1);
    }

    function LANG_NAMES() {
        return {
            eng: 'English', swe: 'Swedish', nor: 'Norwegian', dan: 'Danish', fin: 'Finnish',
            deu: 'German', fra: 'French', spa: 'Spanish', ita: 'Italian', nld: 'Dutch',
            por: 'Portuguese', rus: 'Russian', pol: 'Polish', tur: 'Turkish', uzb: 'Uzbek',
            ara: 'Arabic', heb: 'Hebrew', zho: 'Chinese', chi: 'Chinese', jpn: 'Japanese',
            kor: 'Korean', ces: 'Czech', hun: 'Hungarian', srp: 'Serbian', hr: 'Croatian',
            ell: 'Greek', rum: 'Romanian', ukr: 'Ukrainian', tha: 'Thai', hin: 'Hindi',
            vie: 'Vietnamese', ind: 'Indonesian', bul: 'Bulgarian', slk: 'Slovak',
            slv: 'Slovenian', lit: 'Lithuanian', lav: 'Latvian', est: 'Estonian',
            isl: 'Icelandic', und: 'Unknown'
        };
    }

    function languageLabel(code) {
        if (!code || code === 'und') {
            return 'Unknown language';
        }
        var names = LANG_NAMES();
        return names[code] || code.toUpperCase();
    }

    function humanCount(n, one, many) {
        return n + ' ' + (n === 1 ? one : (many || one + 's'));
    }

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) {
            node.className = className;
        }
        if (text !== undefined && text !== null) {
            node.textContent = text;
        }
        return node;
    }

    // ---------------------------------------------------------------- styles

    function ensureStyles() {
        if (document.getElementById('subsync-styles')) {
            return;
        }
        var style = document.createElement('style');
        style.id = 'subsync-styles';
        style.textContent = [
            '.ss-overlay{position:fixed;inset:0;background:rgba(0,0,0,.72);z-index:99999;display:flex;align-items:center;justify-content:center;padding:16px}',
            '.ss-card{background:#202020;color:#eee;border-radius:10px;padding:20px 22px;width:100%;max-width:560px;max-height:86vh;overflow-y:auto;box-shadow:0 12px 40px rgba(0,0,0,.5);font-size:.95em}',
            '.ss-card h2{margin:0 0 2px;font-size:1.25em;font-weight:600}',
            '.ss-sub{color:#9a9a9a;font-size:.85em;margin-bottom:16px}',
            '.ss-field{display:flex;flex-direction:column;gap:4px;margin-bottom:14px}',
            '.ss-field label{font-size:.82em;color:#b9b9b9;text-transform:uppercase;letter-spacing:.04em}',
            '.ss-field select,.ss-field input{background:#2b2b2b;color:#eee;border:1px solid #3c3c3c;border-radius:6px;padding:8px 10px;font-size:.95em;width:100%}',
            '.ss-field select:disabled{color:#777}',
            '.ss-note{color:#8d8d8d;font-size:.82em;margin-top:-8px;margin-bottom:14px}',
            '.ss-row{display:flex;align-items:center;gap:10px;padding:8px 0;border-bottom:1px solid #2e2e2e}',
            '.ss-row:last-child{border-bottom:none}',
            '.ss-row-name{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}',
            '.ss-row-meta{color:#8d8d8d;font-size:.8em}',
            '.ss-synced{color:#7fce8f}',
            '.ss-actions{display:flex;gap:10px;justify-content:flex-end;margin-top:18px}',
            '.ss-btn{border:none;border-radius:6px;padding:9px 18px;font-size:.95em;cursor:pointer;background:#3a3a3a;color:#eee}',
            '.ss-btn:hover{background:#454545}',
            '.ss-btn-primary{background:#3d7bd6;color:#fff}',
            '.ss-btn-primary:hover{background:#4a8ae8}',
            '.ss-btn:disabled{opacity:.55;cursor:default}',
            '.ss-progress{margin-top:16px;display:none}',
            '.ss-progress.is-visible{display:block}',
            '.ss-bar{height:3px;border-radius:2px;background:#333;overflow:hidden}',
            '.ss-bar > span{display:block;height:100%;width:0;background:#3d7bd6;transition:width .4s ease}',
            '.ss-progress-line{margin-top:8px;font-size:.85em;color:#c7c7c7;display:flex;gap:8px;align-items:baseline;flex-wrap:wrap}',
            '.ss-progress-line .ss-muted{color:#8d8d8d}',
            '.ss-error{color:#f08a8a;font-size:.85em;margin-top:10px}',
            '.ss-spinner{width:12px;height:12px;border:2px solid #444;border-top-color:#3d7bd6;border-radius:50%;display:inline-block;animation:ss-spin .8s linear infinite}',
            '@keyframes ss-spin{to{transform:rotate(360deg)}}'
        ].join('\n');
        document.head.appendChild(style);
    }

    // ---------------------------------------------------------------- dialog shell

    function buildShell(title, subtitle) {
        ensureStyles();
        var overlay = el('div', 'ss-overlay');
        var card = el('div', 'ss-card');
        var heading = el('h2', null, title);
        var sub = el('div', 'ss-sub', subtitle || '');
        var body = el('div', 'ss-body');
        var progress = el('div', 'ss-progress');
        var bar = el('div', 'ss-bar');
        var fill = el('span');
        bar.appendChild(fill);
        var line = el('div', 'ss-progress-line');
        progress.appendChild(bar);
        progress.appendChild(line);
        var error = el('div', 'ss-error');
        var actions = el('div', 'ss-actions');
        var close = el('button', 'ss-btn', 'Close');
        close.type = 'button';
        actions.appendChild(close);

        card.appendChild(heading);
        card.appendChild(sub);
        card.appendChild(body);
        card.appendChild(progress);
        card.appendChild(error);
        card.appendChild(actions);
        overlay.appendChild(card);
        document.body.appendChild(overlay);

        function dismiss() {
            if (state.timer) {
                clearInterval(state.timer);
                state.timer = null;
            }
            overlay.remove();
        }

        overlay.addEventListener('click', function (e) {
            if (e.target === overlay) {
                dismiss();
            }
        });
        close.addEventListener('click', dismiss);

        var state = { timer: null, dismiss: dismiss };
        return {
            overlay: overlay,
            card: card,
            body: body,
            sub: sub,
            error: error,
            actions: actions,
            state: state,
            setProgress: function (fraction, parts, muted) {
                progress.classList.add('is-visible');
                fill.style.width = Math.max(0, Math.min(100, Math.round((fraction || 0) * 100))) + '%';
                line.innerHTML = '';
                (parts || []).forEach(function (part, index) {
                    if (index) {
                        line.appendChild(el('span', 'ss-muted', '\u00b7'));
                    }
                    line.appendChild(el('span', index === 0 ? null : 'ss-muted', part));
                });
                if (muted) {
                    line.appendChild(el('span', 'ss-muted', muted));
                }
            },
            hideProgress: function () { progress.classList.remove('is-visible'); },
            showError: function (message) { error.textContent = message || ''; }
        };
    }

    function primaryButton(label) {
        var btn = el('button', 'ss-btn ss-btn-primary', label);
        btn.type = 'button';
        return btn;
    }

    // ---------------------------------------------------------------- bulk dialog (series / season)

    function openBulkDialog(meta) {
        var shell = buildShell('Sync all episodes', 'Reading the episodes\u2026');
        var scopeField = el('div', 'ss-field');
        var scopeLabel = el('label', null, 'Scope');
        var scopeSelect = el('select');
        scopeSelect.disabled = true;
        scopeField.appendChild(scopeLabel);
        scopeField.appendChild(scopeSelect);
        var langField = el('div', 'ss-field');
        var langLabel = el('label', null, 'Subtitles to sync');
        var langSelect = el('select');
        langSelect.disabled = true;
        langField.appendChild(langLabel);
        langField.appendChild(langSelect);
        var note = el('div', 'ss-note', '');
        var startBtn = primaryButton('Sync');
        startBtn.disabled = true;
        shell.body.appendChild(scopeField);
        shell.body.appendChild(langField);
        shell.body.appendChild(note);
        shell.actions.insertBefore(startBtn, shell.actions.firstChild);

        var seriesId = meta.type === 'Series' ? meta.id : (meta.seriesId || null);
        var entrySeasonId = meta.type === 'Season' ? meta.id : null;

        function buildScopes(seriesEpisodes, seasons, fallbackEpisodes, fallbackName) {
            var options = [];
            if (seriesEpisodes && seriesEpisodes.length) {
                options.push({
                    id: seriesId,
                    label: 'Whole series \u2014 ' + humanCount(seriesEpisodes.length, 'episode'),
                    episodes: seriesEpisodes
                });
                var bySeason = {};
                seriesEpisodes.forEach(function (ep) {
                    var key = ep.seasonId || 'unknown';
                    (bySeason[key] = bySeason[key] || []).push(ep);
                });
                (seasons || []).forEach(function (season) {
                    var group = bySeason[season.id];
                    if (group && group.length) {
                        options.push({
                            id: season.id,
                            label: season.name + ' \u2014 ' + humanCount(group.length, 'episode'),
                            episodes: group
                        });
                    }
                });
                // Seasons the server did not name (or returned without a Season item).
                Object.keys(bySeason).forEach(function (key) {
                    if (key === 'unknown') {
                        return;
                    }
                    var already = (seasons || []).some(function (s) { return s.id === key; });
                    if (!already) {
                        options.push({
                            id: key,
                            label: 'Season \u2014 ' + humanCount(bySeason[key].length, 'episode'),
                            episodes: bySeason[key]
                        });
                    }
                });
            } else if (fallbackEpisodes && fallbackEpisodes.length) {
                options.push({
                    id: meta.id,
                    label: (fallbackName || meta.name) + ' \u2014 ' + humanCount(fallbackEpisodes.length, 'episode'),
                    episodes: fallbackEpisodes
                });
            }
            return options;
        }

        var scopes = [];
        var languageCounts = {};

        function scopeById(id) {
            for (var i = 0; i < scopes.length; i++) {
                if (scopes[i].id === id) {
                    return scopes[i];
                }
            }
            return scopes[0];
        }

        function selectedScope() {
            return scopeById(scopeSelect.value);
        }

        /** One task per language per episode, external file preferred over an embedded track. */
        function buildTasks(episodes, tracksById, language) {
            var tasks = [];
            episodes.forEach(function (ep) {
                var tracks = tracksById[ep.id] || [];
                var byLanguage = {};
                tracks.forEach(function (t) {
                    var code = trackLanguage(t);
                    var current = byLanguage[code];
                    if (!current || trackRank(t) < trackRank(current)) {
                        byLanguage[code] = t;
                    }
                });
                Object.keys(byLanguage).sort().forEach(function (code) {
                    if (language && language !== '*' && code !== language) {
                        return;
                    }
                    var t = byLanguage[code];
                    var label = (ep.name || 'Episode') + ' \u2014 ' + (trackTitle(t) || languageLabel(code));
                    tasks.push({ itemId: ep.id, subtitleIndex: trackIndex(t), title: label, language: code, episode: ep.name || '' });
                });
            });
            return tasks;
        }

        function fillLanguages(tracksById, episodes) {
            languageCounts = {};
            var forcedCounts = {};
            var episodeCounts = {};
            episodes.forEach(function (ep) {
                var tracks = tracksById[ep.id] || [];
                var seen = {};
                tracks.forEach(function (t) {
                    var code = trackLanguage(t);
                    languageCounts[code] = (languageCounts[code] || 0) + 1;
                    if (trackForced(t)) {
                        forcedCounts[code] = (forcedCounts[code] || 0) + 1;
                    }
                    if (!seen[code]) {
                        seen[code] = true;
                        episodeCounts[code] = (episodeCounts[code] || 0) + 1;
                    }
                });
            });
            var codes = Object.keys(languageCounts).sort();
            langSelect.innerHTML = '';
            var total = 0;
            codes.forEach(function (code) { total += languageCounts[code]; });
            var all = el('option', null, 'All languages \u2014 ' + humanCount(total, 'track'));
            all.value = '*';
            langSelect.appendChild(all);
            codes.forEach(function (code) {
                // Forced tracks are called out, because they are signs/text tracks with a handful of
                // cues: syncing one by accident is how a two-cue sidecar appeared for a whole episode.
                var forced = forcedCounts[code] || 0;
                var option = el('option', null, languageLabel(code) + ' \u2014 '
                    + humanCount(episodeCounts[code], 'episode') + ', ' + humanCount(languageCounts[code], 'track')
                    + (forced ? ', ' + forced + ' forced' : ''));
                option.value = code;
                langSelect.appendChild(option);
            });
            // Keep the Swedish/English order familiar: most episodes first, then alphabetically.
            langSelect.disabled = codes.length === 0;
            return codes.length;
        }

        function summarise() {
            var scope = selectedScope();
            if (!scope) {
                return;
            }
            var lang = langSelect.value || '*';
            var tracks = tracksCache[scope.id] || {};
            var tasks = buildTasks(scope.episodes, tracks, lang);
            var episodes = {};
            tasks.forEach(function (t) { episodes[t.itemId] = 1; });
            if (!tasks.length) {
                note.textContent = 'No subtitle tracks found in this scope.';
                startBtn.disabled = true;
                return;
            }
            note.textContent = humanCount(tasks.length, 'track') + ' across '
                + humanCount(Object.keys(episodes).length, 'episode')
                + (lang === '*' ? '' : ' (' + languageLabel(lang) + ' only)')
                + ' \u2014 runs through the server queue with the mode configured in Settings.';
            startBtn.disabled = false;
        }

        var tracksCache = {};
        var scanToken = 0;

        function scan() {
            var scope = selectedScope();
            if (!scope) {
                return;
            }
            var myToken = ++scanToken;
            startBtn.disabled = true;
            langSelect.disabled = true;
            if (tracksCache[scope.id]) {
                fillLanguages(tracksCache[scope.id], scope.episodes);
                summarise();
                return;
            }
            var ids = scope.episodes.map(function (ep) { return ep.id; });
            shell.setProgress(0, ['Reading subtitles\u2026'], humanCount(ids.length, 'episode'));
            fetchTracks(ids, function (done, total) {
                if (myToken !== scanToken) {
                    return;
                }
                shell.setProgress(total ? done / total : 0, ['Reading subtitles\u2026', done + '/' + total]);
            }).then(function (tracksById) {
                if (myToken !== scanToken) {
                    return;
                }
                tracksCache[scope.id] = tracksById;
                shell.hideProgress();
                fillLanguages(tracksById, scope.episodes);
                summarise();
            }).catch(function (err) {
                if (myToken !== scanToken) {
                    return;
                }
                shell.hideProgress();
                shell.showError('Could not read the subtitles: ' + (err.message || err));
            });
        }

        function start() {
            var scope = selectedScope();
            var lang = langSelect.value || '*';
            var tasks = buildTasks(scope.episodes, tracksCache[scope.id] || {}, lang);
            if (!tasks.length) {
                return;
            }
            startBtn.disabled = true;
            startBtn.textContent = 'Starting\u2026';
            var payload = {
                label: meta.name + (scope.id === seriesId ? '' : ' \u2014 ' + scope.label.split(' \u2014 ')[0]),
                tasks: tasks.map(function (t) {
                    return { itemId: t.itemId, subtitleIndex: t.subtitleIndex, title: t.title };
                })
            };
            api(SYNC_BASE + '/Batch', {
                method: 'POST',
                body: JSON.stringify(payload)
            }).then(function (view) {
                var batchId = (view && (view.Id || view.id)) || '';
                if (!batchId) {
                    throw new Error('the server did not return a batch id');
                }
                shell.setProgress(0, ['Queued', humanCount(tasks.length, 'track')]);
                watchBatch(batchId, tasks);
            }).catch(function (err) {
                startBtn.disabled = false;
                startBtn.textContent = 'Sync';
                shell.showError('Could not start: ' + (err.message || err));
            });
        }

        function watchBatch(batchId, tasks) {
            var titles = {};
            tasks.forEach(function (t) { titles[t.itemId] = t.episode; });
            var pollFailures = 0;
            function poll() {
                api(SYNC_BASE + '/Batch/' + batchId).then(function (view) {
                    pollFailures = 0;
                    var total = (view.Total !== undefined ? view.Total : view.total) || tasks.length;
                    var ok = (view.Ok !== undefined ? view.Ok : view.ok) || 0;
                    var failed = (view.Failed !== undefined ? view.Failed : view.failed) || 0;
                    var cancelled = (view.Cancelled !== undefined ? view.Cancelled : view.cancelled) || 0;
                    var completed = (view.Completed !== undefined ? view.Completed : view.completed) || 0;
                    var status = view.Status || view.status || '';
                    var running = (view.RunningTasks || view.runningTasks || []).length;
                    var limit = view.WorkerLimit || view.workerLimit || 0;
                    var mode = (view.Mode || view.mode || '').toLowerCase();

                    var terminal = status === 'Completed' || status === 'Failed' || status === 'Cancelled' || status === 'Partial';
                    var parts;
                    if (status === 'Queued' && running === 0) {
                        parts = ['Waiting for the queue\u2026', humanCount(completed, 'track') + ' of ' + total + ' done'];
                    } else if (terminal) {
                        parts = [humanCount(ok, 'track') + ' synced'];
                        if (failed) {
                            parts.push(failed + ' failed');
                        }
                        if (cancelled) {
                            parts.push(cancelled + ' cancelled');
                        }
                        if (mode && limit > 1) {
                            parts.push(limit + ' at a time');
                        }
                    } else {
                        parts = [completed + '/' + total, running + (limit > 1 ? '/' + limit : '') + ' running'];
                        var current = view.CurrentTask || view.currentTask;
                        if (current) {
                            var currentTitle = current.Title || current.title || '';
                            parts.push(currentTitle.length > 46 ? currentTitle.slice(0, 46) + '\u2026' : currentTitle);
                        }
                    }
                    shell.setProgress(total ? completed / total : 0, parts);

                    if (terminal) {
                        clearInterval(shell.state.timer);
                        shell.state.timer = null;
                        startBtn.disabled = false;
                        startBtn.textContent = 'Sync again';
                        shell.sub.textContent = failed
                            ? 'Finished with ' + humanCount(failed, 'failure')
                            : 'Finished \u2014 the library is refreshed for the folders that changed.';
                    }
                }).catch(function (err) {
                    // One failed poll used to stop the watcher for good, so a single blip left the
                    // panel on stale numbers until the page was reloaded. Tolerate a run of them
                    // before saying contact was lost.
                    pollFailures++;
                    if (pollFailures < 10) {
                        shell.sub.textContent = 'Reconnecting\u2026 (' + pollFailures + ')';
                        return;
                    }
                    clearInterval(shell.state.timer);
                    shell.state.timer = null;
                    shell.showError('Lost contact with the batch: ' + (err.message || err));
                    startBtn.disabled = false;
                    startBtn.textContent = 'Sync';
                });
            }
            poll();
            shell.state.timer = setInterval(poll, 1500);
        }

        startBtn.addEventListener('click', start);
        scopeSelect.addEventListener('change', function () {
            scan();
        });
        langSelect.addEventListener('change', summarise);

        // Load the scope list: the whole series (so every season can be chosen) plus this season.
        var menuTarget = seriesId || meta.id;
        Promise.all([
            fetchEpisodes(menuTarget),
            fetchSeasons(seriesId && seriesId !== meta.id ? seriesId : (meta.type === 'Series' ? meta.id : null))
        ]).then(function (results) {
            var episodes = results[0] || [];
            var seasons = results[1] || [];
            scopes = buildScopes(episodes, seasons, episodes, meta.name);
            if (!scopes.length) {
                // A season inside a series whose episodes could not be listed by series id: use its own.
                return fetchEpisodes(meta.id).then(function (own) {
                    scopes = buildScopes(null, null, own, meta.name);
                    return null;
                });
            }
            return null;
        }).then(function () {
            if (!scopes.length) {
                shell.sub.textContent = 'No episodes found in this item.';
                return;
            }
            scopeSelect.innerHTML = '';
            scopes.forEach(function (scope) {
                var option = el('option', null, scope.label);
                option.value = scope.id;
                scopeSelect.appendChild(option);
            });
            // Default to the item the menu was opened on.
            if (entrySeasonId) {
                scopeSelect.value = entrySeasonId;
                if (scopeSelect.value !== entrySeasonId) {
                    scopeSelect.value = scopes[0].id;
                }
            }
            scopeSelect.disabled = scopes.length < 2;
            if (scopeField) {
                scopeField.style.display = scopes.length > 1 ? '' : 'none';
            }
            var total = scopes.reduce(function (sum, scope) { return sum + scope.episodes.length; }, 0);
            shell.sub.textContent = meta.name + ' \u2014 ' + humanCount(scopes[0].episodes.length, 'episode')
                + (scopes.length > 1 ? ', ' + humanCount(scopes.length - 1, 'season') : '');
            log('Bulk dialog ready for ' + meta.type + ' ' + meta.name + ' (' + total + ' episodes total)');
            scan();
        }).catch(function (err) {
            shell.sub.textContent = 'Could not read the episodes.';
            shell.showError(err.message || String(err));
        });
    }

    // ---------------------------------------------------------------- single-item dialog

    function openSingleDialog(meta) {
        var shell = buildShell('Sync Subtitles', meta.name || '');
        shell.sub.textContent = 'Reading subtitles\u2026';
        api(SYNC_BASE + '/Subtitles/' + meta.id).then(function (tracks) {
            tracks = (tracks || []).filter(function (t) { return typeof trackIndex(t) === 'number'; });
            if (!tracks.length) {
                shell.sub.textContent = 'No text subtitles found for this video.';
                return;
            }
            shell.sub.textContent = 'Pick the subtitle to synchronize. The original is never modified.';
            tracks.forEach(function (track) {
                var row = el('div', 'ss-row');
                var name = el('div', 'ss-row-name');
                var isExternal = trackIsExternal(track);
                name.appendChild(el('span', null, trackTitle(track) || languageLabel(trackLanguage(track))));
                var meta2 = el('div', 'ss-row-meta', (isExternal ? 'external file' : 'embedded')
                    + (trackForced(track) ? ' \u00b7 forced' : '')
                    + ' \u00b7 ' + languageLabel(trackLanguage(track)));
                if (track.HasSyncedVersion !== undefined ? track.HasSyncedVersion : track.hasSyncedVersion) {
                    meta2.appendChild(el('span', 'ss-synced', '  \u2713 synced before'));
                }
                name.appendChild(meta2);
                var button = primaryButton('Sync');
                button.addEventListener('click', function () {
                    button.disabled = true;
                    button.textContent = 'Starting\u2026';
                    api(SYNC_BASE + '/Sync', {
                        method: 'POST',
                        body: JSON.stringify({ itemId: meta.id, subtitleIndex: trackIndex(track) })
                    }).then(function (job) {
                        var jobId = job.Id || job.id;
                        button.textContent = 'Syncing\u2026';
                        var started = Date.now();
                        shell.state.timer = setInterval(function () {
                            api(SYNC_BASE + '/Jobs/' + jobId).then(function (status) {
                                var state = status.Status || status.status;
                                var progress = status.Progress !== undefined ? status.Progress : (status.progress || 0);
                                var phase = status.Phase || status.phase || 'Working';
                                var elapsed = Math.round((Date.now() - started) / 1000);
                                var minutes = Math.floor(elapsed / 60);
                                var seconds = elapsed % 60;
                                shell.setProgress(progress, [phase], minutes + ':' + (seconds < 10 ? '0' : '') + seconds);
                                button.textContent = Math.round(progress * 100) + '%';
                                if (state === 'Completed' || state === 'Failed' || state === 'Cancelled') {
                                    clearInterval(shell.state.timer);
                                    shell.state.timer = null;
                                    button.textContent = state === 'Completed' ? 'Synced' : state;
                                    button.disabled = state !== 'Completed';
                                    var outcome = status.Outcome || status.outcome;
                                    if (state === 'Completed') {
                                        shell.setProgress(1, ['Synced', outcome || 'done']);
                                    } else {
                                        shell.showError(status.Error || status.error || 'The sync did not finish.');
                                    }
                                }
                            }).catch(function (err) {
                                clearInterval(shell.state.timer);
                                shell.state.timer = null;
                                shell.showError('Lost contact with the job: ' + (err.message || err));
                                button.disabled = false;
                                button.textContent = 'Sync';
                            });
                        }, 1500);
                    }).catch(function (err) {
                        button.disabled = false;
                        button.textContent = 'Sync';
                        shell.showError('Could not start: ' + (err.message || err));
                    });
                });
                row.appendChild(name);
                row.appendChild(button);
                shell.body.appendChild(row);
            });
        }).catch(function (err) {
            shell.sub.textContent = 'Could not read the subtitles.';
            shell.showError(err.message || String(err));
        });
    }

    // ---------------------------------------------------------------- menu injection

    var lastMenuCardId = null;
    var lastMenuCardTime = 0;

    document.addEventListener('click', function (e) {
        var target = e.target;
        if (!target || !target.closest) {
            return;
        }
        var button = target.closest('[data-action="menu"], [data-action="openmenu"], .itemActionButton, button[data-role="menu"]');
        if (!button) {
            return;
        }
        var holder = button.closest('[data-id]');
        if (holder) {
            lastMenuCardId = holder.getAttribute('data-id');
            lastMenuCardTime = Date.now();
        }
    }, true);

    function getItemIdFromHash() {
        var hash = location.hash || '';
        var match = hash.match(/[?&]id=([0-9a-fA-F-]{32,36})/);
        return match ? match[1] : null;
    }

    function openDialog(itemId) {
        fetchMeta(itemId).then(function (meta) {
            if (BULK_TYPES[meta.type]) {
                openBulkDialog(meta);
                return;
            }
            if (SINGLE_TYPES[meta.type] || meta.mediaType === 'Video') {
                openSingleDialog(meta);
                return;
            }
            // A container that is neither a series nor a season (folder, collection, playlist):
            // say so instead of opening a dialog that would fail later.
            var shell = buildShell('Nothing to sync here', meta.name || '');
            shell.sub.textContent = 'This item is a ' + (meta.type || 'container').toLowerCase()
                + '. Use the menu on a series, a season, or a single video.';
        }).catch(function (err) {
            log('Could not read the item: ' + (err.message || err));
        });
    }

    function hookActionSheets() {
        var injectPending = false;

        function maybeInject() {
            if (injectPending) {
                return;
            }
            if (document.querySelector('.actionSheetContent [data-id="subsync"]')) {
                return;
            }
            var sheet = document.querySelector('.actionSheetContent');
            if (!sheet) {
                return;
            }

            var cardFresh = lastMenuCardId && (Date.now() - lastMenuCardTime < 3000);
            var itemId = cardFresh ? lastMenuCardId : getItemIdFromHash();
            if (!itemId) {
                return;
            }
            lastMenuCardId = null;
            lastMenuCardTime = 0;

            injectPending = true;
            fetchMeta(itemId).then(function (meta) {
                injectPending = false;
                var isBulk = !!BULK_TYPES[meta.type];
                var isSingle = !!SINGLE_TYPES[meta.type] || meta.mediaType === 'Video';
                if (!isBulk && !isSingle) {
                    return; // folders, collections, playlists: no subtitle sync of their own
                }

                var scroller = sheet.querySelector('.actionSheetScroller') || sheet;
                var menuItem = document.createElement('button');
                menuItem.setAttribute('is', 'emby-button');
                menuItem.setAttribute('type', 'button');
                menuItem.setAttribute('data-id', 'subsync');
                menuItem.className = 'listItem listItem-button actionSheetMenuItem emby-button';
                menuItem.innerHTML =
                    '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons subtitles" aria-hidden="true"></span>' +
                    '<div class="listItemBody actionsheetListItemBody">' +
                    '<div class="listItemBodyText actionSheetItemText">' + (isBulk ? 'Sync all episodes' : 'Sync Subtitles') + '</div>' +
                    '</div>';
                menuItem.addEventListener('click', function () {
                    openDialog(itemId);
                });

                var editSubs = sheet.querySelector('[data-id="editsubtitles"]');
                if (editSubs && editSubs.nextSibling && scroller.contains(editSubs)) {
                    scroller.insertBefore(menuItem, editSubs.nextSibling);
                } else {
                    scroller.appendChild(menuItem);
                }
                log('Injected "' + (isBulk ? 'Sync all episodes' : 'Sync Subtitles') + '" for ' + meta.type + ' ' + (meta.name || itemId));
            }).catch(function () {
                injectPending = false;
                // Could not read the item (offline, or a container the API would not answer for):
                // keep the menu item on a detail page so the dialog can explain itself.
                if (!getItemIdFromHash()) {
                    return;
                }
                var scroller = sheet.querySelector('.actionSheetScroller') || sheet;
                if (scroller.querySelector('[data-id="subsync"]')) {
                    return;
                }
                var fallback = document.createElement('button');
                fallback.setAttribute('is', 'emby-button');
                fallback.setAttribute('type', 'button');
                fallback.setAttribute('data-id', 'subsync');
                fallback.className = 'listItem listItem-button actionSheetMenuItem emby-button';
                fallback.innerHTML =
                    '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons subtitles" aria-hidden="true"></span>' +
                    '<div class="listItemBody actionsheetListItemBody">' +
                    '<div class="listItemBodyText actionSheetItemText">Sync Subtitles</div>' +
                    '</div>';
                var hashItemId = getItemIdFromHash();
                fallback.addEventListener('click', function () { openDialog(hashItemId); });
                scroller.appendChild(fallback);
            });
        }

        var observer = new MutationObserver(function () {
            if (!document.querySelector('.actionSheetContent')) {
                return;
            }
            maybeInject();
        });
        observer.observe(document.documentElement, { childList: true, subtree: true });
    }

    // Jellyfin 12 injects the plugin page as markup, and a <script src> that arrives with injected
    // markup is fetched but never executed here: the page rendered with every control in place, the
    // status line stayed on "Checking status..." and the page made no request of any kind. The same
    // script, attached from this file, runs and fills the page in (measured in a browser). So the page's
    // script is attached here — this file is injected into the web client by SubSyncMiddleware, the path
    // that already works — and re-attached whenever the web client replaces the page element.
    function attachPluginPageScript() {
        var pageEl = document.getElementById('subsyncMainPage');
        if (!pageEl || pageEl.getAttribute('data-ss-script') === 'attached') {
            return;
        }
        pageEl.setAttribute('data-ss-script', 'attached');

        // The page runs in this document but cannot always reach the web client's API object — Jellyfin 12
        // does not define window.ApiClient for a plugin page, which left the page unable to authenticate and
        // stuck on "Loading libraries…". This script is injected into the web client itself, where the API
        // object (and the session) definitely exist, so the page gets both from here.
        try {
            var api = (typeof ApiClient !== 'undefined') ? ApiClient : null;
            window.__subsyncBridge = {
                token: (api && api.accessToken) ? api.accessToken() : '',
                userId: (api && api.getCurrentUserId) ? api.getCurrentUserId() : '',
                at: Date.now()
            };
            log('Handed the page its session (' + (window.__subsyncBridge.token ? 'token' : 'no token')
                + (window.__subsyncBridge.userId ? ', user' : ', no user') + ')');
        } catch (e) {
            window.__subsyncBridge = { token: '', userId: '', at: Date.now() };
            log('Could not read the session for the page: ' + (e && e.message ? e.message : e));
        }
        // The page's own tag is fetched but not run in Jellyfin 12, so the two ways of attaching it are
        // tried in the order they were measured: fetching the text and running it as a blob (measured to
        // work) first, and the plain src tag as the fallback.
        fetch('/SubSync/MainScript')
            .then(function (r) { return r.ok ? r.text() : Promise.reject(new Error('HTTP ' + r.status)); })
            .then(function (src) {
                var script = document.createElement('script');
                script.src = URL.createObjectURL(new Blob([src], { type: 'application/javascript' }));
                document.head.appendChild(script);
                log('Plugin page script attached');
            })
            ['catch'](function (e) {
                var script = document.createElement('script');
                script.src = '/SubSync/MainScript';
                document.head.appendChild(script);
                log('Plugin page script attached (fallback): ' + (e && e.message ? e.message : e));
            });
    }

    var pageObserver = new MutationObserver(function () {
        attachPluginPageScript();
    });
    pageObserver.observe(document.documentElement, { childList: true, subtree: true });
    attachPluginPageScript();

    hookActionSheets();
    log('Client script loaded');
})();

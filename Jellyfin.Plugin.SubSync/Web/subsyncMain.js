/* SubSync — the plugin page's client script.

   Served at /SubSync/MainScript and referenced by subsyncMain.html. It lives in its own file because
   Jellyfin 12 injects the plugin page as markup, where an inline <script> never executes: the page
   rendered but was completely inert (no request of any kind, the status line stuck on "Checking
   status..."). A script the page loads with a src is fetched and run normally — measured in a real
   browser: window.__ssMainScriptRan === true, and the same page's inline script never ran. */

(function () {
    'use strict';

    // The page's own <script src> is fetched but not executed in Jellyfin 12, so the script is attached
    // by the client script the middleware injects (see subsync.js). A client that does execute the tag
    // would attach a second copy: the first one wins, the second does nothing.
    window.__ssTrace = window.__ssTrace || [];
    if (window.__subsyncPageLoaded) {
        window.__ssTrace.push('skipped: already loaded');
        return;
    }

    window.__subsyncPageLoaded = true;
    window.__ssTrace.push('loaded');

    // Start on the first signal that arrives, from up here and not from the last line of the file: this
    // script is injected into a page the Jellyfin web client builds, and a client that replaces the
    // document while the script is still executing cuts the rest of the file off (measured: the first
    // line ran, nothing after the declarations did, and no error was reported anywhere). The listeners
    // below are registered before anything else can go wrong.
    document.addEventListener('DOMContentLoaded', whenApiReady);
    window.addEventListener('pageshow', whenApiReady);
    window.addEventListener('load', whenApiReady);
    setTimeout(whenApiReady, 0);

    var PLUGIN_ID = 'c7d8e9f0-a1b2-4c3d-e5f6-a7b8c9d0e1f2';
    var allItems = [];
    var libraries = [];
    var selected = null;        // selected library item
    var scopeTarget = null;     // itemId actually synced (series or season)
    var movieTracks = [];       // subtitle tracks of the selected movie
    var busy = false;
    var startInFlight = false; // a queue-build POST is in flight — prevents double submits
    var selectedIds = {};      // ids picked by right-click (multi-select)
    var shiftAnchor = -1;      // last checkbox clicked, for shift-range selection

    var STATE_KEY = 'subsync.state.v1';
    // Largest worker count the server accepts; refreshed from every batch view so the
    // two cannot drift apart. Keep in step with SubSyncService.MaxParallelWorkers.
    var workerCeiling = 64;
    var pendingScope = null; // scope to restore once seasons load
    var lastInitAt = 0;
    var reattached = false; // a run was reattached after page load
    var attachPending = false; // batch lookup in flight — don't hide the box meanwhile

    // Storage that survives reloads (used for UI state only — history
    // now lives on the server with the batches).
    var storageMode = 'none';
    function store() {
        try {
            if (window.localStorage) { localStorage.setItem('__subsync_probe__', '1'); localStorage.removeItem('__subsync_probe__'); storageMode = 'localStorage'; return localStorage; }
        } catch (e) { /* fall through */ }
        try {
            if (window.sessionStorage) { sessionStorage.setItem('__subsync_probe__', '1'); sessionStorage.removeItem('__subsync_probe__'); storageMode = 'sessionStorage'; return sessionStorage; }
        } catch (e) { /* fall through */ }
        var mem = {};
        return { getItem: function (k) { return Object.prototype.hasOwnProperty.call(mem, k) ? mem[k] : null; }, setItem: function (k, v) { mem[k] = String(v); } };
    }
    var storeImpl = store();

    function loadState() {
        try { var s = JSON.parse(storeImpl.getItem(STATE_KEY)); return (s && typeof s === 'object') ? s : {}; }
        catch (e) { return {}; }
    }
    function saveState(patch) {
        try {
            var s = loadState();
            for (var k in patch) { if (Object.prototype.hasOwnProperty.call(patch, k)) s[k] = patch[k]; }
            storeImpl.setItem(STATE_KEY, JSON.stringify(s));
        } catch (e) { /* state just won't persist */ }
    }

    function $(id) { return document.getElementById(id); }
    function esc(v) {
        return String(v == null ? '' : v).replace(/[&<>"]/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
        });
    }

    function diag(msg, isErr) {
        var el = $('ss-diag');
        if (!msg) { el.classList.add('ss-hidden'); el.textContent = ''; return; }
        el.textContent = msg;
        el.classList.remove('ss-hidden');
        el.classList.toggle('err', !!isErr);
    }

    // Jellyfin 12 disables the legacy authorization mechanisms by default: the old
    // `X-Emby-Token` header and the `?api_key=` query parameter are ignored there. The
    // `Authorization: MediaBrowser Token="..."` header and `?ApiKey=` are what
    // jellyfin-web sends and both are accepted by 10.11 as well.
    function bridge() {
        return (typeof window !== 'undefined' && window.__subsyncBridge) ? window.__subsyncBridge : null;
    }

    function token() {
        if (typeof ApiClient !== 'undefined' && ApiClient && ApiClient.accessToken) {
            return ApiClient.accessToken();
        }
        // Second source: the client script (SubSync.js) runs in the web client itself, where the API object
        // exists, and hands the page its token and user id. This is what makes the page work when
        // window.ApiClient is undefined here, which is the normal case for a plugin page in Jellyfin 12.
        var handed = bridge();
        if (handed && handed.token) {
            return handed.token;
        }
        // The web client keeps its session in localStorage and this page is injected into that client,
        // so the page can read its own token. Without it nothing works when window.ApiClient is absent
        // (Jellyfin 12 does not define it early; this instance never defines it — see whenApiReady).
        try {
            var raw = localStorage.getItem('jellyfin_credentials');
            if (raw) {
                var servers = (JSON.parse(raw) || {}).Servers;
                if (servers && servers.length) {
                    return servers[0].AccessToken || '';
                }
            }
        } catch (e) { }
        return '';
    }

    // The page asks for the signed-in user in five places, all of them for "my runs". The web client's
    // answer is used when it is there; otherwise the server is asked once (see whenApiReady).
    var cachedUserId = '';
    function currentUserId() {
        try {
            if (typeof ApiClient !== 'undefined' && ApiClient && ApiClient.getCurrentUserId) {
                var mine = ApiClient.getCurrentUserId();
                if (mine) { return mine; }
            }
        } catch (e) { /* fall through to the handed-over session */ }

        var handed = bridge();
        if (handed && handed.userId) {
            return handed.userId;
        }

        return cachedUserId;
    }

    // Primes the user id whenever it cannot be read locally — not only when the API object is missing, which
    // is what left the page unable to say which user it was asking for.
    function primeUserId() {
        if (currentUserId()) {
            return Promise.resolve();
        }

        return api('Users/Me').then(function (me) {
            cachedUserId = (me && me.Id) || '';
            if (!cachedUserId) {
                throw new Error('the server did not report a signed-in user');
            }
        });
    }

    // Every early return that leaves the interface unfinished has to say so where the user is looking.
    function explain(text) {
        diag(text, true);
        var dl = $('ss-dataline');
        if (dl) { dl.textContent = text; dl.classList.add('err'); }
        var st = $('ss-status');
        if (st && !/ffsubsync source/.test(st.textContent || '')) { st.textContent = text; }
    }

    function authHeader() {
        return 'MediaBrowser Token="' + token().replace(/"/g, '') + '"';
    }

    function apiUrl(path) {
        return (typeof ApiClient !== 'undefined' && ApiClient.getUrl) ? ApiClient.getUrl(path) : '/' + path;
    }

    // One shape for a failure, so the page can show the reason the server gave (D10). Every failing SubSync
    // endpoint answers with { status, title, detail }; a bare body is still possible (a proxy, an older build) and
    // is shown as it arrived rather than hidden.
    function problemText(status, body) {
        var text = (body || '').trim();
        if (text) {
            try {
                var parsed = JSON.parse(text);
                var detail = parsed && (parsed.detail || parsed.Detail);
                var title = parsed && (parsed.title || parsed.Title);
                if (detail && title && detail !== title) return title + ': ' + detail;
                if (detail || title) return detail || title;
            } catch (ex) {
                // Not JSON: whatever the server said is the message.
            }
            return text;
        }
        return 'HTTP ' + status;
    }

    // Requests in flight, so several callers wanting the same thing in the same moment share one round trip (D7).
    // A page load used to fire the same `/SubSync/Batches` several times: the four load signals, the initial
    // refresh, and the heartbeat's first tick all asked for it independently, and on a slow server each answer
    // arrived after the next question had been sent.
    var inflightGets = {};

    // A GET answer is also good for a moment after it arrives, so the second and third caller of a page load do not
    // ask the same question again (measured at load before this: the same `/SubSync/Batches` three times, because the
    // initial refresh, the load signals and the heartbeat's first tick each wanted it). Any write throws the whole
    // cache away, so a page can never read a stale list after queueing, cancelling or saving something.
    var recentGets = {};
    var GET_REUSE_MS = 500;

    function api(path, options) {
        options = options || {};
        var method = options.method || 'GET';
        var key = method + ' ' + path;
        if (method === 'GET') {
            var recent = recentGets[key];
            if (recent && (Date.now() - recent.at) < GET_REUSE_MS) {
                return recent.promise;
            }
            if (inflightGets[key]) {
                return inflightGets[key];
            }
        }
        var headers = { 'Accept': 'application/json', 'Authorization': authHeader() };
        if (options.body) headers['Content-Type'] = 'application/json';
        var request = fetch(apiUrl(path), {
            method: method,
            headers: headers,
            body: options.body ? options.body : undefined
        }).then(function (r) {
            if (!r.ok) {
                return r.text().then(function (b) {
                    throw new Error(problemText(r.status, b));
                });
            }
            return r.status === 204 ? null : r.json();
        });
        if (method === 'GET') {
            inflightGets[key] = request;
            var remember = function () {
                delete inflightGets[key];
                recentGets[key] = { at: Date.now(), promise: request };
            };
            var dropFailed = function () { delete inflightGets[key]; delete recentGets[key]; };
            request.then(remember, dropFailed);
        } else {
            // Everything on the page is derived from what the server says: after a write, nothing cached may be reused.
            recentGets = {};
        }
        return request;
    }

    /** One result as a row: the state as a word, the file, and the note. Built in one place so a run's detail
     *  in History (D1) has one definition to render - the live progress panel no longer shows results at all
     *  (see the removal note below).
     *
     *  The word and the colour come from one table, and every status the server reports has a row in it.
     *  They used to come from a three-way ternary - "Synced" for Completed, "Skipped" for Cancelled,
     *  **"Failed" for everything else** - and "everything else" is where Queued and Running fall. Opening a
     *  run in History while it was still going therefore painted every task that had not started yet as
     *  Failed in red: measured on the rig against the shipped code, a ten-task batch spent its whole life
     *  with 1-10 tasks reading "Failed" (`Queued -> "Failed" (ss-task-bad)`, `Running -> "Failed"
     *  (ss-task-bad)`), i.e. the panel told the user the run had failed where nothing had gone wrong. A
     *  status the table has never seen is not a failure either - it says what it is, in the neutral colour,
     *  because an unknown word must not be an accusation. */
    var TASK_STATES = {
        Completed: { word: 'Synced', kind: 'ok' },
        Cancelled: { word: 'Skipped', kind: 'skip' },
        Failed: { word: 'Failed', kind: 'bad' },
        Refused: { word: 'Refused', kind: 'skip' },
        Running: { word: 'Running', kind: 'pending' },
        Queued: { word: 'Queued', kind: 'pending' }
    };

    function taskState(status) {
        return TASK_STATES[status] || { word: status || 'Unknown', kind: 'pending' };
    }

    function buildTaskRow(status, title, note) {
        var row = document.createElement('div');
        var state = taskState(status);
        row.className = 'ss-task ss-task-' + state.kind;

        var stateEl = document.createElement('span');
        stateEl.className = 'ss-task-state';
        stateEl.textContent = state.word;

        var name = document.createElement('span');
        name.className = 'ss-task-name';
        name.textContent = title || '';

        row.appendChild(stateEl);
        row.appendChild(name);
        if (note) {
            var detail = document.createElement('span');
            detail.className = 'ss-task-note';
            detail.textContent = note;
            row.appendChild(detail);
        }
        return row;
    }

    // G2: an image-based subtitle track (Blu-ray PGS, DVD VobSub, DVB, XSUB) carries no text, so the engine
    // can never align it. The server refuses one at enqueue - the refusal deliberately becomes a failed task
    // of whichever run asked for it, so the run reports what happened instead of silently dropping work - and
    // this page used to hand it those tracks anyway: the row's track picker listed one as selectable, "All N
    // tracks" counted it, the language filter offered a language carried only by one, and the button counted
    // it. Measured on the user's own server before this change: five refusals in one afternoon of testing
    // ("task 3 failed validation: This track is DVDSUB, an image subtitle format..."), each counted as a
    // failure of an otherwise fine run, and one of them queued from the library row with no language filter
    // set. The listing still shows such a track and says why (S5 - hiding it made the list disagree with the
    // file); what changes is that nothing here can queue it.
    function trackUnsupported(t) {
        return !!((t && (t.UnsupportedReason !== undefined ? t.UnsupportedReason : t.unsupportedReason)) || '');
    }

    /** The tracks of a list that can actually be queued, in the order they were listed. */
    function syncableTracks(tracks) {
        return (tracks || []).filter(function (t) { return !trackUnsupported(t); });
    }

    /** Why a track cannot be synced, for the one place that shows it. */
    function trackUnsupportedReason(t) {
        return (t && (t.UnsupportedReason !== undefined ? t.UnsupportedReason : t.unsupportedReason)) || '';
    }

    /** A count with its noun, so the page never prints "1 subtitles". */
    function plural(n, one, many) {
        return n + ' ' + (n === 1 ? one : (many || one + 's'));
    }

    /** One task's result as a single note: the outcome, the reader's cost, and where it was written. */
    function taskResultNote(outcome, extractNote, outPath) {
        var parts = [];
        if (outcome) parts.push(outcome);
        if (extractNote) parts.push(extractNote);
        if (outPath) parts.push('wrote ' + outPath);
        return parts.join(' \u00b7 ');
    }

    // D1: History is a list of runs, not a terminal. One row per run, grouped under the day it started, with
    // a state chip ("Succeeded", "Partly failed", ...) and the numbers that describe it; opening a run shows
    // what happened to each subtitle as rows, a switch for the problems only, and the raw report behind a
    // "Copy as text" link. What the old <pre> of OK/FAIL lines was for - pasting a run into a bug report - is
    // the link; the log itself is no longer the surface.

    /** The run's state as a chip: what a reader wants to know before the counts. */
    function historyState(b) {
        var status = b.Status || b.status || '';
        var ok = (b.Ok != null) ? b.Ok : (b.ok || 0);
        var failed = (b.Failed != null) ? b.Failed : (b.failed || 0);
        var total = (b.Total != null) ? b.Total : (b.total || 0);
        if (status === 'Queued') return { chip: 'Queued', cls: 'partial' };
        if (status === 'Running') return { chip: 'Running', cls: 'partial' };
        if (status === 'Cancelled') return { chip: 'Cancelled', cls: 'partial' };
        if (failed > 0 && ok > 0) return { chip: 'Partly failed', cls: 'partial' };
        if (failed > 0 || status === 'Failed') return { chip: 'Failed', cls: 'fail' };
        return { chip: total > 0 ? 'Succeeded' : (status || 'Finished'), cls: 'ok' };
    }

    /** How long the run took, from the two timestamps the server keeps. */
    function historyDuration(b) {
        var started = Date.parse(b.CreatedAtUtc || b.createdAtUtc || '');
        var finished = Date.parse(b.FinishedAtUtc || b.finishedAtUtc || '');
        if (!started) return '';
        if (!finished) return 'still running';
        var seconds = Math.max(1, Math.round((finished - started) / 1000));
        if (seconds < 90) return seconds + ' s';
        return Math.round(seconds / 60) + ' min';
    }

    function historyCountsLine(b) {
        var total = (b.Total != null) ? b.Total : (b.total || 0);
        var ok = (b.Ok != null) ? b.Ok : (b.ok || 0);
        var failed = (b.Failed != null) ? b.Failed : (b.failed || 0);
        var cancelled = (b.Cancelled != null) ? b.Cancelled : (b.cancelled || 0);
        var bits = [plural(total, 'subtitle'), ok + ' synced'];
        if (failed) bits.push(failed + ' failed');
        if (cancelled) bits.push(cancelled + ' cancelled');
        var time = historyDuration(b);
        if (time) bits.push(time);
        return bits.join(' \u00b7 ');
    }

    function historyDayLabel(date) {
        var today = new Date();
        var sameDay = function (a, b) {
            return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
        };
        var yesterday = new Date(today.getTime() - 86400000);
        if (sameDay(date, today)) return 'Today';
        if (sameDay(date, yesterday)) return 'Yesterday';
        return date.toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
    }

    /** The folders a run wrote into, said as one path when they agree. */
    function historyOutputFolder(tasks) {
        var folders = {};
        tasks.forEach(function (t) {
            var path = t.OutputPath || t.outputPath || '';
            if (!path) return;
            var cut = path.lastIndexOf('/');
            folders[cut > 0 ? path.slice(0, cut) : path] = 1;
        });
        var keys = Object.keys(folders);
        if (keys.length === 1) return keys[0];
        if (keys.length > 1) return keys.length + ' folders';
        return '';
    }

    function historyStatusOf(t) {
        return t.Status || t.status || '';
    }

    function isTerminalStatus(status) {
        return status === 'Completed' || status === 'Failed' || status === 'Cancelled';
    }

    /** A task the server has not finished: it has no result yet, so nothing may be said about it as one
     *  (and it must never be counted as a failure - see the note on the state table). */
    function isPendingStatus(status) {
        return status === 'Queued' || status === 'Running';
    }

    /** The rows of one run: grouped by file, so a movie with four tracks reads as one file with four results
     *  rather than four unrelated lines. */
    function historyTaskRows(tasks, problemsOnly) {
        var fragment = document.createDocumentFragment();
        var byFile = {};
        var order = [];
        tasks.forEach(function (t) {
            var title = t.Title || t.title || ('track ' + (t.SubtitleIndex != null ? t.SubtitleIndex : (t.BatchIndex || 0)));
            var file = title.indexOf(' \u2014 ') > 0 ? title.slice(0, title.indexOf(' \u2014 ')) : '';
            var track = file ? title.slice(title.indexOf(' \u2014 ') + 3) : title;
            var status = historyStatusOf(t);
            if (!byFile[file]) {
                byFile[file] = [];
                order.push(file);
            }
            byFile[file].push({ track: track, status: status, task: t });
        });

        order.forEach(function (file) {
            var entries = byFile[file];
            var shown = entries.filter(function (e) {
                return !problemsOnly || e.status === 'Failed';
            });
            if (!shown.length) return;
            if (file && entries.length > 1) {
                var head = document.createElement('div');
                head.className = 'ss-hist-subhead';
                head.textContent = file + ' \u2014 ' + plural(entries.length, 'track');
                fragment.appendChild(head);
            }
            shown.forEach(function (e) {
                var t = e.task;
                var note;
                if (e.status === 'Completed') {
                    note = taskResultNote(t.Outcome || t.outcome || '', t.ExtractionNote || t.extractionNote || '',
                                          t.OutputPath || t.outputPath || '');
                } else if (e.status === 'Cancelled') {
                    note = 'cancelled before it started';
                } else if (isPendingStatus(e.status)) {
                    // A queued or running task has no result to report: it says why it is waiting, when the
                    // server knows (the phase carries it), and nothing when it does not. This used to read
                    // the task's own status word, which is how a pending row said "Queued" as its *note*
                    // under a state word that claimed it had failed.
                    note = t.Phase || t.phase || '';
                } else {
                    note = t.Error || t.error || t.Status || e.status;
                }
                fragment.appendChild(buildTaskRow(e.status, file && entries.length > 1 ? e.track : (file || e.track), note));
            });
        });
        return fragment;
    }

    /** The detail of one run: what it was, what it did, and the two controls that make it readable. */
    function renderHistoryDetail(detail, view) {
        detail.innerHTML = '';
        var tasks = (view.Tasks || view.tasks || []);
        var failed = tasks.filter(function (t) { return historyStatusOf(t) === 'Failed'; }).length;
        var state = historyState(view);
        var time = historyDuration(view);
        var folder = historyOutputFolder(tasks);

        var summary = document.createElement('div');
        summary.className = 'ss-hist-summary';
        var bits = [state.chip + (time ? ' in ' + time : ''),
                    plural(tasks.length, 'subtitle'),
                    tasks.filter(function (t) { return isTerminalStatus(historyStatusOf(t)); }).length + ' finished'];
        if (failed) bits.push(failed + ' failed');
        var mode = view.Mode || view.mode || '';
        if (mode) bits.push(mode + ' mode');
        if (folder) bits.push('outputs in ' + folder);
        summary.textContent = bits.join(' \u00b7 ');
        detail.appendChild(summary);

        var bar = document.createElement('div');
        bar.className = 'ss-hist-filter';
        var allBtn = document.createElement('button');
        allBtn.type = 'button';
        allBtn.className = 'ss-hist-filter-btn ss-hist-filter-on';
        allBtn.textContent = 'All ' + tasks.length;
        var problemBtn = document.createElement('button');
        problemBtn.type = 'button';
        problemBtn.className = 'ss-hist-filter-btn';
        problemBtn.textContent = 'Problems only' + (failed ? ' ' + failed : '');
        var copyLink = document.createElement('a');
        copyLink.href = '#';
        copyLink.className = 'ss-hist-copy';
        copyLink.textContent = 'Copy as text';
        var copyState = document.createElement('span');
        copyState.className = 'ss-muted';
        copyState.style.fontSize = '.78rem';
        bar.appendChild(allBtn);
        bar.appendChild(problemBtn);
        bar.appendChild(copyLink);
        bar.appendChild(copyState);
        detail.appendChild(bar);

        var rows = document.createElement('div');
        rows.className = 'ss-tasks ss-hist-rows';
        function draw(problemsOnly) {
            rows.innerHTML = '';
            rows.appendChild(historyTaskRows(tasks, problemsOnly));
            if (!rows.children.length) {
                var none = document.createElement('div');
                none.className = 'ss-task-note-row';
                none.textContent = problemsOnly ? 'No problems in this run.' : 'No results recorded for this run.';
                rows.appendChild(none);
            }
        }
        allBtn.addEventListener('click', function () {
            allBtn.classList.add('ss-hist-filter-on');
            problemBtn.classList.remove('ss-hist-filter-on');
            draw(false);
        });
        problemBtn.addEventListener('click', function () {
            problemBtn.classList.add('ss-hist-filter-on');
            allBtn.classList.remove('ss-hist-filter-on');
            draw(true);
        });
        copyLink.addEventListener('click', function (e) {
            e.preventDefault();
            copyHistoryText(view, copyState);
        });
        draw(false);
        detail.appendChild(rows);
    }

    /** The old log shape, on the clipboard - what the terminal block was actually used for. */
    function copyHistoryText(view, statusEl) {
        var text = batchToLines(view);
        var ok = function () {
            statusEl.textContent = 'copied';
            setTimeout(function () { statusEl.textContent = ''; }, 2500);
        };
        var fallback = function () {
            var area = document.createElement('textarea');
            area.value = text;
            area.style.position = 'fixed';
            area.style.top = '-1000px';
            document.body.appendChild(area);
            area.select();
            var copied = false;
            try { copied = document.execCommand('copy'); } catch (err) { copied = false; }
            document.body.removeChild(area);
            statusEl.textContent = copied ? 'copied' : 'could not copy - open the run and select the text';
        };
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text).then(ok, fallback);
        } else {
            fallback();
        }
    }

    function refreshHistory() {
        // History comes from the SERVER now (batches): every tab and
        // device attached to the server sees the same runs.
        api('SubSync/Batches').then(function (batches) {
            var list = (batches || []).slice().sort(function (a, b) {
                return Date.parse((b.CreatedAtUtc || b.createdAtUtc) || 0) - Date.parse((a.CreatedAtUtc || a.createdAtUtc) || 0);
            });
            var el = $('ss-history');
            el.innerHTML = '';
            var stats = $('ss-history-stats');
            var failedRuns = list.filter(function (b) { return historyState(b).cls !== 'ok'; }).length;
            if (stats) {
                stats.textContent = plural(list.length, 'run') + ' on this server'
                    + (failedRuns ? ' \u00b7 ' + failedRuns + ' with a failure' : '')
                    + ' \u00b7 shared across tabs and devices';
            }
            $('ss-history-empty').classList.toggle('ss-hidden', list.length > 0);

            var currentDay = null;
            list.forEach(function (b) {
                var created = Date.parse((b.CreatedAtUtc || b.createdAtUtc || ''));
                if (created) {
                    var day = historyDayLabel(new Date(created));
                    if (day !== currentDay) {
                        currentDay = day;
                        var head = document.createElement('div');
                        head.className = 'ss-hist-day';
                        head.textContent = day;
                        el.appendChild(head);
                    }
                }

                var state = historyState(b);
                var item = document.createElement('div');
                item.className = 'ss-hist-item';

                var top = document.createElement('div');
                top.className = 'ss-hist-head';
                var title = document.createElement('span');
                title.className = 'ss-hist-title';
                title.textContent = b.Label || b.label || 'Run';
                var right = document.createElement('span');
                right.className = 'ss-hist-right';
                var when = document.createElement('span');
                when.className = 'ss-hist-meta';
                when.textContent = created ? new Date(created).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }) : '';
                var chip = document.createElement('span');
                chip.className = 'ss-hist-badge ' + state.cls;
                chip.textContent = state.chip;
                right.appendChild(when);
                right.appendChild(chip);
                top.appendChild(title);
                top.appendChild(right);

                var counts = document.createElement('div');
                counts.className = 'ss-hist-meta ss-hist-counts';
                counts.textContent = historyCountsLine(b);

                var detail = document.createElement('div');
                detail.className = 'ss-hist-detail ss-hidden';

                item.appendChild(top);
                item.appendChild(counts);
                item.appendChild(detail);
                item.addEventListener('click', function (e) {
                    if (e.target.closest('.ss-hist-detail')) return;
                    var opening = detail.classList.contains('ss-hidden');
                    detail.classList.toggle('ss-hidden', !opening);
                    if (opening && !detail.getAttribute('data-loaded')) {
                        detail.setAttribute('data-loaded', '1');
                        detail.innerHTML = '';
                        var loading = document.createElement('div');
                        loading.className = 'ss-task-note-row';
                        loading.textContent = 'Loading\u2026';
                        detail.appendChild(loading);
                        api('SubSync/Batch/' + (b.Id || b.id)).then(function (view) {
                            if (view) renderHistoryDetail(detail, view);
                        }).catch(function (err) {
                            detail.innerHTML = '';
                            var failed = document.createElement('div');
                            failed.className = 'ss-task-note-row';
                            failed.textContent = 'Could not load this run: ' + (err.message || err);
                            detail.appendChild(failed);
                        });
                    }
                });
                el.appendChild(item);
            });
        }).catch(function () {
            var stats = $('ss-history-stats');
            if (stats) stats.textContent = 'History unavailable \u2014 could not reach the server.';
        });
    }

    function batchToLines(view) {
        // Render a server batch view as readable log lines (same format
        // as live progress: OK/FAIL per task with the written path).
        // Queued and running tasks are not results: they are counted, not called failures.
        var lines = [];
        var tasks = (view && view.Tasks) || (view && view.tasks) || [];
        var pending = 0;
        tasks.forEach(function (t) {
            var title = t.Title || t.title || ('track ' + (t.SubtitleIndex != null ? t.SubtitleIndex : (t.BatchIndex || 0)));
            var status = t.Status || t.status;
            var error = t.Error || t.error;
            var outPath = t.OutputPath || t.outputPath;
            var outcome = t.Outcome || t.outcome || '';
            var extractNote = t.ExtractionNote || t.extractionNote || '';
            if (status === 'Completed') lines.push('OK   ' + title + (outPath ? ' \u2192 ' + outPath : '') + (outcome || extractNote ? ' (' + [outcome, extractNote].filter(Boolean).join(' \u00b7 ') + ')' : ''));
            else if (status === 'Cancelled') lines.push('SKIP ' + title + ' \u2014 cancelled');
            else if (status === 'Failed') lines.push('FAIL ' + title + ' \u2014 ' + (error || status));
            else pending++;
        });
        if (pending > 0) {
            lines.push('(' + pending + ' task' + (pending === 1 ? '' : 's') + ' still queued or running)');
        }
        return lines.length ? lines.join('\n') : '(no tasks recorded)';
    }

    // One row per task currently syncing. Only parallel/ultimate modes have
    // more than one, but the panel is harmless for a single task and makes the
    // phase visible while it works.
    // Only say something when there is something to say: plain sequential work is the
    // normal case and needs no badge.
    function modeBadge(mode) {
        switch ((mode || '').toLowerCase()) {
            case 'ultimate': return 'parallel';
            case 'parallel': return 'parallel';
            case 'fast': return 'reusing the audio analysis';
            default: return '';
        }
    }

    // Every worker slot is drawn, busy or not, so the panel answers "how much of the
    // configured parallelism is actually in use" at a glance. A batch waiting behind another
    // one shows nothing of its own - so the slots are filled from whatever is running on the
    // server, which is what the user is waiting for.
    var lastActive = { running: [], queued: 0 };

    function refreshActive() {
        return api('SubSync/Active').then(function (active) {
            lastActive = active || { running: [], queued: 0 };
        }).catch(function () { /* keep the last known state */ });
    }

    function normalizeRunning(t) {
        return {
            Title: t.Title || t.title || '',
            Phase: t.Phase || t.phase || 'working',
            Progress: (t.Progress != null) ? t.Progress : (t.progress || 0)
        };
    }

    function renderWorkerRows(view) {
        var box = $('ss-workers');
        if (!box) return;

        // One row per worker that is actually working - the same shape as before, with the
        // phase and the percentage. Every slot is *not* drawn: padding the panel with idle
        // rows turned a two-worker run into eight lines of "idle —" and buried the work.
        var mine = ((view && (view.RunningTasks || view.runningTasks)) || []).map(normalizeRunning);
        var elsewhere = (lastActive.running || []).map(normalizeRunning);
        var busy = mine.length ? mine : elsewhere;

        if (!busy.length) {
            box.classList.add('ss-hidden');
            box.innerHTML = '';
            return;
        }

        var html = '';
        busy.forEach(function (task, slot) {
            var prog = Math.max(0, Math.min(1, task.Progress || 0));
            var pct = Math.round(prog * 100);
            html += '<div class="ss-worker">' +
                '<div class="ss-worker-idx">' + (slot + 1) + '</div>' +
                '<div class="ss-worker-name">' + esc(task.Title || ('task ' + (slot + 1))) +
                '<span class="ss-worker-phase">' + esc(task.Phase || '') + '</span></div>' +
                '<div class="ss-worker-pct">' + pct + '%</div>' +
                '<div class="ss-worker-bar-wrap"><div class="ss-worker-bar" style="width:' + pct + '%"></div></div>' +
                '</div>';
        });
        box.innerHTML = html;
        box.classList.remove('ss-hidden');
    }

    // C1, revised after real use. One line states the run's totals, and every number in it is said once:
    // the "N subtitles left" head is gone because "227/489" already says how many are left, and the time
    // estimate is gone because the run mixes 25-minute episodes with two-hour films - on the user's own
    // 489-task run the estimate read "about 4 min left" at 29% and "about 10 min left" at 46%, and a number
    // that moves the wrong way while the run advances is worse than no number. What is left is the batch
    // view's own counts (Total, Completed, Failed, Cancelled); the live state - workers, current phase -
    // sits above the bar.
    function renderQueueLine(view) {
        var line = $('ss-queue');
        if (!line) return;
        var total = (view && (view.Total != null ? view.Total : view.total)) || 0;
        if (!total) {
            line.textContent = '';
            return;
        }
        var done = (view.Completed != null ? view.Completed : view.completed) || 0;
        var failed = (view.Failed != null ? view.Failed : view.failed) || 0;
        var cancelled = (view.Cancelled != null ? view.Cancelled : view.cancelled) || 0;
        var bits = [done + '/' + total + ' done (' + Math.round((done / total) * 100) + '%)'];
        if (failed) bits.push(failed + ' failed');
        if (cancelled) bits.push(cancelled + ' cancelled');
        // "already in sync" is a success with nothing to write (the job completed with no output path and an
        // outcome saying so), and it used to be indistinguishable from a real write in this line: a run where
        // every subtitle was already aligned read exactly like one that had written every subtitle. Counted
        // off the tasks themselves rather than a new server field - the outcome text is already what the rows
        // in History show, so this cannot drift from them (one definition, the same one the detail view uses).
        var inSync = alreadyInSyncCount(view);
        if (inSync) bits.push(inSync + ' already in sync');
        line.textContent = bits.join(' \u00b7 ');
    }

    /** How many of a batch's finished tasks resolved as already in sync (nothing written). */
    function alreadyInSyncCount(view) {
        var tasks = (view && (view.Tasks || view.tasks)) || [];
        var n = 0;
        for (var i = 0; i < tasks.length; i++) {
            var t = tasks[i];
            var status = t.Status || t.status || '';
            var outcome = t.Outcome || t.outcome || '';
            var outPath = t.OutputPath !== undefined ? t.OutputPath : t.outputPath;
            if (status === 'Completed' && !outPath && /already in sync/i.test(outcome)) {
                n++;
            }
        }
        return n;
    }

    function showRunBox(show) {
        if (!show) {
            var w = $('ss-workers');
            if (w) { w.classList.add('ss-hidden'); w.innerHTML = ''; }
        }
        $('ss-runbox').classList.toggle('ss-hidden', !show);
        var cancel = $('ss-cancel');
        if (cancel) cancel.classList.toggle('ss-hidden', !show);
        if (show && $('ss-runbox').scrollIntoView) $('ss-runbox').scrollIntoView({ block: 'nearest' });
    }

    // ---------------- Settings ----------------

    // Golden-section search is handed to the engine only together with framerate correction (the server emits
    // --gss just when correction is on), so while correction is off the switch is disabled rather than
    // switchable-but-inert - the case that read as a control that does nothing when ticked (F12). The tick
    // itself is left alone: what was set is what comes back when correction is turned on again.
    function syncGoldenSectionState() {
        var gss = $('ss-gss');
        var fixfps = $('ss-fixfps');
        if (!gss || !fixfps) return;
        var usable = !!fixfps.checked;
        gss.disabled = !usable;
        gss.setAttribute('aria-disabled', usable ? 'false' : 'true');
        var row = gss.closest ? gss.closest('.checkboxContainer') : null;
        if (row) row.classList.toggle('ss-inert', !usable);
    }

    function loadConfig() {
        return api('SubSync/Configuration').then(function (c) {
            $('ss-vad').value = c.VadMethod || 'subs_then_webrtc';
            $('ss-maxoffset').value = c.MaxOffsetSeconds || 60;
            $('ss-maxrefoffset').value = c.MaxSubtitleReferenceOffsetSeconds || 30;
            $('ss-splitpenalty').value = c.SplitPenalty || 0;
            $('ss-maxsub').value = c.MaxSubtitleSeconds || 10;
            $('ss-encoding').value = c.OutputEncoding || 'utf-8';
            $('ss-ffmpeg').value = c.FfmpegPath || '';
            $('ss-ffsubsync').value = c.FfSubSyncPath && c.FfSubSyncPath !== 'ffsubsync' ? c.FfSubSyncPath : '';
            $('ss-mode').value = c.SyncModeCopy !== false ? 'copy' : 'replace';
            $('ss-fixfps').checked = !!c.FixFramerate;
            $('ss-gss').checked = !!c.UseGoldenSectionSearch;
            syncGoldenSectionState();
            $('ss-multimode').value = c.MultiSyncMode || 'auto';
            // The settings input has its own id: it used to share "ss-workers" with the
            // worker-rows container, and getElementById returned the container, so the
            // field was never loaded and every save stored the fallback (4).
            var workerInput = $('ss-workers-input');
            if (workerInput) {
                workerInput.value = c.ParallelWorkers || 4;
                workerInput.max = workerCeiling;
            }
            renderLangChips();
        });
    }

    function wireSettingsControls() {
        var fixfps = $('ss-fixfps');
        if (fixfps) fixfps.addEventListener('change', syncGoldenSectionState);
    }

    function saveConfig() {
        $('ss-save-status').textContent = 'Saving\u2026';
        var wantedWorkers = Math.min(workerCeiling, Math.max(1, parseInt($('ss-workers-input').value, 10) || 4));
        var back = 0;   // what the server actually stored, filled in by the two steps below
        return api('SubSync/Configuration').then(function (c) {
            c.VadMethod = $('ss-vad').value;
            c.MaxOffsetSeconds = parseInt($('ss-maxoffset').value, 10) || 60;
            c.MaxSubtitleReferenceOffsetSeconds = parseFloat($('ss-maxrefoffset').value) || 30;
            c.SplitPenalty = parseFloat($('ss-splitpenalty').value) || 0;
            c.MaxSubtitleSeconds = parseFloat($('ss-maxsub').value) || 10;
            c.OutputEncoding = $('ss-encoding').value || 'utf-8';
            c.FfmpegPath = $('ss-ffmpeg').value;
            c.FfSubSyncPath = $('ss-ffsubsync').value || 'ffsubsync';
            c.SyncModeCopy = $('ss-mode').value === 'copy';
            c.FixFramerate = $('ss-fixfps').checked;
            c.UseGoldenSectionSearch = $('ss-gss').checked;
            c.SyncLanguages = syncLanguages.slice();
            c.MultiSyncMode = $('ss-multimode').value || 'auto';
            c.ParallelWorkers = wantedWorkers;
            return api('SubSync/Configuration', { method: 'POST', body: JSON.stringify(c) });
        }).then(function (stored) {
            // The server answers with what it stored, which is the point of this step: "Saved." on its
            // own cannot distinguish a stored setting from one that was silently dropped.
            back = parseInt(stored && stored.ParallelWorkers, 10);
            var workerField = $('ss-workers-input');
            if (workerField && !isNaN(back)) {
                workerField.value = back;
            }
            return api('SubSync/Settings/ValidationNotes');
        }).then(function (notes) {
            // What the server adjusted, in its own words: a value the plugin brought into range or dropped
            // used to be indistinguishable from a stored one (D3/F10).
            var adjustments = (notes && notes.length) ? ' ' + notes.join('; ') : '';
            $('ss-save-status').textContent = (back === wantedWorkers)
                ? 'Saved.' + adjustments
                : 'Saved, but the server stored ' + back + ' while ' + wantedWorkers + ' was asked for.'
                  + adjustments;
        }).catch(function (e) {
            $('ss-save-status').textContent = 'Save failed: ' + (e.message || e);
        });
    }

    // ---- global language filter (Settings tab) ----------------------
    var syncLanguages = [];

    function renderLangChips() {
        var box = $('ss-langchips');
        if (!box) return;
        var html = '';
        syncLanguages.forEach(function (code) {
            html += '<a href="#" data-ss-globallang="' + esc(code) + '" title="Remove this language">'
                + esc(langLabel(code)) + ' \u2715</a>';
        });
        box.innerHTML = html;

        var hint = $('ss-langhint');
        if (hint) {
            hint.textContent = syncLanguages.length
                ? 'Currently syncing only: ' + syncLanguages.map(langLabel).join(', ')
                    + '. Everything else is hidden from the library lists and the scheduled sweep.'
                : 'No filter — every text subtitle language is synced.';
        }
    }

    function addGlobalLanguage(raw) {
        var value = (raw || '').trim();
        if (!value) return;
        var code = normLang({ Language: value });
        if (syncLanguages.indexOf(code) === -1) {
            syncLanguages.push(code);
            renderLangChips();
        }
        var input = $('ss-langadd');
        if (input) input.value = '';
    }

    // Suggestion list for the language input. A native <datalist> popup cannot
    // be sized or styled (it opened as an enormous list of 85 languages), so the
    // suggestions are rendered here instead: filtered as you type, capped in
    // height, scrollable.
    var LANG_CODES = null;

    function langCodesSorted() {
        if (!LANG_CODES) {
            LANG_CODES = Object.keys(LANG_NAMES2).sort(function (a, b) {
                return langLabel(a).localeCompare(langLabel(b));
            });
        }
        return LANG_CODES;
    }

    function showLangSuggestions(query) {
        var box = $('ss-langsugg');
        if (!box) return;
        var q = (query || '').trim().toLowerCase();
        var hits = [];
        langCodesSorted().forEach(function (c) {
            var label = langLabel(c);
            var hay = (label + ' ' + c).toLowerCase();
            if (!q || hay.indexOf(q) !== -1) {
                hits.push({ code: c, label: label });
            }
        });
        if (!hits.length) {
            box.style.display = 'none';
            box.innerHTML = '';
            return;
        }
        var html = '';
        hits.slice(0, 40).forEach(function (h) {
            html += '<div data-ss-langpick="' + esc(h.code) + '">' + esc(h.label)
                + ' <small>' + esc(h.code) + '</small></div>';
        });
        box.innerHTML = html;
        box.style.display = 'block';
    }

    function hideLangSuggestions() {
        var box = $('ss-langsugg');
        if (box) { box.style.display = 'none'; box.innerHTML = ''; }
    }

    function refreshStatus() {
        api('SubSync/InstallationStatus').then(function (s) {
            var lines = [];
            if (s.BundledFfSubSyncVersion) {
                lines.push('\u2713 Bundled ffsubsync: ' + s.BundledFfSubSyncVersion + ' (' + (s.BundledRid || '') + ') \u2014 ready to use.');
            } else {
                lines.push('ffsubsync source: ' + (s.ResolvedBinaryPath || 'ffsubsync'));
            }
            lines.push('ffmpeg: Jellyfin\u2019s own ffmpeg is used automatically');

            // Stated here so a setting that appears to be ignored is visible at a glance:
            // the value in force against the value configured.
            if (s.WorkerSummary || s.workerSummary) {
                lines.push('Workers: ' + (s.WorkerSummary || s.workerSummary));
            }
            $('ss-status').textContent = lines.join('\n');

            // The loaded copy of the plugin is only interesting when something is wrong, so
            // it is a small dimmed line instead of a fifth line of the status block. The
            // speech cache has its own element below, so it is not repeated here.
            var identityEl = $('ss-identity');
            if (identityEl) {
                var identity = s.PluginIdentity || s.pluginIdentity || '';
                identityEl.textContent = identity ? 'Loaded from ' + identity : '';
            }

            // The plugin's own log, openable straight from here: a log that needs a shell on
            // the server to read is a log nobody reads.
            var logEl = $('ss-logfile');
            if (logEl) {
                logEl.textContent = '';
                var logPath = s.LogFile || s.logFile || '';
                if (logPath) {
                    logEl.appendChild(document.createTextNode('Debug log: ' + logPath + ' '));
                    var logLink = document.createElement('a');
                    logLink.href = apiUrl('SubSync/Log') + '?ApiKey=' + encodeURIComponent(token());
                    logLink.target = '_blank';
                    logLink.rel = 'noopener';
                    logLink.textContent = 'open';
                    logEl.appendChild(logLink);
                }
            }

            var cacheEl = $('ss-speechcache');
            if (cacheEl) {
                var summary = s.SpeechCacheSummary || 'empty';

                // What the extracted-subtitle cache holds, beside the audio line: the one cache the interface used to
                // leave undescribed (F16).
                var subtitleLine = document.getElementById('ss-subtitlecache');
                if (subtitleLine) {
                    subtitleLine.textContent = s.SubtitleCacheSummary
                        ? 'Extracted subtitles: ' + s.SubtitleCacheSummary + '.'
                        : '';
                }
                cacheEl.textContent = 'Audio analysis: ' + summary
                    + ' \u00b7 ' + (summary.indexOf('empty') === -1 ? 'ready to reuse' : 'nothing cached yet');
            }

            var badge = $('ss-badge');
            if (badge) {
                // The build lives here, next to the engine version, instead of inside the
                // progress line where it crowded out what the run was doing.
                var buildTag = s.PluginVersion ? ' \u00b7 v' + s.PluginVersion : '';
                if (s.BundledFfSubSyncVersion) {
                    badge.textContent = '\u2713 bundled ' + (s.BundledFfSubSyncVersion.split(' ')[1] || s.BundledFfSubSyncVersion) + buildTag;
                    badge.classList.add('ss-badge-ok');
                } else {
                    badge.textContent = 'no bundled binary' + buildTag;
                }
            }
        }).catch(function (e) {
            $('ss-status').textContent = 'Status check failed: ' + (e.message || e);
        });
    }

    // ---------------- Browse ----------------
    function loadLibraries() {
        var uid = currentUserId();
        if (!uid) {
            explain('This page could not tell which user is signed in, so it cannot list your libraries. '
                + 'Open it from the Jellyfin web UI while signed in; if it still fails, the browser console '
                + 'shows what the page asked for.');
            return Promise.resolve();
        }
        return api('Users/' + uid + '/Views').then(function (data) {
            var folders = (data && data.Items) ? data.Items : [];
            libraries = folders.filter(function (f) {
                var t = (f.CollectionType || '').toLowerCase();
                return t === 'movies' || t === 'tvshows' || t === 'homevideos' || t === 'mixed' || t === '';
            });
            var sel = $('ss-library');
            var cur = sel.value;
            sel.innerHTML = '<option value="">All Libraries</option>' +
                libraries.map(function (f) { return '<option value="' + esc(f.Id) + '">' + esc(f.Name) + '</option>'; }).join('');
            if (cur && libraries.some(function (f) { return f.Id === cur; })) sel.value = cur;
            var dl = $('ss-dataline');
            if (libraries.length === 0) {
                diag('No libraries visible to this account. Grant access in Dashboard \u2192 Users, or log in as admin.', true);
                if (dl) { dl.textContent = '0 libraries returned by the server'; dl.classList.add('err'); }
            } else {
                diag(null);
                if (dl) { dl.textContent = libraries.length + ' librar' + (libraries.length === 1 ? 'y' : 'ies') + ' available'; dl.classList.remove('err'); }
            }
        }).catch(function (e) {
            diag('Could not load libraries: ' + (e.message || e), true);
            var dl = $('ss-dataline');
            if (dl) { dl.textContent = 'Library request failed: ' + (e.message || e); dl.classList.add('err'); }
        });
    }

    function loadItems() {
        var params = new URLSearchParams({
            Recursive: 'true',
            IncludeItemTypes: 'Series,Movie',
            SortBy: 'SortName',
            SortOrder: 'Ascending',
            Fields: 'ProductionYear'
        });
        var lid = $('ss-library').value;
        if (lid) params.set('ParentId', lid);
        var uid = currentUserId();
        var path = uid ? 'Users/' + uid + '/Items?' + params.toString() : 'Items?' + params.toString();
        $('ss-dataline').textContent = 'Loading\u2026';
        return api(path).then(function (data) {
            allItems = (data && data.Items) ? data.Items : [];
            trackCache = {};
            episodesCache = {};
            refreshDataline();
            render();
        }).catch(function (e) {
            diag('Could not load items: ' + (e.message || e), true);
            var dl = $('ss-dataline');
            if (dl) { dl.textContent = 'Items request failed: ' + (e.message || e); dl.classList.add('err'); }
        });
    }

    // The count line keeps the "Select all" link (scoped to what is
    // currently shown); the sync action sits in its own row underneath so
    // it reads as a real button.
    function refreshDataline() {
        var dl = $('ss-dataline');
        if (!dl) return;
        dl.classList.remove('err');

        var libName = ($('ss-library').selectedOptions && $('ss-library').selectedOptions[0])
            ? $('ss-library').selectedOptions[0].textContent
            : 'all libraries';
        var base = allItems.length + ' item' + (allItems.length === 1 ? '' : 's') + ' in ' + libName;
        var vis = visibleItems();
        var allPicked = vis.length > 0 && vis.every(function (x) { return selectedIds[x.Id]; });
        var n = pickedCount();

        var html = esc(base) + ' \u00b7 ' +
            '<a href="#" id="ss-selall-link" style="color:var(--theme-primary-color,#00a4dc);text-decoration:none;">' +
            (allPicked ? 'Clear selection' : 'Select all') + '</a>';

        if (n === 0) {
            html += ' <span style="opacity:.6;">\u00b7 right-click a row to pick it, shift+right-click for a range</span>';
        } else {
            html += ' <span style="opacity:.6;">\u00b7 ' + n + ' file' + (n === 1 ? '' : 's') + ' picked'
                + ' \u00b7 right-click to add or remove, shift+right-click for a range</span>';
        }
        if (syncLanguages.length) {
            html += ' <span style="opacity:.6;">\u00b7 language filter: '
                + esc(syncLanguages.map(langLabel).join(', ')) + '</span>';
        }

        dl.innerHTML = html;

        var bar = $('ss-selactions');
        if (!bar) return;
        if (n === 0) {
            bar.style.display = 'none';
            return;
        }

        var busyNow = isBuilding || startInFlight;
        var estimate = estimateTaskCount();
        var fmt = function (v) { return v.toLocaleString(); };

        var scanSpin = $('ss-selscan');
        if (scanSpin) scanSpin.style.display = langScan.running ? 'inline-block' : 'none';

        var btn = $('ss-syncsel-btn');
        var span = btn ? btn.querySelector('span') : null;
        if (span) {
            // C5: the same words as every other sync button - a number of subtitles, never a number of files.
            // The count is only known once the subtitle lists of the picks have been read; until then the
            // button says what it will do without inventing a number.
            span.textContent = busyNow ? 'Working\u2026' : subtitleButtonLabel(estimate);
        }
        if (btn) {
            // G2 follow-up: a selection that carries tracks but none that can be aligned has nothing to queue,
            // so the button is greyed out with the reason on it instead of clicking through to a diagnostic
            // line that says the same thing. While the scan is still running the count is unknown (null) and
            // the button stays live, because "not known yet" is not "nothing".
            var nothingToSync = !busyNow && estimate === 0;
            btn.disabled = busyNow || nothingToSync;
            btn.setAttribute('title', nothingToSync
                ? (selectionUnsyncableReason(pickedItems()) || 'There is nothing to sync in this selection.')
                : '');
        }

        // Keep the language dropdown in step with the scan, without
        // disturbing the option the user picked.
        var langSel = $('ss-sellang');
        if (langSel) {
            // B3: this box is the same control in every state. While the subtitle lists were being read it had
            // its whole option list replaced by a single "LOADING" entry and was disabled, which reads as the
            // filter box being swapped out for a line of text. What it does now is keep its box, keep the
            // languages the last scan found (a re-scan does not invalidate them), stay enabled, and say what it
            // is doing inside its own text.
            var counts = langScan.running ? lastLangCounts : langScan.langs;
            var codes = Object.keys(counts).sort(function (a, b) {
                return langLabel(a).localeCompare(langLabel(b));
            });
            var first = selLangs.length ? 'Add another language\u2026' : 'All languages';
            if (langScan.running) {
                first = 'Sorting\u2026' + (langScan.total ? ' ' + langScan.done + '/' + langScan.total : '');
            }
            var sig = codes.join(',') + '|' + selLangs.join(',') + '|' + first;
            if (sig !== langScan.signature) {
                langScan.signature = sig;
                var opts = '<option value="">' + esc(first) + '</option>';
                codes.forEach(function (c) {
                    if (selLangs.indexOf(c) !== -1) return;
                    opts += '<option value="' + esc(c) + '">' + esc(langLabel(c))
                        + ' (' + counts[c] + ')</option>';
                });
                langSel.innerHTML = opts;
            }
            langSel.value = '';
            langSel.disabled = false;
        }

        var chips = $('ss-selchips');
        if (chips) {
            var chipHtml = '';
            selLangs.forEach(function (c) {
                chipHtml += '<a href="#" data-ss-lang="' + esc(c) + '" title="Remove this language">'
                    + esc(langLabel(c)) + ' \u2715</a>';
            });
            chips.innerHTML = chipHtml;
        }

        var hasSeries = false;
        for (var si = 0; si < allItems.length; si++) {
            if (selectedIds[allItems[si].Id] && allItems[si].Type === 'Series') { hasSeries = true; break; }
        }
        var note = $('ss-selnote');
        if (note) {
            var langs = selLangs.map(langLabel).join(' + ');
            var words = n + ' file' + (n === 1 ? '' : 's') + ' selected';
            if (hasSeries) words += ' \u2014 series sync all their episodes';

            if (langScan.running) {
                words += ' \u00b7 reading subtitle lists\u2026 ' + langScan.done + '/' + langScan.total;
            } else if (selLangs.length && estimate !== null) {
                words += ' \u2014 ' + fmt(estimate) + ' ' + langs + ' subtitle track'
                    + (estimate === 1 ? '' : 's') + ' to sync';
            } else if (selLangs.length) {
                words += ' \u2014 only ' + langs + ' subtitles';
            }

            if (langScan.partial) words += ' \u00b7 language list partial';
            note.textContent = words;
        }
        bar.style.display = 'flex';
    }

    function trackOptionsHtml() {
        var usable = syncableTracks(movieTracks);
        var html = '<option value="-1">All ' + usable.length + ' track' + (usable.length === 1 ? '' : 's') + '</option>';
        movieTracks.forEach(function (t) {
            var label = esc(t.Title || ('Track ' + t.Index)) + (t.Language ? ' (' + esc(t.Language) + ')' : '');
            if (trackUnsupported(t)) {
                // Listed, because the file has it, and disabled, because it cannot be synced: an option a
                // click can reach is an option the plugin has to honour.
                html += '<option value="' + t.Index + '" disabled title="' + esc(trackUnsupportedReason(t)) + '">'
                    + label + ' \u2014 image format, cannot be synced</option>';
            } else {
                html += '<option value="' + t.Index + '">' + label + '</option>';
            }
        });
        return html;
    }

    // ---------------- Multi-select (right-click + shift-range) ----------------
    // C4, revised for the second user report of 2026-09-18: the picked rows come first so a selection stays
    // visible and reachable while the search box narrows the list - but only once there is a *selection* to
    // keep visible. With a single pick the list keeps the library's own order, because a one-item selection
    // is not something the user is tracking as a group and moving the row they just clicked to the top of the
    // list is motion they did not ask for. Measured before the change: one left-click put that row at
    // position 0 of a 41-item list. The threshold is the same one that makes the selection controls appear.
    // The order inside each group is the library's own. One definition, used by the renderer and by the range
    // selection, so a shift+right-click range covers the rows in the order they are drawn.
    function visibleItems() {
        var q = ($('ss-search').value || '').toLowerCase().trim();
        var shown = allItems.filter(function (x) {
            return !q || (x.Name || '').toLowerCase().indexOf(q) !== -1;
        });
        if (pickedCount() < FLOAT_MIN_PICKS) {
            return shown;
        }
        var picked = [], rest = [];
        shown.forEach(function (x) {
            (selectedIds[x.Id] ? picked : rest).push(x);
        });
        return picked.concat(rest);
    }

    function pickedCount() {
        var n = 0;
        for (var k in selectedIds) { if (Object.prototype.hasOwnProperty.call(selectedIds, k)) n++; }
        return n;
    }

    // How many picks it takes before the list reorders itself. One pick is a row the user clicked; two is a
    // selection they are building, and only then is keeping it together worth moving rows for (see
    // visibleItems).
    var FLOAT_MIN_PICKS = 2;

    // C5: every sync button states how many subtitle tracks it will queue, in the same words. A count that is
    // not known yet says the unit without inventing a number, and nothing says "files" any more: what a sync
    // queues is a number of subtitle tracks, which is the number the run box and the history report too.
    function subtitleButtonLabel(count) {
        if (count === null || count === undefined || count <= 0) {
            return 'Sync subtitles';
        }
        return 'Sync ' + count + ' subtitle' + (count === 1 ? '' : 's');
    }

    // How many tracks the row's own controls would queue: the movie's picked track (or all of them), or the
    // series scope's tracks for the chosen language. Null until the lists for that scope have been read, which
    // the button then says instead of guessing.
    /** True when the picked items do carry subtitle tracks, but every one of them is an image format (G2).
     *  The two empty results are different messages: nothing found, or nothing that can be aligned. */
    function selectionHasOnlyImageTracks(items) {
        var sawTrack = false;
        (items || []).forEach(function (it) {
            var lists = (it.Type === 'Movie')
                ? [trackCache[it.Id] || []]
                : (episodesCache[it.Id] || []).map(function (ep) { return trackCache[ep.id] || []; });
            lists.forEach(function (list) { if (list.length) sawTrack = true; });
        });
        return sawTrack;
    }

    /** The sentence a user gets when their selection carries tracks but none that can be aligned (G2). */
    function selectionUnsyncableReason(items) {
        if (!selectionHasOnlyImageTracks(items)) return '';
        return 'The selected items carry no text subtitle tracks: every track they have is an image format '
            + '(PGS, VobSub), which cannot be aligned. Convert one to srt first.';
    }

    /** The picked rows, as objects - what the selection-level controls are about. */
    function pickedItems() {
        return allItems.filter(function (x) { return selectedIds[x.Id]; });
    }

    /** How many of the selected movie's tracks can be queued at all (G2). */
    function movieSyncableCount() {
        return syncableTracks(movieTracks).length;
    }

    /** The reason the selected movie offers nothing to sync, or '' when it does. */
    function movieUnsupportedReason() {
        if (movieSyncableCount() > 0) return '';
        var first = (movieTracks || [])[0];
        return first ? trackUnsupportedReason(first) : 'no subtitle tracks';
    }

    function movieRowCount() {
        var sel = $('ss-trackpick');
        if (!sel || sel.value === '') {
            return null;
        }
        return sel.value === '-1' ? syncableTracks(movieTracks).length : 1;
    }

    function seriesRowCount(only) {
        var cache = scopeTarget ? langFilterCache[scopeTarget] : null;
        if (!cache || !cache.counts) {
            return null;
        }
        var want = (only === undefined) ? currentLangFilter() : only;
        if (!want || want === '*') {
            var total = 0;
            for (var code in cache.counts) {
                if (Object.prototype.hasOwnProperty.call(cache.counts, code)) total += cache.counts[code];
            }
            return total;
        }
        return cache.counts[want] || null;
    }

    function refreshRowButtonLabel() {
        var btn = $('ss-syncbtn');
        if (!btn) return;
        var span = btn.querySelector('span');
        if (!span) return;
        var count = (selected && selected.Type === 'Movie') ? movieRowCount() : seriesRowCount();
        span.textContent = subtitleButtonLabel(count);
    }

    // Rows are multi-selected by right-clicking: a plain click selects one
    // item and shows its options without touching the selection, each
    // shift-click adds or removes that item, so picks do not have to be
    // adjacent. Right-click toggles one row; shift+right-click selects the
    // range from the anchor. (Shift+left-click is kept as the older way to
    // add a single pick; right-click is the documented one.)
    var isBuilding = false; // a batch queue is being assembled
    var anchorId = null;    // last plain-clicked row (range anchor)

    function toggleSelectAllVisible() {
        var vis = visibleItems();
        var allPicked = vis.length > 0 && vis.every(function (x) { return selectedIds[x.Id]; });
        if (allPicked) {
            selectedIds = {};
            anchorId = null;
        } else {
            selectedIds = {};
            vis.forEach(function (x) { selectedIds[x.Id] = true; });
            anchorId = vis.length ? vis[0].Id : null;
        }
        startLangScan();
        render();
    }

    function togglePicked(id) {
        if (selectedIds[id]) delete selectedIds[id];
        else selectedIds[id] = true;
        startLangScan();
        render();
    }

    // ---- picked-selection language support -------------------------
    // Track/episode lists are cached: the language scan and the queue
    // build walk the same data, so each item is only fetched once.
    var trackCache = {};
    var episodesCache = {};
    var selLangs = [];           // canonical codes; empty = every language
    var langScanToken = 0;
    var langScan = { running: false, done: 0, total: 0, langs: {}, partial: false, signature: '' };
    // The languages the last finished scan found. Kept apart from langScan.langs (which is emptied when a new
    // scan starts) so the filter box stays usable and populated while a re-scan reads the lists (B3).
    var lastLangCounts = {};
    var LANG_SCAN_BUDGET = 400;  // max track requests per scan

    // Jellyfin hands back language codes in whatever form the file used:
    // two-letter ISO 639-1 ("sv"), three-letter 639-2/B ("swe", "chi") or
    // occasionally a name. Everything is folded onto one canonical code so
    // "Swedish", "sv" and "swe" collapse into a single dropdown entry.
    var LANG_ALIASES = {};
    var LANG_NAMES2 = {};
    (function () {
        var table = {
            eng: ['en', 'english'], swe: ['sv', 'swedish'], nor: ['no', 'nb', 'nn', 'norwegian'],
            dan: ['da', 'danish'], fin: ['fi', 'finnish'], isl: ['is', 'ice', 'icelandic'],
            deu: ['de', 'ger', 'german'], fra: ['fr', 'fre', 'french'], spa: ['es', 'spanish'],
            ita: ['it', 'italian'], nld: ['nl', 'dut', 'dutch'], por: ['pt', 'portuguese'],
            rus: ['ru', 'russian'], pol: ['pl', 'polish'], ces: ['cs', 'cze', 'czech'],
            slk: ['sk', 'slo', 'slovak'], hun: ['hu', 'hungarian'], ron: ['ro', 'rum', 'romanian'],
            bul: ['bg', 'bulgarian'], hrv: ['hr', 'croatian'], srp: ['sr', 'serbian'],
            slv: ['sl', 'slovenian'], bos: ['bs', 'bosnian'], mkd: ['mk', 'mac', 'macedonian'],
            sqi: ['sq', 'alb', 'albanian'], ell: ['el', 'gre', 'greek'], tur: ['tr', 'turkish'],
            ara: ['ar', 'arabic'], heb: ['he', 'iw', 'hebrew'], fas: ['fa', 'per', 'persian'],
            urd: ['ur', 'urdu'], hin: ['hi', 'hindi'], ben: ['bn', 'bengali'], tam: ['ta', 'tamil'],
            tel: ['te', 'telugu'], mal: ['ml', 'malayalam'], kan: ['kn', 'kannada'],
            mar: ['mr', 'marathi'], guj: ['gu', 'gujarati'], pan: ['pa', 'punjabi'],
            sin: ['si', 'sinhala'], nep: ['ne', 'nepali'], zho: ['zh', 'chi', 'chinese'],
            jpn: ['ja', 'japanese'], kor: ['ko', 'korean'], vie: ['vi', 'vietnamese'],
            tha: ['th', 'thai'], ind: ['id', 'indonesian'], msa: ['ms', 'may', 'malay'],
            tgl: ['tl', 'fil', 'tagalog'], khm: ['km', 'khmer'], lao: ['lo', 'lao'],
            mya: ['my', 'bur', 'burmese'], mon: ['mn', 'mongolian'], bod: ['bo', 'tib', 'tibetan'],
            uzb: ['uz', 'uzbek'], kaz: ['kk', 'kazakh'], aze: ['az', 'azerbaijani'],
            hye: ['hy', 'arm', 'armenian'], kat: ['ka', 'geo', 'georgian'], ukr: ['uk', 'ukrainian'],
            bel: ['be', 'belarusian'], est: ['et', 'estonian'], lav: ['lv', 'latvian'],
            lit: ['lt', 'lithuanian'], eus: ['eu', 'baq', 'basque'], cat: ['ca', 'catalan'],
            glg: ['gl', 'galician'], cym: ['cy', 'wel', 'welsh'], gle: ['ga', 'irish'],
            afr: ['af', 'afrikaans'], swa: ['sw', 'swahili'], amh: ['am', 'amharic'],
            som: ['so', 'somali'], hau: ['ha', 'hausa'], yor: ['yo', 'yoruba'],
            zul: ['zu', 'zulu'], xho: ['xh', 'xhosa'], kur: ['ku', 'kurdish'],
            pus: ['ps', 'pashto'], und: ['unknown', 'unk']
        };
        Object.keys(table).forEach(function (canon) {
            LANG_ALIASES[canon] = canon;
            var aliases = table[canon];
            aliases.forEach(function (a) { LANG_ALIASES[a] = canon; });
            var name = null;
            for (var i = 0; i < aliases.length; i++) {
                if (aliases[i].length > 3) { name = aliases[i]; break; }
            }
            LANG_NAMES2[canon] = name ? (name.charAt(0).toUpperCase() + name.slice(1)) : canon.toUpperCase();
        });
    })();

    function normLang(t) {
        var raw = (t && t.Language) ? String(t.Language).trim().toLowerCase() : '';
        if (!raw) return 'und';
        raw = raw.split(/[-_]/)[0]; // sv-SE -> sv
        if (LANG_ALIASES[raw]) return LANG_ALIASES[raw];
        return LANG_ALIASES[raw.slice(0, 2)] || raw;
    }

    function trackForced(t) {
        return !!(t && (t.IsForced !== undefined ? t.IsForced : t.isForced));
    }

    function trackExternal(t) {
        return !!(t && (t.IsExternal !== undefined ? t.IsExternal : t.isExternal));
    }

    // Lower is better when two tracks share a language: a forced track is a signs/text track
    // with a handful of cues, so it must never win over the full track, and an external file
    // is preferred over an embedded one of the same rank.
    function trackRank(t) {
        return (trackForced(t) ? 2 : 0) + (trackExternal(t) ? 0 : 1);
    }

    function langLabel(code) {
        if (!code) return 'All languages';
        return LANG_NAMES2[code] || LANG_NAMES[code] || code.toUpperCase();
    }

    function cachedTracks(id) {
        if (trackCache[id]) return Promise.resolve(trackCache[id]);
        return listTracks(id).then(function (t) {
            trackCache[id] = t || [];
            return trackCache[id];
        });
    }

    function cachedEpisodes(id) {
        if (episodesCache[id]) return Promise.resolve(episodesCache[id]);
        return episodesInScope(id, false).then(function (eps) {
            episodesCache[id] = eps || [];
            return episodesCache[id];
        });
    }

    // One track per language per episode, ranked the same way the queue build ranks
    // (non-forced before forced, then external before embedded), so a two-cue forced track
    // is never what gets queued for a language that also has a full track.
    function bestPerLanguage(tracks, onlyLang) {
        var want = null;
        if (onlyLang && onlyLang.length) {
            // Normalise the wanted side too, so a stray form ('sv', 'Swedish')
            // still matches the canonical codes we compare against.
            want = ((typeof onlyLang === 'string') ? [onlyLang] : onlyLang).map(function (c) {
                return normLang({ Language: c });
            });
        }
        var byLang = {};
        syncableTracks(tracks).forEach(function (t) {
            var k = normLang(t);
            if (want && want.indexOf(k) === -1) return;
            var cur = byLang[k];
            if (!cur || trackRank(t) < trackRank(cur)) byLang[k] = t;
        });
        return Object.keys(byLang).sort().map(function (k) { return byLang[k]; });
    }

    // Loads subtitle lists for many items through the bulk endpoint, filling the same
    // caches the queue build reads. One request per chunk instead of one request per
    // file (and per episode) — the difference between seconds and minutes on a
    // library-wide selection.
    function bulkLoadTracks(itemIds, expandSeries, onProgress) {
        var CHUNK = 25;
        var chunks = [];
        for (var i = 0; i < itemIds.length; i += CHUNK) {
            chunks.push(itemIds.slice(i, i + CHUNK));
        }

        var loaded = 0;
        var tracks = 0;
        var seq = Promise.resolve();
        chunks.forEach(function (chunk) {
            seq = seq.then(function () {
                return api('SubSync/Subtitles/Batch', {
                    method: 'POST',
                    body: JSON.stringify({ itemIds: chunk, expandSeries: !!expandSeries })
                }).then(function (res) {
                    var items = (res && (res.Items || res.items)) || [];
                    items.forEach(function (entry) {
                        var id = entry.Id || entry.id;
                        var seriesId = entry.SeriesId || entry.seriesId;
                        var list = (entry.Tracks || entry.tracks || []).filter(function (t) {
                            return t && typeof t.Index === 'number';
                        });
                        trackCache[id] = list;
                        if (seriesId) {
                            var eps = episodesCache[seriesId] || (episodesCache[seriesId] = []);
                            if (!eps.some(function (e) { return e.id === id; })) {
                                eps.push({ id: id, name: entry.Name || entry.name || '' });
                            }
                        }
                        tracks += list.length;
                    });
                    loaded += chunk.length;
                    if (onProgress) onProgress(loaded, itemIds.length, tracks);
                });
            });
        });

        return seq;
    }

    // Reads the subtitle lists of everything picked so the dropdown can
    // list the languages that actually exist. Runs in the background and
    // stops after LANG_SCAN_BUDGET track requests.
    function startLangScan() {
        var token = ++langScanToken;
        var picked = allItems.filter(function (x) { return selectedIds[x.Id]; });
        langScan = { running: picked.length > 0, done: 0, total: picked.length, langs: {}, partial: false, signature: '' };
        if (!picked.length) { refreshDataline(); return; }

        bulkLoadTracks(picked.map(function (x) { return x.Id; }), true, function (loaded) {
            if (token !== langScanToken) return;
            langScan.done = loaded;
            refreshDataline();
        }).then(function () {
            if (token !== langScanToken) return;

            // Aggregate languages straight from the caches: movies contribute their
            // own tracks, series every episode's.
            picked.forEach(function (it) {
                var collect = function (list) {
                    (list || []).forEach(function (t) {
                        var k = normLang(t);
                        langScan.langs[k] = (langScan.langs[k] || 0) + 1;
                    });
                };
                if (it.Type === 'Movie') {
                    collect(trackCache[it.Id]);
                    return;
                }
                (episodesCache[it.Id] || []).forEach(function (ep) { collect(trackCache[ep.id]); });
            });

            langScan.running = false;
            lastLangCounts = langScan.langs;
            refreshDataline();
        }).catch(function (e) {
            if (token !== langScanToken) return;
            langScan.running = false;
            langScan.partial = true;
            refreshDataline();
            diag('Could not read every subtitle list: ' + (e.message || e), true);
        });
    }

    // Tasks for one item. Movies contribute their tracks, series one track
    // per language per episode (external preferred). When a language is
    // chosen in the dropdown, only that language is queued.
    function buildTasksForItem(item) {
        if (item.Type === 'Movie') {
            return cachedTracks(item.Id).then(function (tracks) {
                var chosen = selLangs.length ? bestPerLanguage(tracks, selLangs) : syncableTracks(tracks);
                return chosen.map(function (t) {
                    return { itemId: item.Id, index: t.Index, title: item.Name + ' \u2014 ' + (t.Title || ('track ' + t.Index)) };
                });
            });
        }

        return cachedEpisodes(item.Id).then(function (episodes) {
            var tasks = [];
            var seq = Promise.resolve();
            episodes.forEach(function (ep) {
                seq = seq.then(function () {
                    return cachedTracks(ep.id).then(function (tracks) {
                        bestPerLanguage(tracks, selLangs).forEach(function (t) {
                            tasks.push({ itemId: ep.id, index: t.Index, title: ep.name + ' \u2014 ' + (t.Title || ('track ' + t.Index)) });
                        });
                    });
                });
            });
            return seq.then(function () { return tasks; });
        });
    }

    // How many subtitle tracks the current picks would actually queue. Only
    // answered once the scan has finished, so the number always matches the
    // queue that gets built (both read the same caches + bestPerLanguage).
    function estimateTaskCount() {
        if (langScan.running) return null;
        var total = 0;
        allItems.forEach(function (it) {
            if (!selectedIds[it.Id]) return;
            if (it.Type === 'Movie') {
                var tracks = trackCache[it.Id];
                if (!tracks) return;
                total += (selLangs.length ? bestPerLanguage(tracks, selLangs) : syncableTracks(tracks)).length;
                return;
            }
            (episodesCache[it.Id] || []).forEach(function (ep) {
                var epTracks = trackCache[ep.id];
                if (!epTracks) return;
                total += bestPerLanguage(epTracks, selLangs).length;
            });
        });
        return total;
    }

    // Shared tail for starting a batch: stream it if idle, otherwise let it
    // join the server queue behind the running run.
    function watchOrQueueBatch(batchId, label, taskCount) {
        if (watchedBatchTimer) {
            busy = true;
            $('ss-run-label').textContent = 'Queued: ' + label + ' \u2014 behind the current run.';
            refreshHistory();
            return;
        }

        busy = true;
        reattached = false;
        attachPending = false;
        pollBatchView(batchId);
    }

    // The API refuses a batch of more than 1000 tasks (SubSyncController.CreateBatch) and both queue
    // builds used to POST the whole selection in a single request: a long series or a multi-library pick
    // can pass 1000 tasks (episodes x languages) and was refused with "Batch is too large" after minutes
    // of reading subtitle lists, with nothing in the page saying what the limit was.
    //
    // The split is client-side on purpose: the request keeps its shape, the server keeps its bound, and
    // nothing else moves. At or below the limit this is exactly the single POST it always was.
    var BATCH_CHUNK = 1000;

    // The server refuses to queue the same item and track twice (D9); when it says so, the page says so too,
    // instead of leaving a user to wonder why the run has fewer tasks than they asked for.
    function noteAlreadyQueued(view) {
        var n = view && (view.AlreadyQueuedCount != null ? view.AlreadyQueuedCount : view.alreadyQueuedCount);
        if (n > 0) {
            diag(n + ' task(s) were already queued or running, so they were not queued a second time.');
        }
    }

    function postBatch(label, rows) {
        if (rows.length <= BATCH_CHUNK) {
            return api('SubSync/Batch', {
                method: 'POST',
                body: JSON.stringify({ label: label, tasks: rows })
            }).then(function (view) {
                noteAlreadyQueued(view);
                return view;
            });
        }

        var parts = Math.ceil(rows.length / BATCH_CHUNK);

        // Sequential, not parallel - the same chain the bulk track loader uses: the enqueue is
        // synchronous server-side, so a burst of chunks would only make each one slower. The last part
        // is what is returned, because its jobs finish last and the run box follows that batch; the
        // earlier parts are already queued ahead of it in the same FIFO.
        var last = null;
        var seq = Promise.resolve();
        for (var part = 0; part < parts; part++) {
            (function (index) {
                var slice = rows.slice(index * BATCH_CHUNK, (index + 1) * BATCH_CHUNK);
                seq = seq.then(function () {
                    $('ss-run-label').textContent = 'Queueing ' + (index + 1) + '/' + parts + '\u2026';
                    return api('SubSync/Batch', {
                        method: 'POST',
                        body: JSON.stringify({
                            label: label + ' (' + (index + 1) + '/' + parts + ')',
                            tasks: slice
                        })
                    }).then(function (view) { noteAlreadyQueued(view); last = view; });
                });
            })(part);
        }

        return seq.then(function () { return last; });
    }

    function startSyncSelected() {
        if (startInFlight) return;
        var items = allItems.filter(function (x) { return selectedIds[x.Id]; });
        if (!items.length) { diag('Nothing selected.', true); return; }

        startInFlight = true;
        isBuilding = true;
        refreshDataline();
        var idle = !watchedBatchTimer;
        if (idle) {
            busy = true;
            showRunBox(true);
            $('ss-progress').style.width = '0%';
        }
        var done = 0;

        var all = [];
        // One bulk request per chunk fills the caches; assembling the task list is
        // then pure bookkeeping with no further round-trips.
        var seq = bulkLoadTracks(items.map(function (x) { return x.Id; }), true, function (loaded, total, trackCount) {
            $('ss-run-label').textContent = 'Loading\u2026';
        }).then(function () {
            var inner = Promise.resolve();
            items.forEach(function (it) {
                inner = inner.then(function () {
                    return buildTasksForItem(it).then(function (t) {
                        all = all.concat(t);
                        done++;
                        $('ss-run-label').textContent = 'Loading\u2026';
                    });
                });
            });
            return inner;
        });

        seq.then(function () {
            if (!all.length) {
                diag(selectionUnsyncableReason(items)
                    || 'No subtitle tracks found for the selected items.', true);
                startInFlight = false;
                isBuilding = false;
                refreshDataline();
                if (idle) busy = false;
                return;
            }

            var label = 'Selection (' + items.length + ' file' + (items.length === 1 ? '' : 's') + ')';
            $('ss-run-label').textContent = 'Loading\u2026';

            return postBatch(label, all.map(function (t) {
                return { itemId: t.itemId, subtitleIndex: t.index, title: t.title };
            })).then(function (view) {
                var batchId = (view && (view.Id || view.id)) || '';
                if (!batchId) throw new Error('server did not return a batch id');
                startInFlight = false;
                isBuilding = false;
                refreshDataline();
                watchOrQueueBatch(batchId, label, all.length);
            });
        }).catch(function (e) {
            startInFlight = false;
            isBuilding = false;
            refreshDataline();
            if (!watchedBatchTimer) busy = false;
            diag('Could not start sync: ' + (e.message || e), true);
        });
    }

    function render() {
        var filtered = visibleItems();
        $('ss-list').innerHTML = filtered.map(function (item) {
            var meta = item.Type + (item.ProductionYear ? ' \u00b7 ' + item.ProductionYear : '');
            var isSel = selected && selected.Id === item.Id;
            var isPicked = !!selectedIds[item.Id];
            var html = '<div class="ss-row' + (isPicked ? ' selected' : '') + '" data-id="' + esc(item.Id) + '">' +
                '<div class="ss-row-main">' +
                '<div class="ss-name">' + esc(item.Name) + '</div>' +
                '<div class="ss-meta">' + esc(meta) + '</div>' +
                '</div>';
            if (isSel) {
                if (item.Type === 'Movie') {
                    html += '<div class="ss-row-actions">' +
                        '<select is="emby-select" id="ss-trackpick" class="ss-compact" label="Subtitle">' + trackOptionsHtml() + '</select>' +
                        '<button is="emby-button" type="button" id="ss-syncbtn" class="raised button-submit emby-button"' + (movieSyncableCount() ? '' : ' disabled title="' + esc(movieUnsupportedReason()) + '"') + '><span>' + esc(subtitleButtonLabel(movieSyncableCount() || null)) + '</span></button>' +
                        '</div>';
                } else {
                    html += '<div class="ss-row-actions">' +
                        '<select is="emby-select" id="ss-scope" class="ss-compact" label="Scope"><option value="">Whole series</option></select>' +
                        '<select is="emby-select" id="ss-langfilter" class="ss-compact" label="Subtitle">' + seriesLangHtml() + '</select>' +
                        '<button is="emby-button" type="button" id="ss-syncbtn" class="raised button-submit emby-button"><span>' + esc(subtitleButtonLabel(seriesRowCount('*'))) + '</span></button>' +
                        '</div>';
                }
            } else {
                html += '<span class="material-icons" aria-hidden="true" style="opacity:.45;">chevron_right</span>';
            }
            html += '</div>';
            return html;
        }).join('');
        $('ss-empty').classList.toggle('ss-hidden', filtered.length !== 0);

        Array.prototype.forEach.call($('ss-list').querySelectorAll('.ss-row'), function (row) {
            // Shift-clicking in a browser would otherwise drag-select the
            // page text; suppress that on the row.
            row.addEventListener('mousedown', function (e) {
                if (e.shiftKey) e.preventDefault();
            });

            // Right-click picks or unpicks one row; shift+right-click
            // extends from the anchor over everything in between.
            row.addEventListener('contextmenu', function (e) {
                if (e.target.closest('.ss-row-actions')) return;
                e.preventDefault();
                var id = row.getAttribute('data-id');
                if (e.shiftKey && anchorId) {
                    // The range covers the rows as they are *drawn*, read straight off the DOM rather than
                    // recomputed: the drawn order now depends on how many picks there are (see visibleItems),
                    // and a range computed from a second evaluation could disagree with the list on screen.
                    var drawnIds = Array.prototype.map.call(
                        $('ss-list').querySelectorAll('.ss-row'),
                        function (r) { return r.getAttribute('data-id'); });
                    var a = drawnIds.indexOf(anchorId);
                    var b = drawnIds.indexOf(id);
                    if (a >= 0 && b >= 0) {
                        selectedIds = {};
                        for (var i = Math.min(a, b); i <= Math.max(a, b); i++) selectedIds[drawnIds[i]] = true;
                        startLangScan();
                        render();
                        return;
                    }
                }
                togglePicked(id);
            });

            row.addEventListener('click', function (e) {
                if (e.target.closest('.ss-row-actions')) return; // clicks on controls handled separately
                var id = row.getAttribute('data-id');
                var match = null;
                for (var i = 0; i < allItems.length; i++) if (allItems[i].Id === id) { match = allItems[i]; break; }
                if (!match) return;

                if (e.shiftKey) {
                    // Shift-click adds or removes just this item, so
                    // non-adjacent media can be picked. The action row
                    // (and its options) stay put.
                    togglePicked(id);
                    return;
                }

                // Fix 1, 2026-09-18: a plain click does NOT touch the selection. It selects and views the row -
                // it shows that item's own options - and that is all. Right-click is the only way to add a
                // pick, which is what this list has always been documented as ("right-click a row to pick it,
                // shift+right-click for a range"), and what the shift+right-click range is built on.
                //
                // C3 had made a plain click additive, on the reasoning that clearing a selection while
                // searching is costly. The cost it named was real, but the cause was different: nothing here
                // clears picks any more (searching does not), and the user's own testing of 2.0.64 found the
                // consequence of the C3 fix - every left-click silently grew the sync selection, so a user
                // browsing the library accumulated a batch they never asked for. C3 is kept where it belongs:
                // the selection survives a search, and the count line says how to clear it.
                anchorId = id;
                selectItem(match);
            });
        });

        if (selected) {
            // C5: the row's own button states the count its controls imply, and follows them: picking a single
            // track from a movie, or a language from a series, changes what the button would queue.
            if (selected.Type === 'Movie' && $('ss-trackpick')) {
                $('ss-trackpick').onchange = refreshRowButtonLabel;
            }
            if (selected.Type === 'Series' && $('ss-langfilter')) {
                $('ss-langfilter').onchange = refreshRowButtonLabel;
            }
            if (selected.Type === 'Series' && $('ss-scope')) {
                $('ss-scope').onchange = function () {
                    var opt = $('ss-scope').options[$('ss-scope').selectedIndex];
                    scopeTarget = $('ss-scope').value || selected.Id;
                    saveState({ scope: scopeTarget });
                    var name = opt ? (opt.getAttribute('data-name') || opt.textContent) : selected.Name;
                    var rowName = $('ss-list').querySelector('.ss-row.selected .ss-name');
                    if (rowName) rowName.textContent = name;
                    var v = $('ss-scope').value || selected.Id;
                    if (v) {
                        discoverLangOptions(v, v !== selected.Id);
                    }
                };
            }
            if ($('ss-syncbtn')) {
                $('ss-syncbtn').onclick = function (e) {
                    e.stopPropagation();
                    startBatch();
                };
            }
            // Re-apply the chosen language filter after a row rebuild.
            if (selected && selected.Type === 'Series' && $('ss-langfilter') && scopeTarget) {
                var eL = langFilterCache[scopeTarget];
                if (eL && eL.value) {
                    $('ss-langfilter').value = eL.value;
                }
            }
            // The saved language filter has just been put back, so the button follows it rather than the
            // default the row was built with (C5).
            refreshRowButtonLabel();
        }

        refreshDataline();
    }

    function selectItem(item) {
        selected = item;
        scopeTarget = item.Id;
        movieTracks = [];
        saveState({ selectedId: item.Id });
        if (!busy && !reattached && !attachPending) {
                $('ss-progress').style.width = '0%';
            showRunBox(false);
        }
        diag(null);
        if (item.Type === 'Movie') {
            api('SubSync/Subtitles/' + item.Id).then(function (tracks) {
                movieTracks = (tracks || []).filter(function (t) { return t && typeof t.Index === 'number'; });
                if (!movieTracks.length) diag('No subtitle tracks found for this movie.', true);
                render();
            }).catch(function (e) {
                diag('Could not load subtitles: ' + (e.message || e), true);
                render();
            });
        } else {
            var params = new URLSearchParams({
                ParentId: item.Id,
                IncludeItemTypes: 'Season',
                SortBy: 'SortName',
                SortOrder: 'Ascending'
            });
            var uid = currentUserId();
            api((uid ? 'Users/' + uid + '/' : '') + 'Items?' + params.toString()).then(function (data) {
                var seasons = (data && data.Items) || [];
                var scope = $('ss-scope');
                if (!scope) return;
                scope.innerHTML = '<option value="' + esc(item.Id) + '" data-name="' + esc(item.Name) + '">Whole series</option>';
                seasons.forEach(function (s) {
                    var opt = document.createElement('option');
                    opt.value = s.Id;
                    opt.setAttribute('data-name', item.Name + ' \u00b7 ' + s.Name);
                    opt.textContent = s.Name;
                    scope.appendChild(opt);
                });
                // Restore the previously selected season after reload
                if (pendingScope && scope.querySelector('option[value="' + pendingScope + '"]')) {
                    scope.value = pendingScope;
                    scopeTarget = pendingScope;
                    var opt2 = scope.options[scope.selectedIndex];
                    if (opt2) {
                        var rowName2 = $('ss-list').querySelector('.ss-row.selected .ss-name');
                        if (rowName2) rowName2.textContent = opt2.getAttribute('data-name') || opt2.textContent;
                    }
                }
                pendingScope = null;
            }).catch(function (e) { diag('Could not load seasons: ' + (e.message || e), true); });
            render();

            // The row — and with it the language select — only exists after
            // render(), so the scan has to start here. Starting it earlier
            // found no element and the Whole-series scope showed no
            // languages at all until a specific season was picked.
            var scopeNow = scopeTarget || item.Id;
            if (!langFilterCache[scopeNow]) {
                discoverLangOptions(scopeNow, scopeNow !== item.Id);
            }
        }
    }

    // ---------------- Sync ----------------
    function episodesInScope(scopeId, seasonOnly) {
        var params = new URLSearchParams({
            ParentId: scopeId,
            Recursive: 'true',
            IncludeItemTypes: 'Episode',
            SortBy: 'SortName',
            SortOrder: 'Ascending'
        });
        var uid = currentUserId();
        return api((uid ? 'Users/' + uid + '/' : '') + 'Items?' + params.toString()).then(function (data) {
            var items = ((data && data.Items) || []);
            if (seasonOnly) {
                // Belt & braces: some server layouts return sibling-season
                // episodes for a season ParentId — keep only this season's.
                items = items.filter(function (i) {
                    return i.SeasonId ? i.SeasonId === scopeId : (i.ParentId === scopeId);
                });
            }
            return items.map(function (i) { return { id: i.Id, name: i.Name }; });
        });
    }

    function listTracks(itemId) {
        return api('SubSync/Subtitles/' + itemId).then(function (tracks) {
            return (tracks || []).filter(function (t) { return t && typeof t.Index === 'number'; });
        });
    }

    // Common 3-letter codes → friendly names for the language filter.
    var LANG_NAMES = { eng: 'English', swe: 'Swedish', nor: 'Norwegian', dan: 'Danish', fin: 'Finnish',
        deu: 'German', fra: 'French', spa: 'Spanish', ita: 'Italian', nld: 'Dutch', por: 'Portuguese',
        rus: 'Russian', pol: 'Polish', tur: 'Turkish', uzb: 'Uzbek', ara: 'Arabic', heb: 'Hebrew',
        zho: 'Chinese', chi: 'Chinese', jpn: 'Japanese', kor: 'Korean', ces: 'Czech', hun: 'Hungarian',
        srp: 'Serbian', hr: 'Croatian', ell: 'Greek', rum: 'Romanian', ukr: 'Ukrainian', und: 'Unknown' };

    var langDiscoverToken = 0;

    // Populated language-filter options per scope, so re-renders of the
    // row (search typing, state restore) do not wipe a finished scan.
    var langFilterCache = {};

    // Scans the episodes of the current scope and fills the language
    // dropdown with the languages actually present (with track counts).
    function discoverLangOptions(scopeId, seasonOnly) {
        var sel = $('ss-langfilter');
        if (!sel || !scopeId) return;
        var token = ++langDiscoverToken;
        var prev = sel.getAttribute('data-value') || '*';
        delete langFilterCache[scopeId];
        sel.innerHTML = '<option value="*">All subtitles</option><option value="__scan" disabled>Scanning subtitles\u2026</option>';
        episodesInScope(scopeId, seasonOnly).then(function (episodes) {
            var counts = {};
            var forcedCounts = {};
            var seq = Promise.resolve();
            episodes.forEach(function (ep) {
                seq = seq.then(function () {
                    return listTracks(ep.id).then(function (tracks) {
                        syncableTracks(tracks).forEach(function (t) {
                            // Canonical key: sv / swe / sv-SE / Swedish are
                            // one language, not four rows in the dropdown.
                            var lang = normLang(t);
                            counts[lang] = (counts[lang] || 0) + 1;
                            if (trackForced(t)) {
                                forcedCounts[lang] = (forcedCounts[lang] || 0) + 1;
                            }
                        });
                    });
                });
            });
            return seq.then(function () {
                if (token !== langDiscoverToken) return; // scope changed meanwhile
                var codes = Object.keys(counts).sort(function (a, b) {
                    return langLabel(a).localeCompare(langLabel(b));
                });
                var html = '<option value="*">All subtitles (' + episodes.length + ' ep)</option>';
                codes.forEach(function (c) {
                    // Forced tracks are counted separately: they are signs/text tracks with a
                    // handful of cues, and picking one silently is what produced a two-cue
                    // sidecar for a whole episode.
                    var forced = forcedCounts[c] || 0;
                    html += '<option value="' + esc(c) + '">' + esc(langLabel(c))
                        + ' (' + counts[c] + ' track' + (counts[c] === 1 ? '' : 's')
                        + (forced ? ', ' + forced + ' forced' : '') + ')</option>';
                });
                sel.innerHTML = html;
                if (prev && prev !== '*' && prev !== '__scan' && codes.indexOf(prev) !== -1) {
                    sel.value = prev;
                }
                sel.setAttribute('data-value', sel.value);
                // C5: the counts travel with the options, so the row's button can state what the chosen
                // language would queue instead of guessing or saying "files".
                langFilterCache[scopeId] = { html: sel.innerHTML, value: sel.value, counts: counts, episodes: episodes.length };
                refreshRowButtonLabel();
            });
        }).catch(function () {
            if (token === langDiscoverToken) {
                sel.innerHTML = '<option value="*">Every language</option>';
            }
        });
    }

    function currentLangFilter() {
        var sel = $('ss-langfilter');
        return sel ? (sel.value || '*') : '*';
    }

    // Cached language-filter dropdown markup for the currently scoped
    // series/season (so re-renders keep a finished scan instead of
    // wiping it). No cross-scope fallback: a season must never show the
    // whole series' language list.
    function seriesLangHtml() {
        var e = scopeTarget ? langFilterCache[scopeTarget] : null;
        return e ? e.html : '<option value="*">All subtitles</option>';
    }

    // Active run attached to a server batch (own start or reattached after reload).
    var watchedBatchId = null;
    var watchedBatchTimer = null;

    function pollBatchView(batchId) {
        if (watchedBatchTimer) { clearInterval(watchedBatchTimer); watchedBatchTimer = null; }
        watchedBatchId = batchId;
        var timer = setInterval(function () {
            // Fetch what the whole server is running as well: a batch queued behind another one
            // has no running tasks of its own, and the panel must still show the work in
            // progress (with every slot drawn) instead of a bare "Queued…" line.
            refreshActive().then(function () {
            api('SubSync/Batch/' + batchId).then(function (view) {
                if (!view) return;
                var tasks = (view.Tasks) || (view.tasks) || [];
                var ceilFromServer = view.WorkerCeiling || view.workerCeiling;
                if (ceilFromServer) workerCeiling = ceilFromServer;
                var status = view.Status || view.status;
                var total = (view.Total != null) ? view.Total : view.total;
                var ok = (view.Ok != null) ? view.Ok : view.ok;
                var failed = (view.Failed != null) ? view.Failed : view.failed;
                var current = view.CurrentTask || view.currentTask;

                renderWorkerRows(view);

                // Header + progress
                var pos = (view.Completed != null) ? view.Completed : ((view.completed != null) ? view.completed : 0);
                // How many subtitles are done out of how many the run will do: the counter
                // answers "how much is left" directly, where episode numbers did not.
                var workersNow = (view.RunningTasks || view.runningTasks || []).length;
                // The workers line says what the workers are doing and nothing else: the run's totals -
                // how far along it is, how many failed - are on the counts line under the bar. Both used to
                // carry the fraction and the failure count, so every run printed them twice.
                var bits = [];
                var badge = modeBadge(view.Mode || view.mode);
                if (badge) bits.push(badge);
                var workerLimit = view.WorkerLimit || view.workerLimit || 0;
                var workerSetting = view.WorkerSetting || view.workerSetting;
                if (workersNow > 0 || workerLimit > 0) {
                    // "2/1 workers" was possible for one poll: the view's limit can trail the number of
                    // workers already running. A count that exceeds its limit says the count alone.
                    bits.push((workerLimit > 0 && workersNow <= workerLimit)
                        ? workersNow + '/' + workerLimit + ' workers'
                        : workersNow + ' workers');
                    // If the saved setting disagrees with the limit in force, say so: that
                    // disagreement is the one thing this line cannot show otherwise.
                    if (workerSetting && workerSetting > 0 && workerSetting !== workerLimit) {
                        bits.push('setting is ' + workerSetting);
                    }
                }
                $('ss-run-label').textContent = bits.join(' \u00b7 ');
                renderQueueLine(view);
                runSpinner(true);

                if (current) {
                    var phase = current.Phase || current.phase || '';
                    // The phase gets its own line only when no worker rows carry it; showing
                    // both duplicated the same sentence twice on screen.
                    $('ss-phase').textContent = workersNow > 0 ? '' : phase;
                    var prog = (current.Progress != null) ? current.Progress : (current.progress || 0);
                    var overall = total ? ((pos + prog) / total) : 0;
                    $('ss-progress').style.width = Math.round(Math.min(overall, 1) * 100) + '%';
                } else if (status === 'Queued') {
                    var elsewhereBusy = (lastActive.running || []).length;
                    $('ss-phase').textContent = queuedReason(view, elsewhereBusy);
                    runSpinner(true);
                    $('ss-progress').style.width = Math.round((total ? pos / total : 0) * 100) + '%';
                } else {
                    $('ss-run-label').textContent = 'Finished';
                    $('ss-phase').textContent = '';
                    $('ss-progress').style.width = '100%';
                    runSpinner(false);
                }

                var terminal = status === 'Completed' || status === 'Failed' || status === 'Partial' || status === 'Cancelled';
                if (terminal && !current && pos >= total) {
                    clearInterval(timer);
                    watchedBatchTimer = null;
                    watchedBatchId = null;
                    advanceToNextBatch();
                }
            }).catch(function () { /* transient poll error — keep trying */ });
            });
        }, 1000);
        watchedBatchTimer = timer;
    }

    // The panel used to update only while *this* page was streaming a batch it had started
    // itself, so a run started from the detail view left it showing "Queued — waiting for
    // earlier runs to finish…" until the page was reloaded. This mirrors whatever the server
    // is doing, whoever started it, so the numbers on screen are never stale.
    var heartbeatTimer = null;
    var mirroredBatchId = null;
    var mirroredSummary = '';

    function renderMirroredBatch(view) {
        // A run this page did not start still needs the box that shows it: the mirrored rows,
        // progress and the Cancel/Kill control all live inside `#ss-runbox`, and without this
        // line the page rendered the whole run into a hidden box. Measured in a real browser:
        // 40 s of a 50-task batch with `/SubSync/Batches` reporting `Running`, `document.hidden`
        // false, and the run box `display: none` the whole time — no progress, and no way to
        // cancel or kill from the page.
        showRunBox(true);
        var total = (view.Total != null) ? view.Total : (view.total || 0);
        var pos = (view.Completed != null) ? view.Completed : (view.completed || 0);
        var ok = (view.Ok != null) ? view.Ok : (view.ok || 0);
        var failed = (view.Failed != null) ? view.Failed : (view.failed || 0);
        var runningTasks = (view.RunningTasks || view.runningTasks || []).map(normalizeRunning);
        var limit = (view.WorkerLimit || view.workerLimit) || 0;
        var bits = [];
        var badge = modeBadge(view.Mode || view.mode);
        if (badge) bits.push(badge);
        if (runningTasks.length > 0 || limit > 0) {
            bits.push(runningTasks.length + '/' + (limit || runningTasks.length) + ' workers');
        }
        $('ss-run-label').textContent = bits.join(' \u00b7 ');
        $('ss-phase').textContent = runningTasks.length === 0
            ? ((view.Status || view.status) === 'Queued' ? queuedReason(view, 0) : 'starting\u2026')
            : '';
        runSpinner((view.Status || view.status) === 'Queued' || runningTasks.length > 0);
        $('ss-progress').style.width = Math.round((total ? pos / total : 0) * 100) + '%';
        renderQueueLine(view);
        renderWorkerRows(view);

        // Which run this is, so a different one is not mistaken for the same box (the idle state
        // takes the box away again when it ends).
        mirroredBatchId = (view.Id || view.id) || '';

        // A run that is done should not stay on screen as if it were still going.
        if (runningTasks.length === 0 && pos >= total && total > 0) {
            $('ss-phase').textContent = 'Done \u2014 ' + ok + '/' + total + ' succeeded.';
        }
        mirroredSummary = pos + '/' + total;
    }

    function showIdlePanel() {
        runSpinner(false);
        var active = (lastActive.running || []);
        if (active.length > 0) {
            return; // workers are busy elsewhere: keep showing them
        }
        renderWorkerRows(null);
        renderQueueLine(null);
        $('ss-run-label').textContent = 'Idle';
        $('ss-phase').textContent = mirroredSummary
            ? 'Last run finished at ' + mirroredSummary + '.'
            : 'Nothing queued.';
        $('ss-progress').style.width = '0%';
        // A mirrored run that has finished takes its box with it: a state that is no longer true
        // must stop being shown. A run this page started keeps its box (watchedBatchTimer/busy).
        if (!watchedBatchTimer && !busy && mirroredBatchId) {
            showRunBox(false);
            mirroredBatchId = null;
        }
    }

    // How often the page asks the server what it is doing when the user is not streaming a run from this page.
    // It used to be a flat 2 s interval that also fired while nothing was happening (measured ~1.4 requests per
    // second on an idle page, most of them the same two GETs), which is a poll a phone pays for in battery (D7).
    var heartbeatIdleMs = 10000;
    var heartbeatBusyMs = 2000;

    function startHeartbeat() {
        if (heartbeatTimer) return;
        var tick = function () {
            // A batch started from this page already streams itself every second.
            if (watchedBatchTimer || document.hidden) return;
            refreshActive().then(function () {
                return api('SubSync/Batches');
            }).then(function (list) {
                var batches = (list && list.length !== undefined ? list : ((list && (list.Batches || list.batches)) || []));
                var running = null;
                batches.forEach(function (b) {
                    var st = b.Status || b.status;
                    var id = b.Id || b.id;
                    if (!id) return;
                    if (st === 'Running') { running = id; return; }
                    if (st === 'Queued' && !running) { running = id; }
                });
                if (!running) { showIdlePanel(); return null; }
                return api('SubSync/Batch/' + running).then(function (view) {
                    if (view) renderMirroredBatch(view);
                });
            }).catch(function () { /* transient: the next tick tries again */ });
        };
        // Self-scheduling rather than a fixed interval: the next question is asked at the slow rate until a run
        // is known to be going, then at the fast rate for as long as it is. A tick can also never overlap the
        // next one, because the next one is only scheduled after this one settles.
        var schedule = function () {
            heartbeatTimer = setTimeout(function () {
                tick();
                schedule();
            }, mirroredBatchId ? heartbeatBusyMs : heartbeatIdleMs);
        };
        tick();
        schedule();
        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) tick(); // catch up immediately after a tab switch
        });
    }

    function attachBatch(batchId) {
        reattached = true;
        busy = true;
        attachPending = false;
        // The sync buttons stay enabled: more runs can be queued behind
        // this one while it streams.
        var cancel = $('ss-cancel');
        if (cancel) cancel.classList.remove('ss-hidden');
        showRunBox(true);
        $('ss-progress').style.width = '0%';
        $('ss-run-label').textContent = 'Attaching to run\u2026';
        runSpinner(true);
        pollBatchView(batchId);
    }

    // After a batch finishes, stream the next queued batch (oldest first —
    // the server runs its FIFO exactly this way). When nothing is queued,
    // return the UI to idle.
    function advanceToNextBatch() {
        api('SubSync/Batches').then(function (list) {
            var arr = list || [];
            var next = null;
            for (var i = arr.length - 1; i >= 0; i--) {
                var b = arr[i];
                var st = b.Status || b.status;
                if (st === 'Running' || st === 'Queued') { next = b; break; }
            }
            if (next && (next.Id || next.id) !== watchedBatchId) {
                busy = true;
                reattached = false;
                attachPending = false;
                pollBatchView(next.Id || next.id);
                return;
            }
            busy = false;
            reattached = false;
            attachPending = false;
            var b2 = $('ss-syncbtn');
            if (b2) b2.disabled = false;
            var cancelEl = $('ss-cancel');
            if (cancelEl) cancelEl.classList.add('ss-hidden');
            refreshHistory();
        }).catch(function () {
            busy = false;
            reattached = false;
            attachPending = false;
            var b2 = $('ss-syncbtn');
            if (b2) b2.disabled = false;
            refreshHistory();
        });
    }

    function startBatch() {
        if (!selected || startInFlight) return;
        startInFlight = true;
        var btn = $('ss-syncbtn');
        if (btn) btn.disabled = true;
        var idle = !watchedBatchTimer; // no batch is being streamed right now
        if (idle) {
            busy = true;
            showRunBox(true);
                $('ss-progress').style.width = '0%';
        }

        var isMovie = selected.Type === 'Movie';

        // Read the scope from the select AT SYNC TIME — never trust a
        // cached var: a stale change event could leave the series id
        // active while the dropdown shows a season (syncing ALL seasons).
        var scopeSel = $('ss-scope');
        var scopeId = isMovie
            ? selected.Id
            : (scopeSel && scopeSel.value ? scopeSel.value : (scopeTarget || selected.Id));

        // The scope dropdown starts with a placeholder before the season list arrives.
        // Sending anything that is not a real id makes Jellyfin reject the request
        // ("The value 'series' is not valid."), which looked like a random failure that
        // went away after a refresh. Fall back to the series itself instead.
        var idLooksReal = /^[0-9a-fA-F-]{16,}$/.test(String(scopeId || ''));
        if (!idLooksReal) {
            diag('Scope was not ready yet (' + scopeId + ') — syncing the whole series.');
            scopeId = selected.Id;
        }
        if (scopeTarget && !/^[0-9a-fA-F-]{16,}$/.test(String(scopeTarget))) {
            scopeTarget = null;
        }
        var scopeLabel = '';
        if (scopeSel && scopeSel.selectedOptions && scopeSel.selectedOptions[0]) {
            scopeLabel = scopeSel.selectedOptions[0].getAttribute('data-name')
                || scopeSel.selectedOptions[0].textContent
                || '';
        }
        scopeTarget = scopeId;
        var seasonOnly = !isMovie && scopeId !== selected.Id;
        var label = scopeLabel || selected.Name;

        $('ss-run-label').textContent = 'Loading\u2026';

        episodesInScopeChain(isMovie ? null : scopeId, seasonOnly).then(function (result) {
            var tasks = result ? result.queue : [];
            if (!tasks.length) {
                diag(selectionHasOnlyImageTracks([selected])
                    ? 'This item carries no text subtitle tracks: every track it has is an image format '
                        + '(PGS, VobSub), which cannot be aligned. Convert one to srt first.'
                    : 'No subtitle tracks found in the selected scope.', true);
                startInFlight = false;
                if (btn) btn.disabled = false;
                if (idle) busy = false;
                return;
            }
            $('ss-run-label').textContent = 'Loading\u2026';

            return postBatch(label, tasks.map(function (t) {
                return { itemId: t.itemId, subtitleIndex: t.index, title: t.title };
            })).then(function (view) {
                var batchId = (view && (view.Id || view.id)) || '';
                if (!batchId) throw new Error('server did not return a batch id');
                startInFlight = false;
                if (btn) btn.disabled = false;
                watchOrQueueBatch(batchId, label, tasks.length);
            });
        }).catch(function (e) {
            startInFlight = false;
            if (btn) btn.disabled = false;
            if (!watchedBatchTimer) busy = false;
            diag('Could not start batch: ' + (e.message || e), true);
        });
    }

    // Cancel drops queued work. Cancelling cannot interrupt a running ffsubsync,
    // so if processes are still alive the button turns into Kill, which terminates
    // them (SubSync/Kill) — with live feedback either way.
    // A run that is queued or running shows the same small spinner as the item dialog: the page can look
    // idle for a long time before the first subtitle of a file is out, and a still page reads as a stall.
    // Why a queued run has not started, straight from the server: it sets a phase on every queued job
    // ("Reading subtitles from the video - this one starts as soon as its track is out", "Waiting for a free
    // worker (3 of 4 busy)"), which is the answer the old vague wording never gave.
    function queuedReason(view, elsewhereBusy) {
        var tasks = (view && (view.Tasks || view.tasks)) || [];
        for (var i = 0; i < tasks.length; i++) {
            var st = tasks[i].Status || tasks[i].status;
            if (st === 'Queued' && tasks[i].Phase) { return tasks[i].Phase; }
        }
        if (elsewhereBusy > 0) {
            return elsewhereBusy + ' subtitle' + (elsewhereBusy === 1 ? '' : 's') + ' running from an earlier run';
        }
        return 'Queued \u2014 starting as soon as a worker is free';
    }

    function runSpinner(on) {
        var el = document.getElementById('ss-spinner');
        if (el) { el.classList.toggle('ss-hidden', !on); }
    }

    function setCancelButton(label, isKill) {
        var el = $('ss-cancel');
        if (!el) return;
        var span = el.querySelector('span');
        if (span) span.textContent = label;
        el.classList.toggle('ss-kill', !!isKill);
        el.disabled = false;
        // Any other label means the pending confirmation is gone (the run stopped by itself).
        if (!/^Confirm:/.test(label)) {
            killArmed = false;
            killArmedUntil = 0;
        }
    }

    // One press of a red button used to stop every run on the server, for every user. The first
    // press now asks, the second within eight seconds does it, and the line under it says what
    // "all" means before anything happens.
    var killArmed = false;
    var killArmedUntil = 0;

    function reportStillRunning() {
        api('SubSync/Active').then(function (a) {
            var running = (a && (a.Running || a.running)) || [];
            var queued = (a && (a.Queued != null ? a.Queued : a.queued)) || 0;
            if (running.length > 0) {
                var names = running.slice(0, 3).map(function (r) {
                    return (r.Title || r.title || 'a sync');
                }).join(', ');
                $('ss-phase').textContent = running.length + ' run' + (running.length === 1 ? '' : 's')
                    + ' still working: ' + names + ' \u2014 a running sync cannot be interrupted, use Kill to stop it.';
                setCancelButton('Kill all syncing', true);
                return;
            }

            $('ss-phase').textContent = 'Cancelled \u2014 nothing is running any more.';
            setCancelButton('Cancel', false);
            if (watchedBatchId || mirroredBatchId) refreshHistory();
        }).catch(function () {
            setCancelButton('Kill all syncing', true);
        });
    }

    function cancelWatchedBatch() {
        var el = $('ss-cancel');
        if (el && el.classList.contains('ss-kill')) {
            killAllSyncing();
            return;
        }

        // The page also shows runs it did not start (the mirror). The Cancel button is visible in that state,
        // and pressing it used to do nothing at all — no request, no line, no explanation, which reads as a
        // broken button. The batch the mirror is following is cancelled by its id instead.
        var target = watchedBatchId || mirroredBatchId;
        if (!target) return;
        if (el) el.disabled = true;
        $('ss-run-label').textContent = 'Cancelling queued tasks\u2026';
        $('ss-phase').textContent = 'Dropping tasks that have not started yet\u2026';
        api('SubSync/Batch/' + target + '/Cancel', { method: 'POST' }).then(function () {
            // Give the server a moment to reflect the running job's state.
            setTimeout(reportStillRunning, 700);
        }).catch(function (e) {
            var why = (e && e.message) ? e.message : e;
            if (el) el.disabled = false;
            $('ss-run-label').textContent = 'Cancel request failed.';
            $('ss-phase').textContent = 'The cancel was refused: ' + why;
            diag('Cancel failed: ' + why, true);
        });
    }

    function killAllSyncing() {
        // A global, id-less stop deserves a second press: this ends other people's runs too.
        if (!killArmed || Date.now() > killArmedUntil) {
            killArmed = true;
            killArmedUntil = Date.now() + 8000;
            setCancelButton('Confirm: kill all syncing', true);
            $('ss-phase').textContent = 'Press again to stop every sync on this server '
                + '\u2014 that includes runs other users started. Nothing has been stopped yet.';
            return;
        }

        killArmed = false;
        killArmedUntil = 0;
        var el = $('ss-cancel');
        if (el) el.disabled = true;
        $('ss-run-label').textContent = 'Killing running syncs\u2026';
        $('ss-phase').textContent = 'Terminating ffsubsync/ffmpeg processes and dropping the queue\u2026';
        api('SubSync/Kill', { method: 'POST', body: JSON.stringify({ all: true }) }).then(function (r) {
            var killed = (r && (r.runningKilled != null ? r.runningKilled : r.RunningKilled)) || 0;
            var dropped = (r && (r.queuedCancelled != null ? r.queuedCancelled : r.QueuedCancelled)) || 0;
            var still = (r && (r.stillRunning != null ? r.stillRunning : r.StillRunning)) || 0;
            $('ss-phase').textContent = still > 0 ? 'Waiting for ' + still + ' process(es) to exit\u2026' : 'All syncing stopped.';
            if (still > 0) {
                setTimeout(reportStillRunning, 900);
            } else {
                setCancelButton('Cancel', false);
                if (watchedBatchId) refreshHistory();
            }
        }).catch(function (e) {
            var why = (e && e.message) ? e.message : e;
            if (el) el.disabled = false;
            // A refused kill must not leave the button on the confirmation: it would read as "press again",
            // and pressing again would be refused too. Say what happened, in the line the user is reading.
            setCancelButton('Kill all syncing', true);
            $('ss-run-label').textContent = 'Kill request failed.';
            $('ss-phase').textContent = 'The kill was refused: ' + why
                + (/\b403\b/.test(String(why))
                    ? ' \u2014 stopping every run on the server needs an administrator account.' : '');
            diag('Kill failed: ' + why, true);
        });
    }

    function episodesInScopeChain(scopeId, seasonOnly) {
        // Movie: fetch its tracks directly. Series/season: episodes -> one
        // track per language per episode (external preferred when a
        // language appears more than once, e.g. embedded + external).
        var queue = [];
        if (!scopeId) {
            // movie with chosen track index
            var chosen = $('ss-trackpick') ? parseInt($('ss-trackpick').value, 10) : -1;
            return Promise.resolve({ episodeCount: 1, queue: syncableTracks(movieTracks).filter(function (t) { return chosen === -1 || t.Index === chosen; })
                .map(function (t) { return { itemId: selected.Id, index: t.Index, title: selected.Name + ' \u2014 ' + (t.Title || ('track ' + t.Index)) }; }) });
        }
        var lang = currentLangFilter();
        return episodesInScope(scopeId, seasonOnly).then(function (episodes) {
            var seq = Promise.resolve();
            episodes.forEach(function (ep) {
                seq = seq.then(function () {
                    return listTracks(ep.id).then(function (tracks) {
                        var use = syncableTracks(tracks);
                        if (lang && lang !== '*') {
                            use = use.filter(function (t) {
                                var tl = t.Language && t.Language !== 'und' ? String(t.Language).toLowerCase() : 'und';
                                return tl === lang;
                            });
                        }
                        // Collapse duplicate languages per episode. Ranked, because "first
                        // one wins" silently picked a forced track: Vargasommar S01E03 has
                        // a two-cue forced Norwegian SRT beside a full Norwegian WebVTT, and
                        // the synced sidecar came out with the two signs in it. Order:
                        // non-forced before forced, then an external file before an
                        // embedded track.
                        var byLang = {};
                        use.forEach(function (t) {
                            var key = t.Language && t.Language !== 'und' ? String(t.Language).toLowerCase() : 'und';
                            var cur = byLang[key];
                            if (!cur || trackRank(t) < trackRank(cur)) {
                                byLang[key] = t;
                            }
                        });
                        Object.keys(byLang).sort().forEach(function (key) {
                            var t = byLang[key];
                            queue.push({ itemId: ep.id, index: t.Index, title: ep.name + ' \u2014 ' + (t.Title || ('track ' + t.Index)) });
                        });
                    });
                });
            });
            return seq.then(function () { return { episodeCount: episodes.length, queue: queue }; });
        });
    }

    // ---------------- Wiring ----------------
    wireSettingsControls();
    $('ss-save').addEventListener('click', saveConfig);
    var cancelBtn = $('ss-cancel');
    if (cancelBtn) cancelBtn.addEventListener('click', cancelWatchedBatch);

    // Selection controls live in the count line (delegated, because the
    // line is re-rendered on every refresh).
    var dataline = $('ss-dataline');
    if (dataline) {
        dataline.addEventListener('click', function (e) {
            if (e.target.closest('#ss-selall-link')) {
                e.preventDefault();
                toggleSelectAllVisible();
                return;
            }
            if (e.target.closest('#ss-syncsel-btn')) {
                e.preventDefault();
                startSyncSelected();
            }
        });
    }
    var clearCacheBtn = $('ss-clearcache');
    if (clearCacheBtn) {
        clearCacheBtn.addEventListener('click', function (e) {
            e.preventDefault();
            clearCacheBtn.disabled = true;
            api('SubSync/SpeechCache/Clear', { method: 'POST' }).then(function (r) {
                var removed = (r && (r.removed !== undefined ? r.removed : r.Removed)) || 0;
                refreshStatus();
                var message = (r && (r.message || r.Message)) || ('Cleared ' + removed + ' cached audio analysis file' + (removed === 1 ? '' : 's') + '.');
                diag(message, false);
            }).catch(function (err) {
                diag('Could not clear the cache: ' + (err.message || err), true);
            }).then(function () {
                clearCacheBtn.disabled = false;
            });
        });
    }

    var globalLangInput = $('ss-langadd');
    if (globalLangInput) {
        globalLangInput.addEventListener('input', function () {
            showLangSuggestions(globalLangInput.value);
        });
        globalLangInput.addEventListener('focus', function () {
            showLangSuggestions(globalLangInput.value);
        });
        globalLangInput.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                addGlobalLanguage(globalLangInput.value);
                hideLangSuggestions();
            } else if (e.key === 'Escape') {
                hideLangSuggestions();
            }
        });
    }
    var globalLangSugg = $('ss-langsugg');
    if (globalLangSugg) {
        // mousedown, so the click lands before the input's blur hides the list
        globalLangSugg.addEventListener('mousedown', function (e) {
            var pick = e.target.closest('[data-ss-langpick]');
            if (!pick) return;
            e.preventDefault();
            addGlobalLanguage(pick.getAttribute('data-ss-langpick'));
            hideLangSuggestions();
        });
    }
    document.addEventListener('click', function (e) {
        if (e.target.closest('.ss-langfield')) return;
        hideLangSuggestions();
    });
    var globalLangBtn = $('ss-langaddbtn');
    if (globalLangBtn) {
        globalLangBtn.addEventListener('click', function (e) {
            e.preventDefault();
            addGlobalLanguage($('ss-langadd').value);
        });
    }
    var globalLangChips = $('ss-langchips');
    if (globalLangChips) {
        globalLangChips.addEventListener('click', function (e) {
            var chip = e.target.closest('[data-ss-globallang]');
            if (!chip) return;
            e.preventDefault();
            var code = chip.getAttribute('data-ss-globallang');
            syncLanguages = syncLanguages.filter(function (c) { return c !== code; });
            renderLangChips();
        });
    }
    var selLangSel = $('ss-sellang');
    if (selLangSel) {
        selLangSel.addEventListener('change', function () {
            var v = selLangSel.value || '';
            if (!v) {
                selLangs = [];
            } else if (selLangs.indexOf(v) === -1) {
                selLangs.push(v);
            }
            refreshDataline();
        });
    }
    var selBar = $('ss-selactions');
    if (selBar) {
        selBar.addEventListener('click', function (e) {
            var chip = e.target.closest('[data-ss-lang]');
            if (chip) {
                e.preventDefault();
                var code = chip.getAttribute('data-ss-lang');
                selLangs = selLangs.filter(function (c) { return c !== code; });
                refreshDataline();
                return;
            }
            if (e.target.closest('#ss-syncsel-btn')) {
                e.preventDefault();
                startSyncSelected();
            }
        });
    }
    $('ss-library').addEventListener('change', function () {
        saveState({ library: $('ss-library').value });
        loadItems();
    });
    $('ss-search').addEventListener('input', function () {
        saveState({ search: $('ss-search').value });
        render();
    });

    function showPanel(panel, remember) {
        Array.prototype.forEach.call(document.querySelectorAll('.ss-nav-item'), function (b) {
            b.classList.toggle('ss-nav-active', b.getAttribute('data-panel') === panel);
        });
        $('panel-sync').classList.toggle('ss-hidden', panel !== 'sync');
        $('panel-settings').classList.toggle('ss-hidden', panel !== 'settings');
        $('panel-history').classList.toggle('ss-hidden', panel !== 'history');
        if (panel === 'history') refreshHistory();
        if (remember !== false) saveState({ tab: panel });
    }

    document.querySelector('.ss-nav').addEventListener('click', function (e) {
        var btn = e.target.closest ? e.target.closest('.ss-nav-item') : null;
        if (!btn) return;
        showPanel(btn.getAttribute('data-panel'));
    });

    function restoreSelection(st) {
        if (!st || !st.selectedId || !allItems.length) return;
        var match = null;
        for (var i = 0; i < allItems.length; i++) if (allItems[i].Id === st.selectedId) { match = allItems[i]; break; }
        if (!match) return;
        pendingScope = st.scope || null;
        selectItem(match);
    }

    function attachActiveJobs() {
        // Refresh/exit mid-run kills only this page — the batch keeps
        // running server-side. Re-attach to the active batch so the same
        // info (task, position, phase) comes right back.
        if (reattached || attachPending) return;
        attachPending = true;
        api('SubSync/Batches').then(function (batches) {
            attachPending = false;
            var list = (batches || []);
            var running = null, queued = null;
            list.forEach(function (b) {
                var s = b.Status || b.status;
                if (s === 'Running' && !running) running = b;
                if (s === 'Queued' && !queued) queued = b;
            });
            var target = running || queued;
            if (!target) return;
            attachBatch(target.Id || target.id);
        }).catch(function () { attachPending = false; /* not reachable — ignore */ });
    }

    // Jellyfin 12 injects the plugin page as markup and the script that belongs to it (see
    // SubSyncController.GetMainScript) runs as soon as the browser reaches it — which can be before
    // the web client has defined ApiClient. Calling init() straight away then threw
    // "Uncaught ReferenceError: ApiClient is not defined" at the first setting the page read, and the
    // page stayed inert: status line on "Checking status…", every field empty, and no request made by
    // the page itself (measured in a real browser). Wait for the client, and if it never arrives, say
    // so on the page instead of showing a surface that silently does nothing.
    var initStarted = false;
    // The page reports how far it got, and why it stopped: "nothing happened" and "the web client never
    // loaded" looked identical on screen, and only one of them is the user's problem to solve.
    function initFailed(ex) {
        var line = document.getElementById('ss-status');
        var text = 'This page could not start: ' + (ex && ex.message ? ex.message : ex);
        if (line) {
            line.textContent = text;
        }
        if (window.console && console.error) {
            console.error('SubSync page init failed', ex);
        }
    }
    function whenApiReady() {
        var tries = 0;
        (function attempt() {
            window.__ssTrace.push('gate: api=' + (typeof ApiClient) + ' token=' + (token() ? 'yes' : 'no'));
            var lines = document.getElementById('ss-dataline');
            if (tries === 1 && lines) {
                lines.textContent = 'Waiting for the Jellyfin web client to hand over a session\u2026';
            }

            if ((typeof ApiClient !== 'undefined' && ApiClient) || token()) {
                window.__ssTrace.push('gate passed');
                if (!initStarted) {
                    initStarted = true;
                    try {
                        primeUserId().then(init)['catch'](initFailed);
                    } catch (ex) {
                        initFailed(ex);
                    }
                }
                return;
            }
            if (++tries > 80) {
                window.__ssTrace.push('gate gave up');
                var line = document.getElementById('ss-status');
                if (line) {
                    line.textContent = 'The Jellyfin web client did not finish loading and no session was found, so this page cannot read or change settings. Sign in again, then reload the page.';
                }
                return;
            }
            setTimeout(attempt, 250);
        })();
    }

    function init() {
        window.__ssTrace.push('init start');
        var now = Date.now();
        if (now - lastInitAt < 800) return; // pageshow + immediate can double-fire
        lastInitAt = now;

        var st = loadState();

        // Restore remembered tab, library filter and search text
        var tab = st.tab === 'settings' || st.tab === 'history' ? st.tab : 'sync';
        showPanel(tab, false);
        if (st.search) $('ss-search').value = st.search;
        if (st.library) $('ss-library').value = st.library;

        // Each step is isolated: one failing call used to stop the ones after it, which is how the page sat
        // on "Loading libraries…" with nothing else happening (a self-referencing currentUserId() did exactly
        // that in the field). Whatever fails is named on the page.
        var failed = [];
        var step = function (name, run) {
            try {
                return Promise.resolve(run())['catch'](function (e) {
                    failed.push(name + ': ' + (e && e.message ? e.message : e));
                    return null;
                });
            } catch (e) {
                failed.push(name + ': ' + (e && e.message ? e.message : e));
                return Promise.resolve(null);
            }
        };

        var all = Promise.all([
            step('status', refreshStatus),
            step('settings', loadConfig),
            step('runs', attachActiveJobs),
            step('libraries', loadLibraries),
            step('items', loadItems)
        ]);
        startHeartbeat();

        all.then(function () {
            restoreSelection(st);
            attachActiveJobs(); // re-check after selection restore (catches late-running jobs)
            if (failed.length) {
                explain('Some parts of this page could not load \u2014 ' + failed.join(' \u00b7 '));
            }
        });
    }

    document.getElementById('subsyncMainPage').addEventListener('pageshow', whenApiReady);
    if (document.readyState === 'complete' || document.readyState === 'interactive') whenApiReady();
})();

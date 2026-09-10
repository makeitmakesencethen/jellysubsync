import re

p = 'Jellyfin.Plugin.SubSync/Web/subsyncMain.html'
s = open(p).read()


def rep(old, new):
    global s
    assert old in s, 'NOT FOUND: ' + old[:110]
    s = s.replace(old, new, 1)


# --------------------------------------------------------- bulk loader (client)
rep("""            // Reads the subtitle lists of everything picked so the dropdown can""",
"""            // Loads subtitle lists for many items through the bulk endpoint, filling the
            // same caches the queue build reads. One request per chunk instead of one request
            // per file (and per episode), which is the difference between seconds and minutes
            // on a library-wide selection.
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
                                    return t && typeof (t.Index != null ? t.Index : t.index) === 'number';
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

            // Reads the subtitle lists of everything picked so the dropdown can""")

# ------------------------------------------------------ language scan uses it
start = s.index("            function startLangScan() {")
end_marker = """                seq.then(function () {
                    if (token !== langScanToken) return;
                    langScan.running = false;
                    langScan.partial = budget <= 0;
                    refreshDataline();
                });
            }"""
end = s.index(end_marker) + len(end_marker)

new_scan = """            function startLangScan() {
                var token = ++langScanToken;
                var picked = allItems.filter(function (x) { return selectedIds[x.Id]; });
                langScan = { running: picked.length > 0, done: 0, total: picked.length, langs: {}, partial: false, signature: '' };
                if (!picked.length) { refreshDataline(); return; }

                var ids = picked.map(function (x) { return x.Id; });
                bulkLoadTracks(ids, true, function (loaded, total, trackCount) {
                    if (token !== langScanToken) return;
                    langScan.done = loaded;
                    refreshDataline();
                }).then(function () {
                    if (token !== langScanToken) return;

                    // Aggregate languages from the caches: movies contribute their own
                    // tracks, series contribute every episode's.
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
                    refreshDataline();
                }).catch(function (e) {
                    if (token !== langScanToken) return;
                    langScan.running = false;
                    langScan.partial = true;
                    refreshDataline();
                    diag('Could not read every subtitle list: ' + (e.message || e), true);
                });
            }"""
s = s[:start] + new_scan + s[end:]

# --------------------------------------------- queue build works from the caches
rep("""                logLine('Building queue for ' + items.length + ' picked file' + (items.length === 1 ? '' : 's')
                    + (selLangs.length ? ' (' + selLangs.map(langLabel).join(' + ') + ' subtitles only)' : '')
                    + '\\u2026');
                var done = 0;
                var all = [];
                var seq = Promise.resolve();
                items.forEach(function (it) {
                    seq = seq.then(function () {
                        return buildTasksForItem(it).then(function (t) {
                            all = all.concat(t);
                            done++;
                            logLine('  ' + it.Name + ': ' + t.length + ' subtitle track' + (t.length === 1 ? '' : 's')
                                + (it.Type === 'Series' ? ' (all episodes)' : ''));
                            $('ss-run-label').textContent = 'Reading subtitles\\u2026 ' + done + '/' + items.length + ' file'
                                + (items.length === 1 ? '' : 's') + ', ' + all.length + ' track' + (all.length === 1 ? '' : 's') + ' so far';
                        });
                    });
                });""",
"""                logLine('Building queue for ' + items.length + ' picked file' + (items.length === 1 ? '' : 's')
                    + (selLangs.length ? ' (' + selLangs.map(langLabel).join(' + ') + ' subtitles only)' : '')
                    + '\\u2026');

                // One bulk request per chunk fills the caches; building the task list is then
                // pure bookkeeping with no further round-trips.
                var done = 0;
                var all = [];
                var seq = bulkLoadTracks(items.map(function (x) { return x.Id; }), true, function (loaded, total, trackCount) {
                    $('ss-run-label').textContent = 'Reading subtitles\\u2026 ' + loaded + '/' + total + ' file'
                        + (total === 1 ? '' : 's') + ', ' + trackCount + ' track' + (trackCount === 1 ? '' : 's') + ' so far';
                }).then(function () {
                    var inner = Promise.resolve();
                    items.forEach(function (it) {
                        inner = inner.then(function () {
                            return buildTasksForItem(it).then(function (t) {
                                all = all.concat(t);
                                done++;
                                logLine('  ' + it.Name + ': ' + t.length + ' subtitle track' + (t.length === 1 ? '' : 's')
                                    + (it.Type === 'Series' ? ' (all episodes)' : ''));
                                $('ss-run-label').textContent = 'Queued ' + done + '/' + items.length + ' file'
                                    + (items.length === 1 ? '' : 's') + ', ' + all.length + ' task' + (all.length === 1 ? '' : 's') + ' so far';
                            });
                        });
                    });
                    return inner;
                });""")

open(p, 'w').write(s)
print('client bulk loading installed')

scr = '\n'.join(re.findall(r'<script type="text/javascript">(.*?)</script>', s, re.S))
open('/tmp/lb.js', 'w').write(scr)

"""Make the worker setting observable, and put the loading text inside the language box.

1. A run reported "4/4 workers" with 8 configured. Every path that reads the setting looks
   correct, so the next run has to say what is actually happening: the effective limit is logged
   whenever it changes, and the run line shows the configured value whenever it differs from the
   limit being used. If they ever disagree, that line says so instead of leaving it to guesswork.
2. The language box no longer turns into a LOADING placeholder: its options stay, and the loading
   word and count appear inside the box while it reads.
"""
svc = '/opt/data/jellysubsync/Jellyfin.Plugin.SubSync/Services/SubSyncService.cs'
s = open(svc).read()


def rep(old, new, label, text=None):
    if text is None:
        text = s
    assert old in text, 'NOT FOUND: ' + label
    return text.replace(old, new, 1)


rep("""    public int EffectiveWorkerLimit =>
        NormalizeWorkers(Plugin.Instance?.Configuration?.ParallelWorkers ?? DefaultParallelWorkers);""",
"""    public int EffectiveWorkerLimit =>
        NormalizeWorkers(Plugin.Instance?.Configuration?.ParallelWorkers ?? DefaultParallelWorkers);

    /// <summary>
    /// The worker count exactly as configured, without the fallback to the default.
    ///
    /// Exposed so the interface can show it next to the limit actually in force: a reported
    /// "4/4 workers" with 8 configured means one of the two is not what the other thinks, and
    /// that is only visible if both are stated.
    /// </summary>
    public int ConfiguredWorkerLimit => Plugin.Instance?.Configuration?.ParallelWorkers ?? -1;

    private static int _lastLoggedWorkerLimit = -1;

    /// <summary>
    /// Logs the worker limit whenever it changes, so the running value can be checked in the log
    /// rather than inferred from behaviour.
    /// </summary>
    private void LogWorkerLimit()
    {
        var limit = EffectiveWorkerLimit;
        if (limit == _lastLoggedWorkerLimit)
        {
            return;
        }

        _lastLoggedWorkerLimit = limit;
        _logger.LogInformation(
            "SubSync worker limit is {Limit} (configured: {Configured}, ceiling: {Ceiling})",
            limit,
            ConfiguredWorkerLimit,
            MaxParallelWorkers);
    }""", 'configured limit')

rep("""            lock (_queueLock)
            {
                foreach (var finished in inFlight.Where(kvp => kvp.Key.IsCompleted).Select(kvp => kvp.Key).ToList())""",
"""            LogWorkerLimit();

            lock (_queueLock)
            {
                foreach (var finished in inFlight.Where(kvp => kvp.Key.IsCompleted).Select(kvp => kvp.Key).ToList())""", 'log per dispatch')

open(svc, 'w').write(s)
print('service: worker setting is observable')

# ---------------------------------------------------------------- controller
ctl = '/opt/data/jellysubsync/Jellyfin.Plugin.SubSync/Api/SubSyncController.cs'
c = open(ctl).read()
old = """    public int WorkerCeiling { get; set; } = Services.SubSyncService.MaxParallelWorkers;"""
assert old in c
c = c.replace(old, old + """

    /// <summary>
    /// Gets or sets the worker count as configured, so the page can show it next to the limit in
    /// force and reveal any disagreement between the two.
    /// </summary>
    public int WorkerSetting { get; set; }""", 1)
old2 = """            WorkerLimit = Services.SyncJobMode.IsParallel"""
i = c.index(old2)
c = c[:i] + """            WorkerSetting = _syncService.ConfiguredWorkerLimit,
""" + c[i:]
open(ctl, 'w').write(c)
print('controller: batch view carries the configured value')

# ---------------------------------------------------------------- page
ui = '/opt/data/jellysubsync/Jellyfin.Plugin.SubSync/Web/subsyncMain.html'
h = open(ui).read()

# the language box keeps its options; the loading word goes inside it
old = """                    if (langScan.running) {
                        // The lists are still being read: an option list that says "All languages"
                        // before anything has been read claims a completeness it does not have.
                        var progress = langScan.total
                            ? 'LOADING ' + langScan.done + '/' + langScan.total
                            : 'LOADING';
                        if (langScan.signature !== progress) {
                            langScan.signature = progress;
                            langSel.innerHTML = '<option value="">' + esc(progress) + '</option>';
                        }
                        langSel.disabled = true;
                        langSel.value = '';
                    } else {"""
new = """                    if (langScan.running) {
                        // The lists are still being read. The box keeps its own options - it does
                        // not turn into a placeholder - and says so inside itself instead.
                        langSel.disabled = true;
                        langSel.value = '';
                    } else {"""
assert old in h
h = h.replace(old, new, 1)

old = """                var scanSpin = $('ss-selscan');
                if (scanSpin) scanSpin.style.display = langScan.running ? 'inline-block' : 'none';"""
new = """                var scanSpin = $('ss-selscan');
                if (scanSpin) scanSpin.style.display = langScan.running ? 'inline-block' : 'none';

                // Loading state shown inside the language box itself.
                var inlineLoad = $('ss-langload');
                if (inlineLoad) {
                    if (langScan.running) {
                        inlineLoad.textContent = langScan.total
                            ? 'LOADING ' + langScan.done + '/' + langScan.total
                            : 'LOADING';
                        inlineLoad.classList.remove('ss-hidden');
                    } else {
                        inlineLoad.classList.add('ss-hidden');
                    }
                }"""
assert old in h
h = h.replace(old, new, 1)

# markup: wrap the select so the indicator can sit inside the same box
old = """                                <select is="emby-select" id="ss-sellang" class="ss-compact" label="Subtitles" style="max-width:260px;">
                                    <option value="">All languages</option>
                                </select>"""
new = """                                <span class="ss-langwrap">
                                    <select is="emby-select" id="ss-sellang" class="ss-compact" label="Subtitles" style="max-width:260px;">
                                        <option value="">All languages</option>
                                    </select>
                                    <span class="ss-langload ss-hidden" id="ss-langload">LOADING</span>
                                </span>"""
assert old in h
h = h.replace(old, new, 1)

# css for the wrapper and the inline indicator
old = """            .ss-workers { display: flex; flex-direction: column; gap: 6px; margin: 8px 0 2px; max-height: 420px; overflow-y: auto; }"""
new = """            .ss-workers { display: flex; flex-direction: column; gap: 6px; margin: 8px 0 2px; max-height: 420px; overflow-y: auto; }
            .ss-langwrap { position: relative; display: inline-flex; align-items: center; max-width: 260px; }
            .ss-langwrap .ss-langload {
                position: absolute; right: 10px; pointer-events: none;
                font-size: .72rem; letter-spacing: .04em; opacity: .9;
                background: rgba(0,0,0,.45); color: #fff; padding: 1px 7px; border-radius: 10px;
                animation: ss-pulse 1.4s ease-in-out infinite;
            }
            @keyframes ss-pulse { 0%, 100% { opacity: .55; } 50% { opacity: 1; } }"""
assert old in h
h = h.replace(old, new, 1)

# run line: state the configured value when it disagrees with the limit in force
old = """                        var workerLimit = view.WorkerLimit || view.workerLimit || 0;
                        if (workersNow > 1 || (workerLimit > 1 && total > workersNow)) {
                            bits.push(workerLimit > 0 ? workersNow + '/' + workerLimit + ' workers' : workersNow + ' workers');
                        }"""
new = """                        var workerLimit = view.WorkerLimit || view.workerLimit || 0;
                        var workerSetting = view.WorkerSetting || view.workerSetting;
                        if (workersNow > 1 || (workerLimit > 1 && total > workersNow)) {
                            bits.push(workerLimit > 0 ? workersNow + '/' + workerLimit + ' workers' : workersNow + ' workers');
                            if (workerSetting && workerSetting > 0 && workerSetting !== workerLimit) {
                                bits.push('setting is ' + workerSetting);
                            }
                        }"""
assert old in h
h = h.replace(old, new, 1)

open(ui, 'w').write(h)
print('page: loading sits inside the box, and a mismatched setting is stated')

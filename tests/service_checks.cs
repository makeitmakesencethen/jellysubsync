// -----------------------------------------------------------------------------------------------------------
// Phase 0: the three clusters the service map found with no behavioural coverage - C4 (the process runners),
// C8 (the sweep and the access-control surface) and C9 (engine status and install).
//
// These are characterization checks: they record today's behaviour as a safety net for the class extractions
// that follow (C7/C6/C4/C9 leaving the class, per knowledge/SUBSYNCSERVICE_MAP.md §6-§7). Nothing here is a
// correctness audit, and where today's behaviour looks wrong (the two argument styles of B18, the status that
// probes the managed path instead of the resolved one) it is recorded as it is and called out in the report.
//
// Everything is driven through the shipped code: the private runners by reflection, the access and status
// surfaces through Jellyfin's own interfaces faked with DispatchProxy, and the install path with stand-in
// python3/pip scripts, so no check needs a server, root, the network or a real ffsubsync.
// -----------------------------------------------------------------------------------------------------------
{
    var svcRoot = Path.Combine(Path.GetTempPath(), "subsync-service-" + Guid.NewGuid().ToString("N").Substring(0, 8));
    var svcBin = Path.Combine(svcRoot, "bin");
    var svcConfigDir = Path.Combine(svcRoot, "config");
    var svcDataDir = Path.Combine(svcRoot, "data");
    var svcCacheDir = Path.Combine(svcRoot, "cache");
    Directory.CreateDirectory(svcBin);
    Directory.CreateDirectory(svcConfigDir);
    Directory.CreateDirectory(svcDataDir);
    Directory.CreateDirectory(svcCacheDir);

    // A shell script the cases can run: it writes its own argv, one element per line, to a file named beside
    // it. This is how the runners' two argument styles are told apart - the same string does not reach the
    // child the same way through `ArgumentList` and through `ProcessStartInfo.Arguments`.
    string SvcScript(string name, string body, string? target = null)
    {
        var path = Path.Combine(svcBin, name);
        if (target is not null && File.Exists(target))
        {
            File.Delete(target);
        }

        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    string SvcDumpScript(string name, string dumpTo) => SvcScript(
        name,
        $"for a in \"$@\"; do printf '%s\\n' \"$a\"; done > {SvcQuote(dumpTo)}");

    string SvcQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    // The process runners and their table live in SubSyncProcesses (the C4 extraction), so a check drives the
    // class that owns the member rather than the class it used to be in.
    var svcProcesses = new SubSyncProcesses(Microsoft.Extensions.Logging.Abstractions.NullLogger<SubSyncService>.Instance);
    MethodInfo? SvcMethod(string name) => typeof(SubSyncService).GetMethod(
        name,
        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static);

    MethodInfo? SvcMethodOn(object target, string name) => target.GetType().GetMethod(
        name,
        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static);

    object? SvcField(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);

    void SvcSetField(object target, string name, object? value) => target.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(target, value);

    async Task<object?> SvcCall(object target, string name, params object?[] arguments)
    {
        var method = SvcMethodOn(target, name) ?? throw new InvalidOperationException("no method named " + name);
        var result = method.Invoke(target, arguments);
        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            var resultProperty = task.GetType().GetProperty("Result");
            return resultProperty is null ? null : resultProperty.GetValue(task);
        }

        return result;
    }

    (int Code, string Text) SvcPairInt(object? tuple)
    {
        var type = tuple!.GetType();
        return (
            (int)type.GetField("Item1")!.GetValue(tuple)!,
            (string)type.GetField("Item2")!.GetValue(tuple)!);
    }

    int SvcLiveProcesses() =>
        (SvcField(svcProcesses, "_liveProcesses") as System.Collections.Concurrent.ConcurrentDictionary<int, System.Diagnostics.Process>)?.Count ?? -1;

    // ===========================================================================================
    // C4: the five process runners, of which four are called. Characterization for B18.
    // ===========================================================================================
    Check("C4: the runner set is the four that are called, and the dead one is gone",
        SvcMethodOn(svcProcesses, "RunProcessArgumentListAsync") is not null
        && SvcMethodOn(svcProcesses, "RunProcessAsync") is not null
        && SvcMethodOn(svcProcesses, "RunProcessWithStderrCallbackAsync") is not null
        && SvcMethodOn(svcProcesses, "RunProcessCaptureAsync") is not null
        && SvcMethodOn(svcProcesses, "RunCapturedAsync") is null,
        $"present={SvcMethodOn(svcProcesses, "RunProcessArgumentListAsync") is not null}/{SvcMethodOn(svcProcesses, "RunProcessAsync") is not null}"
        + $"/{SvcMethodOn(svcProcesses, "RunProcessWithStderrCallbackAsync") is not null}/{SvcMethodOn(svcProcesses, "RunProcessCaptureAsync") is not null}"
        + $" RunCapturedAsync={(SvcMethodOn(svcProcesses, "RunCapturedAsync") is null ? "absent" : "still there")}");

    // The ArgumentList style hands each element over as one argv element, whatever is inside it.
    var svcArgvA = Path.Combine(svcRoot, "argv-list.txt");
    var svcListRunner = SvcDumpScript("argv-list.sh", svcArgvA);
    var svcListResult = SvcPairInt(await SvcCall(svcProcesses, "RunProcessArgumentListAsync",
        svcListRunner,
        new[] { "a b", "c'd", "$HOME", "x\"y" },
        svcBin,
        CancellationToken.None));
    var svcListArgv = File.Exists(svcArgvA) ? File.ReadAllLines(svcArgvA) : Array.Empty<string>();
    Check("C4: the ArgumentList runner hands each argument through untouched (one argv element per element)",
        svcListResult.Code == 0
        && svcListArgv.Length == 4
        && svcListArgv[0] == "a b"
        && svcListArgv[1] == "c'd"
        && svcListArgv[2] == "$HOME"
        && svcListArgv[3] == "x\"y",
        $"exit={svcListResult.Code} argv=[{string.Join("|", svcListArgv)}]");

    // The string style is parsed by the runtime instead: a space splits, a quote groups, and the shell is not
    // involved. This is the difference B18's consolidation has to resolve, recorded as it is today.
    var svcArgvB = Path.Combine(svcRoot, "argv-string.txt");
    var svcStringRunner = SvcDumpScript("argv-string.sh", svcArgvB);
    var svcStringExit = (int)(await SvcCall(svcProcesses, "RunProcessAsync", svcStringRunner, "a b", svcBin, CancellationToken.None))!;
    var svcStringArgv = File.Exists(svcArgvB) ? File.ReadAllLines(svcArgvB) : Array.Empty<string>();
    var svcArgvC = Path.Combine(svcRoot, "argv-quoted.txt");
    var svcQuotedRunner = SvcDumpScript("argv-quoted.sh", svcArgvC);
    await SvcCall(svcProcesses, "RunProcessAsync", svcQuotedRunner, "\"a b\" c", svcBin, CancellationToken.None);
    var svcQuotedArgv = File.Exists(svcArgvC) ? File.ReadAllLines(svcArgvC) : Array.Empty<string>();
    Check("C4: the string runner lets the runtime parse the string (a space splits, a quote groups)",
        svcStringExit == 0
        && svcStringArgv.Length == 2 && svcStringArgv[0] == "a" && svcStringArgv[1] == "b"
        && svcQuotedArgv.Length == 2 && svcQuotedArgv[0] == "a b" && svcQuotedArgv[1] == "c",
        $"unquoted=[{string.Join("|", svcStringArgv)}] quoted=[{string.Join("|", svcQuotedArgv)}]");

    // The plugin's own quoting is what makes the string runners safe for its own call sites: EscapeArg's
    // output survives that parser as a single argument, which is the property a consolidation must keep.
    var svcEscape = (string)(typeof(FfSubSyncEngine).GetMethod("EscapeArg", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object?[] { "/tmp/a b/subsync" }) ?? string.Empty);
    var svcArgvD = Path.Combine(svcRoot, "argv-escaped.txt");
    var svcEscapedRunner = SvcDumpScript("argv-escaped.sh", svcArgvD);
    await SvcCall(svcProcesses, "RunProcessAsync", svcEscapedRunner, "-m venv " + svcEscape, svcBin, CancellationToken.None);
    var svcEscapedArgv = File.Exists(svcArgvD) ? File.ReadAllLines(svcArgvD) : Array.Empty<string>();
    Check("C4: EscapeArg's quoting survives the string runner as one argument (why the install path works)",
        svcEscape == "\"/tmp/a b/subsync\""
        && svcEscapedArgv.Length == 3
        && svcEscapedArgv[0] == "-m" && svcEscapedArgv[1] == "venv" && svcEscapedArgv[2] == "/tmp/a b/subsync",
        $"escape={svcEscape} argv=[{string.Join("|", svcEscapedArgv)}]");

    // Exit codes and stderr, from every runner that reports one.
    var svcCodeExit = Path.Combine(svcRoot, "exit-code.txt");
    var svcCodeRunner = SvcScript("exit7.sh", "printf 'boom\\n' >&2\nprintf 'out\\n'\nprintf 'ran' > " + SvcQuote(svcCodeExit) + "\nexit 7");
    var svcListFailure = SvcPairInt(await SvcCall(svcProcesses, "RunProcessArgumentListAsync", svcCodeRunner, new[] { "x" }, svcBin, CancellationToken.None));
    var svcStringFailure = (int)(await SvcCall(svcProcesses, "RunProcessAsync", svcCodeRunner, "x", svcBin, CancellationToken.None))!;
    var svcCaptured = SvcPairInt(await SvcCall(svcProcesses, "RunProcessCaptureAsync", svcCodeRunner, "x", svcBin));
    var svcStderrLines = new List<string>();
    var svcCallbackExit = (int)(await SvcCall(svcProcesses, "RunProcessWithStderrCallbackAsync",
        svcCodeRunner,
        new[] { "x" },
        svcBin,
        new Action<string>(line => svcStderrLines.Add(line)),
        CancellationToken.None,
        null,
        null))!;
    Check("C4: a non-zero exit code comes back from every runner that reports one, with the stderr beside it",
        svcListFailure.Code == 7 && svcListFailure.Text.Contains("boom", StringComparison.Ordinal)
        && svcStringFailure == 7
        && svcCallbackExit == 7
        && svcCaptured.Code == 7,
        $"argumentList={svcListFailure.Code} string={svcStringFailure} callback={svcCallbackExit} capture={svcCaptured.Code}");
    Check("C4: the stderr-callback runner reports every stderr line in order and no stdout",
        svcStderrLines.Count == 1 && svcStderrLines[0] == "boom",
        $"[{string.Join("|", svcStderrLines)}]");
    Check("C4: the capture runner returns stdout and stderr concatenated and trimmed",
        svcCaptured.Text == "out" + Environment.NewLine + "boom",
        $"captured='{svcCaptured.Text.Replace(Environment.NewLine, "\\n")}'");

    // A cancelled token stops the run and kills the child, not just the wait. Run more than once on purpose: the
    // kill does not happen on every attempt, and the misses are a finding (F33 - a cancel that lands before the
    // runner arms its callback, or a kill that throws and is swallowed, leaves the child running *and* unregistered,
    // so neither Kill nor the teardown can reach it). What the check pins is the guarantee itself: a build that
    // never kills fails every attempt, while a miss in five is reported in the detail instead of being hidden by a
    // single attempt or turned into a red suite. Each attempt watches a pid that is *new since that attempt
    // started* - an earlier version took whatever pid was in the tracked table, which can be another check's pid.
    bool SvcAlive(int pid)
    {
        try
        {
            var stat = File.ReadAllText("/proc/" + pid + "/stat");
            var state = stat.Substring(stat.LastIndexOf(')') + 2, 1);
            return state != "Z";
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    static string SvcCmdLine(int pid)
    {
        try
        {
            return File.ReadAllText("/proc/" + pid + "/cmdline").Replace('\0', ' ').Trim();
        }
        catch (IOException)
        {
            return "(gone)";
        }
    }

    var svcAttempts = 5;
    var svcCancels = 0;
    var svcKills = 0;
    var svcTrace = new System.Text.StringBuilder();
    for (var svcTry = 1; svcTry <= svcAttempts; svcTry++)
    {
        var svcTrackedBefore = new HashSet<int>();
        if (SvcField(svcProcesses, "_liveProcesses") is System.Collections.Concurrent.ConcurrentDictionary<int, System.Diagnostics.Process> svcPre)
        {
            foreach (var svcPid in svcPre.Keys)
            {
                svcTrackedBefore.Add(svcPid);
            }
        }

        var svcCancelled = false;
        var svcChildPid = -1;
        using (var svcCts = new CancellationTokenSource())
        {
            var svcSleepTask = SvcCall(svcProcesses, "RunProcessArgumentListAsync", "/bin/sleep", new[] { "30" }, svcBin, svcCts.Token);

            // Wait for a pid this attempt started, then let the runner arm its kill callback (TrackChildProcess sits
            // one statement before the registration: SubSyncProcesses.cs:160-168). The budget is generous on purpose:
            // this runs under a full suite and a build on the same machine.
            var svcTrackedAt = 0L;
            for (var svcWait = 0; svcWait < 500 && svcChildPid < 0; svcWait++)
            {
                await Task.Delay(20);
                var svcTracked = (System.Collections.Concurrent.ConcurrentDictionary<int, System.Diagnostics.Process>?)
                    SvcField(svcProcesses, "_liveProcesses");
                if (svcTracked is { Count: > 0 })
                {
                    foreach (var svcPid in svcTracked.Keys)
                    {
                        if (!svcTrackedBefore.Contains(svcPid))
                        {
                            svcChildPid = svcPid;
                            svcTrackedAt = Environment.TickCount64;
                            break;
                        }
                    }
                }
            }

            if (svcChildPid > 0)
            {
                var svcArmWait = 250 - (Environment.TickCount64 - svcTrackedAt);
                if (svcArmWait > 0)
                {
                    await Task.Delay((int)svcArmWait);
                }
            }

            svcCts.Cancel();
            try
            {
                await svcSleepTask;
            }
            catch (OperationCanceledException)
            {
                svcCancelled = true;
            }
        }

        if (svcCancelled)
        {
            svcCancels++;
        }

        var svcChildAlive = svcChildPid > 0 && SvcAlive(svcChildPid);
        for (var svcWait = 0; svcWait < 50 && svcChildAlive; svcWait++)
        {
            await Task.Delay(100);
            svcChildAlive = SvcAlive(svcChildPid);
        }

        if (svcCancelled && svcChildPid > 0 && !svcChildAlive)
        {
            svcKills++;
        }
        else
        {
            svcTrace.Append($"attempt{svcTry}: cancelled={svcCancelled} pid={svcChildPid} alive={svcChildAlive} ");
            if (svcChildAlive)
            {
                // Do not leak a survivor just because the runner did: end it here, best effort (F33 owns the defect).
                try
                {
                    System.Diagnostics.Process.GetProcessById(svcChildPid).Kill(entireProcessTree: true);
                }
                catch
                {
                    // already gone
                }

                svcTrace.Append($"cmdline='{SvcCmdLine(svcChildPid)}' ");
            }
        }
    }

    Check("C4: cancelling a run ends it with a cancellation and kills the child, not just the wait",
        svcCancels == svcAttempts && svcKills > 0,
        $"cancels={svcCancels}/{svcAttempts} killed={svcKills}/{svcAttempts} {svcTrace.ToString().Trim()}");

    var svcTrackedNow = SvcLiveProcesses();
    Check("C4: no runner leaves its process in the tracked table (the teardown counts on that)",
        svcTrackedNow == 0,
        $"after={svcTrackedNow}");
    var svcPreCancelled = new CancellationTokenSource();
    svcPreCancelled.Cancel();
    var svcArgvE = Path.Combine(svcRoot, "argv-never.txt");
    var svcNeverRunner = SvcDumpScript("argv-never.sh", svcArgvE);
    var svcRefusedBeforeStart = false;
    try
    {
        await SvcCall(svcProcesses, "RunProcessArgumentListAsync", svcNeverRunner, new[] { "x" }, svcBin, svcPreCancelled.Token);
    }
    catch (OperationCanceledException)
    {
        svcRefusedBeforeStart = true;
    }

    Check("C4: an already-cancelled token refuses before the process is started",
        svcRefusedBeforeStart && !File.Exists(svcArgvE),
        $"refused={svcRefusedBeforeStart} ran={File.Exists(svcArgvE)}");

    // A missing executable is a spawn failure, not a silent false: IsPythonVenvReadyAsync catches exactly this
    // exception to answer "python3 is not installed", so the type is part of the contract.
    var svcMissing = Path.Combine(svcRoot, "no-such-binary-" + Guid.NewGuid().ToString("N").Substring(0, 6));
    var svcMissingSpawn = false;
    var svcMissingCapture = false;
    try
    {
        await SvcCall(svcProcesses, "RunProcessArgumentListAsync", svcMissing, new[] { "x" }, svcBin, CancellationToken.None);
    }
    catch (System.ComponentModel.Win32Exception)
    {
        svcMissingSpawn = true;
    }

    try
    {
        await SvcCall(svcProcesses, "RunProcessCaptureAsync", svcMissing, "x", svcBin);
    }
    catch (System.ComponentModel.Win32Exception)
    {
        svcMissingCapture = true;
    }

    Check("C4: an executable that does not exist throws the spawn failure the callers catch",
        svcMissingSpawn && svcMissingCapture,
        $"argumentList={svcMissingSpawn} capture={svcMissingCapture}");

    // ===========================================================================================
    // C9: engine status and install (D2's surface). The plugin instance is built here, so this block
    // runs after every other C# check in the harness and restores what it changes.
    // ===========================================================================================
    var svcEngine = new SubSyncService(Microsoft.Extensions.Logging.Abstractions.NullLogger<SubSyncService>.Instance, null!, null!, null!);
    // The engine resolver, the install gate and the version probes live in FfSubSyncEngine (the C9 extraction),
    // and the service holds exactly one - taken out of it here so a check drives the object the service uses,
    // not a second one that would have its own gate.
    var svcEngineLayer = (FfSubSyncEngine)SvcField(svcEngine, "_engine")!;

    // Without a plugin instance: the plugin-side fields say so, and the managed path is only a relative name.
    var svcOrphanStatus = await svcEngine.GetInstallationStatusAsync();
    Check("C9: status without a plugin instance reports the plugin-side values as unavailable",
        svcOrphanStatus.VenvPath is null
        && svcOrphanStatus.PluginIdentity == "plugin instance unavailable"
        && svcOrphanStatus.ManagedBinaryPath == Path.Join("bin", "ffsubsync")
        && !string.IsNullOrEmpty(svcOrphanStatus.LogFile)
        && svcOrphanStatus.PythonAvailable
        && (svcOrphanStatus.PythonVersion ?? string.Empty).Contains("Python", StringComparison.OrdinalIgnoreCase),
        $"venv={svcOrphanStatus.VenvPath ?? "(null)"} managed={svcOrphanStatus.ManagedBinaryPath} "
        + $"identity='{svcOrphanStatus.PluginIdentity}' python={svcOrphanStatus.PythonVersion}");

    // Install with no plugin instance: it refuses with its own sentence, and the gate is released by the
    // finally so a retry reaches the same refusal rather than "already in progress" (a gate left set would
    // refuse every later attempt for the life of the process).
    var svcOrphanInstall = string.Empty;
    var svcOrphanRetry = string.Empty;
    try
    {
        await svcEngine.InstallFfSubSyncAsync(CancellationToken.None);
    }
    catch (InvalidOperationException ex)
    {
        svcOrphanInstall = ex.Message;
    }

    try
    {
        await svcEngine.InstallFfSubSyncAsync(CancellationToken.None);
    }
    catch (InvalidOperationException ex)
    {
        svcOrphanRetry = ex.Message;
    }

    Check("C9: install without a plugin instance refuses by name, and the retry is not blocked by the gate",
        svcOrphanInstall == "Plugin not initialized."
        && svcOrphanRetry == "Plugin not initialized."
        && (int)(SvcField(svcEngineLayer, "_installing") ?? -1) == 0,
        $"first='{svcOrphanInstall}' retry='{svcOrphanRetry}' gate={SvcField(svcEngineLayer, "_installing")}");

    // The re-entrancy guard itself.
    SvcSetField(svcEngineLayer, "_installing", 1);
    var svcBusyInstall = string.Empty;
    try
    {
        await svcEngine.InstallFfSubSyncAsync(CancellationToken.None);
    }
    catch (InvalidOperationException ex)
    {
        svcBusyInstall = ex.Message;
    }

    SvcSetField(svcEngineLayer, "_installing", 0);
    Check("C9: a second install while one is running is refused by the gate",
        svcBusyInstall == "Installation is already in progress.",
        $"'{svcBusyInstall}'");

    // Now the plugin instance: status and install read their paths from it.
    var svcPaths = DispatchProxy.Create<MediaBrowser.Common.Configuration.IApplicationPaths, ServiceCheckPaths>();
    var svcXml = DispatchProxy.Create<MediaBrowser.Model.Serialization.IXmlSerializer, ServiceCheckXml>();
    var svcPathsState = (ServiceCheckPaths)(object)svcPaths;
    svcPathsState.DataPath = svcDataDir;
    svcPathsState.CachePath = svcCacheDir;
    svcPathsState.ConfigDir = svcConfigDir;
    svcPathsState.PluginsDir = Path.Combine(svcRoot, "plugins");
    var svcPlugin = new Plugin(svcPaths, svcXml, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
    var svcVenv = Path.Combine(svcDataDir, "subsync", "venv");
    var svcManaged = Path.Combine(svcVenv, "bin", "ffsubsync");

    void SvcWriteSettings(string? configuredEngine)
    {
        var settings = new Jellyfin.Plugin.SubSync.Configuration.PluginConfiguration
        {
            FfSubSyncPath = configuredEngine ?? "ffsubsync",
            SyncLanguages = Array.Empty<string>(),
            SyncModeCopy = true
        };
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(Jellyfin.Plugin.SubSync.Configuration.PluginConfiguration));
        using (var stream = File.Create(svcPlugin.SettingsFilePath))
        {
            serializer.Serialize(stream, settings);
        }

        Jellyfin.Plugin.SubSync.Services.SettingsSource.Reset();
    }

    Check("C9: the constructed plugin instance is what the status reads its paths from",
        Jellyfin.Plugin.SubSync.Plugin.Instance is not null
        && svcPlugin.VenvPath == svcVenv
        && svcPlugin.SettingsFilePath.StartsWith(svcConfigDir, StringComparison.Ordinal)
        && Path.GetDirectoryName(svcManaged) == Path.Combine(svcVenv, "bin"),
        $"venv={svcPlugin.VenvPath} settings={svcPlugin.SettingsFilePath}");

    // D2's case: a configured engine path that does not exist, with a managed binary that does. The status
    // used to report "installed" for exactly this shape and then watch every sync fail.
    Directory.CreateDirectory(Path.Combine(svcVenv, "bin"));
    File.WriteAllText(svcManaged, "#!/bin/sh\nprintf 'ffsubsync 0.5.1\\n'\n");
    File.SetUnixFileMode(svcManaged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var svcConfigured = Path.Combine(svcRoot, "configured", "ffsubsync");
    SvcWriteSettings(svcConfigured);
    var svcConfiguredStatus = await svcEngine.GetInstallationStatusAsync();
    Check("C9 (D2): a configured engine that does not exist reports not installed, and the note names it",
        svcConfiguredStatus.ResolvedBinaryPath == svcConfigured
        && !svcConfiguredStatus.IsInstalled
        && !svcEngine.EngineIsInstalled()
        && (svcConfiguredStatus.EngineNote ?? string.Empty).Contains(svcConfigured, StringComparison.Ordinal)
        && !File.Exists(svcConfigured),
        $"resolved={svcConfiguredStatus.ResolvedBinaryPath} installed={svcConfiguredStatus.IsInstalled} "
        + $"note='{svcConfiguredStatus.EngineNote}'");

    // The clean-install shape: no override, the managed binary is really there, and it answers.
    SvcWriteSettings(null);
    var svcCleanStatus = await svcEngine.GetInstallationStatusAsync();
    Check("C9: status reports the managed engine as installed and quotes the version it answered",
        svcCleanStatus.IsInstalled
        && svcCleanStatus.ResolvedBinaryPath == svcManaged
        && svcCleanStatus.FfSubSyncVersion == "ffsubsync 0.5.1"
        && svcCleanStatus.EngineNote is null
        && svcEngine.EngineIsInstalled(),
        $"installed={svcCleanStatus.IsInstalled} resolved={svcCleanStatus.ResolvedBinaryPath} "
        + $"version='{svcCleanStatus.FfSubSyncVersion}' note='{svcCleanStatus.EngineNote}'");

    // Where the version field comes from: the *managed* binary, not the engine a job would run. With a
    // configured engine that exists and answers something else, IsInstalled and ResolvedBinaryPath follow the
    // resolver while FfSubSyncVersion still quotes the managed path - recorded as it is today, and called out
    // in the report rather than corrected here.
    var svcOtherEngine = SvcScript("configured-engine.sh", "printf 'ffsubsync 9.9.9\\n'");
    SvcWriteSettings(svcOtherEngine);
    var svcOtherStatus = await svcEngine.GetInstallationStatusAsync();
    Check("C9: the version field quotes the managed binary, not the engine a job would actually run",
        svcOtherStatus.IsInstalled
        && svcOtherStatus.ResolvedBinaryPath == svcOtherEngine
        && svcOtherStatus.FfSubSyncVersion == "ffsubsync 0.5.1"
        && svcOtherEngine != svcManaged,
        $"resolved={svcOtherStatus.ResolvedBinaryPath} version='{svcOtherStatus.FfSubSyncVersion}' managed='{svcManaged}'");
    SvcWriteSettings(null);

    // Install, end to end, against stand-in python3/pip: the venv exists, pip reports success and leaves the
    // binary behind, and the install finishes without touching the network.
    var svcPython = Path.Combine(svcVenv, "bin", "python3");
    var svcPip = Path.Combine(svcVenv, "bin", "pip");
    SvcScript("unused.sh", "exit 0");
    File.WriteAllText(svcPython, "#!/bin/sh\nexit 0\n");
    File.SetUnixFileMode(svcPython, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var svcInstalled = true;
    var svcInstallError = string.Empty;
    var svcManagedTemplate = Path.Combine(svcRoot, "managed-ffsubsync.sh");
    File.WriteAllText(svcManagedTemplate, "#!/bin/sh\nprintf 'ffsubsync 0.5.1\\n'\n");
    File.WriteAllText(svcPip, "#!/bin/sh\ncp " + SvcQuote(svcManagedTemplate) + " " + SvcQuote(svcManaged) + "\nchmod 755 " + SvcQuote(svcManaged) + "\nexit 0\n");
    File.SetUnixFileMode(svcPip, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    try
    {
        await svcEngine.InstallFfSubSyncAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
        svcInstalled = false;
        svcInstallError = ex.GetType().Name + ": " + ex.Message;
    }

    var svcAfterInstall = await svcEngine.GetInstallationStatusAsync();
    Check("C9: a clean install reports success and leaves an engine the status can see",
        svcInstalled
        && File.Exists(svcManaged)
        && svcAfterInstall.IsInstalled
        && (int)(SvcField(svcEngineLayer, "_installing") ?? -1) == 0,
        $"threw={svcInstallError} binary={File.Exists(svcManaged)} installed={svcAfterInstall.IsInstalled}");

    // A failing pip is reported as a failure with its exit code, and does not leave the gate set.
    File.WriteAllText(svcPip, "#!/bin/sh\nexit 3\n");
    File.SetUnixFileMode(svcPip, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var svcPipFailed = string.Empty;
    try
    {
        await svcEngine.InstallFfSubSyncAsync(CancellationToken.None);
    }
    catch (InvalidOperationException ex)
    {
        svcPipFailed = ex.Message;
    }

    var svcPipRetry = string.Empty;
    try
    {
        await svcEngine.InstallFfSubSyncAsync(CancellationToken.None);
    }
    catch (InvalidOperationException ex)
    {
        svcPipRetry = ex.Message;
    }

    Check("C9: a failing pip install is reported with its exit code, and the gate is released afterwards",
        svcPipFailed.Contains("pip install ffsubsync failed with exit code 3", StringComparison.Ordinal)
        && svcPipRetry == svcPipFailed
        && (int)(SvcField(svcEngineLayer, "_installing") ?? -1) == 0,
        $"'{svcPipFailed}' retry='{svcPipRetry}'");

    // pip exits 0 but nothing lands at the expected path: reported, not accepted as an install.
    File.Delete(svcManaged);
    File.WriteAllText(svcPip, "#!/bin/sh\nexit 0\n");
    File.SetUnixFileMode(svcPip, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var svcNoBinary = string.Empty;
    try
    {
        await svcEngine.InstallFfSubSyncAsync(CancellationToken.None);
    }
    catch (InvalidOperationException ex)
    {
        svcNoBinary = ex.Message;
    }

    Check("C9: an install whose binary never appears is refused rather than reported as successful",
        svcNoBinary == "ffsubsync was installed but the binary was not found at the expected path."
        && !File.Exists(svcManaged),
        $"'{svcNoBinary}'");

    // No usable python3 and not root: the refusal names the cause and what to do, before any pip call.
    var svcFakePath = Path.Combine(svcRoot, "path-no-python");
    Directory.CreateDirectory(svcFakePath);
    var svcBrokenPython = Path.Combine(svcFakePath, "python3");
    File.WriteAllText(svcBrokenPython, "#!/bin/sh\nexit 1\n");
    File.SetUnixFileMode(svcBrokenPython, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var svcRealPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    var svcIsRoot = false;
    try
    {
        using var svcId = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("id", "-u")
        {
            RedirectStandardOutput = true
        })!;
        svcIsRoot = svcId.StandardOutput.ReadToEnd().Trim() == "0";
        svcId.WaitForExit();
    }
    catch (Exception)
    {
        // Without `id` the refusal below cannot be classified; the check is skipped rather than guessed at.
        svcIsRoot = true;
    }

    if (svcIsRoot)
    {
        // As root the same situation takes the apt-get path instead, which this suite must never run.
        Console.WriteLine("SKIP  C9: the non-root refusal needs a non-root process");
    }
    else
    {
        var svcNoPython = string.Empty;
        try
        {
            Environment.SetEnvironmentVariable("PATH", svcFakePath + Path.PathSeparator + svcRealPath);
            await svcEngine.InstallFfSubSyncAsync(CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            svcNoPython = ex.Message;
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", svcRealPath);
        }

        Check("C9: a missing python3 on a non-root process is refused with the cause and the fix, before any pip run",
            svcNoPython.Contains("not running as root", StringComparison.Ordinal)
            && svcNoPython.Contains("apt-get install -y python3 python3-venv", StringComparison.Ordinal)
            && (int)(SvcField(svcEngineLayer, "_installing") ?? -1) == 0,
            $"'{svcNoPython}'");
    }

    // A python3 that is not installed at all is answered with false: the spawn failure is the signal
    // ("python3 itself is not installed"), and it must not escape as an exception.
    var svcEmptyPathDir = Path.Combine(svcRoot, "path-without-python");
    Directory.CreateDirectory(svcEmptyPathDir);
    bool? svcVenvReady = null;
    try
    {
        Environment.SetEnvironmentVariable("PATH", svcEmptyPathDir);
        svcVenvReady = (bool?)await SvcCall(svcEngineLayer, "IsPythonVenvReadyAsync");
    }
    catch (Exception)
    {
        svcVenvReady = null;
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", svcRealPath);
    }

    Check("C9: a python3 that is not installed is answered with false, not with a thrown spawn failure",
        svcVenvReady == false,
        $"ready={svcVenvReady?.ToString() ?? "(threw)"}");

    // ===========================================================================================
    // C8: the access-control surface, the read models and the sweep.
    // ===========================================================================================
    var svcItems = new Dictionary<Guid, BaseItem>();
    var svcLibrary = DispatchProxy.Create<MediaBrowser.Controller.Library.ILibraryManager, ServiceCheckLibrary>();
    var svcLibraryState = (ServiceCheckLibrary)(object)svcLibrary;
    svcLibraryState.Items = svcItems;
    var svcUsers = DispatchProxy.Create<MediaBrowser.Controller.Library.IUserManager, ServiceCheckUsers>();
    var svcUsersState = (ServiceCheckUsers)(object)svcUsers;
    svcUsersState.Users = new Dictionary<Guid, Jellyfin.Database.Implementations.Entities.User>();
    var svcAccess = new SubSyncService(
        Microsoft.Extensions.Logging.Abstractions.NullLogger<SubSyncService>.Instance,
        svcLibrary,
        null!,
        svcUsers);
    typeof(BaseItem).GetProperty("LibraryManager")!.SetValue(null, null);

    ServiceCheckVideo SvcVideo(string name, string path, List<MediaBrowser.Model.Entities.MediaStream> streams)
    {
        var video = new ServiceCheckVideo
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = path,
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks,
            Sources = new List<MediaBrowser.Model.Dto.MediaSourceInfo>
            {
                new() { Path = path, MediaStreams = streams }
            }
        };
        typeof(BaseItem).GetProperty("LibraryManager")!.SetValue(video, svcLibrary);
        svcItems[video.Id] = video;
        return video;
    }

    Jellyfin.Database.Implementations.Entities.User SvcUser(IEnumerable<string>? folders, bool allFolders = false, bool admin = false)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("svc-" + Guid.NewGuid().ToString("N").Substring(0, 6), "probe", "probe")
        {
            Id = Guid.NewGuid()
        };
        var permissions = (Jellyfin.Database.Implementations.Interfaces.IHasPermissions)user;
        permissions.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.EnableAllFolders, allFolders);
        permissions.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator, admin);
        if (folders is not null)
        {
            Jellyfin.Data.UserEntityExtensions.SetPreference(
                user,
                Jellyfin.Database.Implementations.Enums.PreferenceKind.EnabledFolders,
                folders.ToArray());
        }

        svcUsersState.Users[user.Id] = user;
        return user;
    }

    BaseItem SvcFolder(string name)
    {
        var folder = new Folder { Id = Guid.NewGuid(), Name = name };
        typeof(BaseItem).GetProperty("LibraryManager")!.SetValue(folder, svcLibrary);
        svcItems[folder.Id] = folder;
        return folder;
    }

    // A library the account holds, and one it does not.
    var svcLibraryA = SvcFolder("Library A");
    var svcLibraryB = SvcFolder("Library B");
    var svcSeason = SvcFolder("Season 1");
    svcSeason.ParentId = svcLibraryA.Id;
    svcSeason.SetParent((Folder)svcLibraryA);
    var svcVideoA = SvcVideo("Episode in A", Path.Combine(svcRoot, "A.mkv"), new List<MediaBrowser.Model.Entities.MediaStream>
    {
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Codec = "h264", Index = 0 }
    });
    svcVideoA.ParentId = svcSeason.Id;
    svcVideoA.SetParent((Folder)svcSeason);
    var svcVideoB = SvcVideo("Episode in B", Path.Combine(svcRoot, "B.mkv"), new List<MediaBrowser.Model.Entities.MediaStream>
    {
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Codec = "h264", Index = 0 }
    });
    svcVideoB.ParentId = svcLibraryB.Id;
    svcVideoB.SetParent((Folder)svcLibraryB);
    var svcOrphanVideo = SvcVideo("Episode with no parent", Path.Combine(svcRoot, "C.mkv"), new List<MediaBrowser.Model.Entities.MediaStream>
    {
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Codec = "h264", Index = 0 }
    });

    var svcAllFoldersUser = SvcUser(null, allFolders: true);
    var svcLibraryAUser = SvcUser(new[] { svcLibraryA.Id.ToString("N") });
    var svcUnreadableUser = SvcUser(null);
    typeof(Jellyfin.Database.Implementations.Entities.User).GetProperty("Preferences")!.SetValue(svcUnreadableUser, null);
    var svcExplodingUser = SvcUser(null, allFolders: true);

    Check("C8: an account with all-folders permission may act on an item in any library",
        svcAccess.CanUserSeeItem(svcAllFoldersUser.Id, svcVideoA.Id)
        && svcAccess.CanUserSeeItem(svcAllFoldersUser.Id, svcVideoB.Id),
        $"A={svcAccess.CanUserSeeItem(svcAllFoldersUser.Id, svcVideoA.Id)} "
        + $"B={svcAccess.CanUserSeeItem(svcAllFoldersUser.Id, svcVideoB.Id)}");

    Check("C8: an account that holds another library is denied the item, and denied an item that no library contains",
        !svcAccess.CanUserSeeItem(svcLibraryAUser.Id, svcVideoB.Id)
        && svcAccess.CanUserSeeItem(svcLibraryAUser.Id, svcVideoA.Id)
        && !svcAccess.CanUserSeeItem(svcLibraryAUser.Id, svcOrphanVideo.Id),
        $"other={svcAccess.CanUserSeeItem(svcLibraryAUser.Id, svcVideoB.Id)} "
        + $"own={svcAccess.CanUserSeeItem(svcLibraryAUser.Id, svcVideoA.Id)} "
        + $"orphan={svcAccess.CanUserSeeItem(svcLibraryAUser.Id, svcOrphanVideo.Id)}");

    var svcUnreadableAllowed = true;
    var svcUnreadableThrew = false;
    try
    {
        svcUnreadableAllowed = svcAccess.CanUserSeeItem(svcUnreadableUser.Id, svcVideoA.Id);
    }
    catch (Exception)
    {
        svcUnreadableThrew = true;
    }

    Check("C8: an account whose folder list cannot be read is denied rather than allowed",
        !svcUnreadableThrew && !svcUnreadableAllowed,
        $"threw={svcUnreadableThrew} allowed={svcUnreadableAllowed}");

    Check("C8: an unknown account and an unknown item are both denied (fails closed)",
        !svcAccess.CanUserSeeItem(Guid.NewGuid(), svcVideoA.Id)
        && !svcAccess.CanUserSeeItem(svcAllFoldersUser.Id, Guid.NewGuid()),
        $"unknownUser={svcAccess.CanUserSeeItem(Guid.NewGuid(), svcVideoA.Id)} "
        + $"unknownItem={svcAccess.CanUserSeeItem(svcAllFoldersUser.Id, Guid.NewGuid())}");

    Check("C8: the bulk check refuses the whole request when the account itself cannot be resolved",
        svcAccess.FirstItemNotVisibleTo(Guid.NewGuid(), new[] { svcVideoA.Id, svcVideoB.Id }) == svcVideoA.Id
        && svcAccess.FirstItemNotVisibleTo(svcAllFoldersUser.Id, new[] { svcVideoA.Id, svcVideoB.Id }) is null,
        $"unresolved={svcAccess.FirstItemNotVisibleTo(Guid.NewGuid(), new[] { svcVideoA.Id, svcVideoB.Id })} "
        + $"resolved={svcAccess.FirstItemNotVisibleTo(svcAllFoldersUser.Id, new[] { svcVideoA.Id, svcVideoB.Id })}");

    Check("C8: the bulk check names the first item the account may not act on",
        svcAccess.FirstItemNotVisibleTo(svcLibraryAUser.Id, new[] { svcVideoA.Id, svcVideoB.Id, svcVideoA.Id }) == svcVideoB.Id
        && svcAccess.FirstItemNotVisibleTo(svcLibraryAUser.Id, new[] { svcVideoA.Id, svcVideoA.Id }) is null,
        $"mixed={svcAccess.FirstItemNotVisibleTo(svcLibraryAUser.Id, new[] { svcVideoA.Id, svcVideoB.Id })} "
        + $"all-visible={svcAccess.FirstItemNotVisibleTo(svcLibraryAUser.Id, new[] { svcVideoA.Id, svcVideoA.Id })}");

    var svcAncestorIds = (IReadOnlyCollection<string>?)SvcMethod("AncestorIds")!.Invoke(null, new object?[] { svcVideoA });
    Check("C8: the ancestor chain is nearest-first, undashed, and excludes the item itself",
        svcAncestorIds is not null
        && svcAncestorIds.Count == 2
        && svcAncestorIds.First() == svcSeason.Id.ToString("N")
        && svcAncestorIds.Last() == svcLibraryA.Id.ToString("N")
        && !svcAncestorIds.Contains(svcVideoA.Id.ToString("N"), StringComparer.Ordinal),
        $"[{string.Join(",", svcAncestorIds ?? Array.Empty<string>())}]");

    var svcAdminUser = SvcUser(null, allFolders: true, admin: true);
    svcUsersState.ThrowFor = svcExplodingUser.Id;
    Check("C8: IsAdministrator reads the permission, and fails closed when it cannot resolve the account",
        svcAccess.IsAdministrator(svcAdminUser.Id)
        && !svcAccess.IsAdministrator(svcAllFoldersUser.Id)
        && !svcAccess.IsAdministrator(Guid.NewGuid())
        && !svcAccess.IsAdministrator(svcExplodingUser.Id),
        $"admin={svcAccess.IsAdministrator(svcAdminUser.Id)} plain={svcAccess.IsAdministrator(svcAllFoldersUser.Id)} "
        + $"unknown={svcAccess.IsAdministrator(Guid.NewGuid())} throwing={svcAccess.IsAdministrator(svcExplodingUser.Id)}");
    svcUsersState.ThrowFor = null;

    var svcKindMissing = SubSyncService.ClassifySyncTarget(svcVideoA.Id, null, false);
    var svcKindSeries = SubSyncService.ClassifySyncTarget(svcVideoA.Id, "Series", false);
    var svcKindVideo = SubSyncService.ClassifySyncTarget(svcVideoA.Id, "Video", true);
    // Null-safe on purpose: a broken branch must make this check fail, not throw and take the harness down.
    Check("C8: a sync target is classified by kind: missing, not-a-video with its own sentence, video",
        svcKindMissing.Refusal == "The item was not found or is not available to this account." && !svcKindMissing.Found
        && svcKindSeries.Found && !svcKindSeries.IsVideo
        && (svcKindSeries.Refusal ?? string.Empty).Contains("is a series, not a video", StringComparison.Ordinal)
        && svcKindVideo.Found && svcKindVideo.IsVideo && svcKindVideo.Refusal is null,
        $"missing='{svcKindMissing.Refusal}' series='{svcKindSeries.Refusal}' video={svcKindVideo.IsVideo}");

    // The listing the page reads: own sidecars listed and flagged (S12), an image track listed with its
    // refusal (S5), the counts and paths as the queue sees them.
    var svcTrackDir = Path.Combine(svcRoot, "tracks");
    Directory.CreateDirectory(svcTrackDir);
    var svcExternalSrt = Path.Combine(svcTrackDir, "Probe Movie (2026).eng.srt");
    var svcSyncedSrt = Path.Combine(svcTrackDir, "Probe Movie (2026).SYNCED.eng.srt");
    var svcImageSub = Path.Combine(svcTrackDir, "Probe Movie (2026).sup");
    File.WriteAllText(svcExternalSrt, "1\n00:00:01,000 --> 00:00:02,000\nhello\n");
    File.WriteAllText(svcSyncedSrt, "1\n00:00:01,000 --> 00:00:02,000\nsynced\n");
    File.WriteAllText(svcImageSub, "not really a subtitle");
    var svcListed = SvcVideo("Listed movie", Path.Combine(svcTrackDir, "Listed.mkv"), new List<MediaBrowser.Model.Entities.MediaStream>
    {
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Codec = "h264", Index = 0 },
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, IsExternal = true, Language = "eng", Codec = "subrip", Index = 1, Path = svcExternalSrt },
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, IsExternal = false, Language = "swe", Codec = "subrip", Index = 2 },
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, IsExternal = true, Language = "eng", Codec = "pgssub", Index = 3, Path = svcImageSub },
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, IsExternal = true, Language = "eng", Codec = "subrip", Index = 4, Path = svcSyncedSrt, IsForced = true }
    });
    var svcTracks = svcAccess.ListSubtitles(svcListed.Id);
    Check("C8: the listing reports every subtitle track with its language, path and flags",
        svcTracks is not null && svcTracks.Count == 4
        && svcTracks[0].Language == "eng" && svcTracks[0].ExternalPath == svcExternalSrt && svcTracks[0].IsExternal
        && svcTracks[1].Language == "swe" && !svcTracks[1].IsExternal && svcTracks[1].ExternalPath is null
        && svcTracks[3].IsForced
        && svcTracks.All(t => !t.HasSyncedVersion),
        svcTracks is null ? "(null)" : string.Join(" | ", svcTracks.Select(t => $"{t.Index}:{t.Language}:{t.IsExternal}:{t.IsForced}")));
    // FirstOrDefault rather than Single: a check must fail, not throw, when the code it covers stops producing
    // exactly one flagged track - a throw would take the rest of the harness down with it and read as no failure.
    var svcPluginOutput = svcTracks?.FirstOrDefault(t => t.IsPluginOutput);
    var svcUnsupported = svcTracks?.FirstOrDefault(t => t.UnsupportedReason is not null);
    Check("C8: the plugin's own sidecar is listed and flagged, and an image track is listed with its refusal (S5/S12)",
        svcTracks is not null
        && svcTracks.Count(t => t.IsPluginOutput) == 1
        && svcPluginOutput?.ExternalPath == svcSyncedSrt
        && svcTracks.Count(t => t.UnsupportedReason is not null) == 1
        && svcUnsupported?.ExternalPath == svcImageSub
        && (svcUnsupported?.UnsupportedReason ?? string.Empty).Contains("image", StringComparison.OrdinalIgnoreCase),
        svcTracks is null
            ? "(null)"
            : string.Join(" | ", svcTracks.Select(t => $"{t.Index}:plugin={t.IsPluginOutput}:reason={t.UnsupportedReason ?? "-"}")));
    Check("C8: an item that is not a video is not listed at all",
        svcAccess.ListSubtitles(svcLibraryA.Id) is null && svcAccess.ListSubtitles(Guid.NewGuid()) is null,
        $"folder={svcAccess.ListSubtitles(svcLibraryA.Id) is null} unknown={svcAccess.ListSubtitles(Guid.NewGuid()) is null}");

    // The sweep: what it scans, what it counts, and the two reasons a track is skipped without being queued.
    var svcSweepRoot = new AggregateFolder { Id = Guid.NewGuid(), Name = "root" };
    typeof(BaseItem).GetProperty("LibraryManager")!.SetValue(svcSweepRoot, svcLibrary);
    svcLibraryState.Root = svcSweepRoot;
    var svcSweepDir = Path.Combine(svcRoot, "sweep");
    Directory.CreateDirectory(svcSweepDir);
    var svcCachedSource = Path.Combine(svcSweepDir, "Cached.eng.srt");
    var svcFailedSource = Path.Combine(svcSweepDir, "Failed.eng.srt");
    var svcAlreadySynced = Path.Combine(svcSweepDir, "Cached.SYNCED.eng.srt");
    File.WriteAllText(svcCachedSource, "1\n00:00:01,000 --> 00:00:02,000\ncached\n");
    File.WriteAllText(svcFailedSource, "1\n00:00:01,000 --> 00:00:02,000\nfailed\n");
    File.WriteAllText(svcAlreadySynced, "1\n00:00:01,000 --> 00:00:02,000\nsynced\n");
    var svcSweepVideo = SvcVideo("Sweep episode", Path.Combine(svcSweepDir, "Sweep.mkv"), new List<MediaBrowser.Model.Entities.MediaStream>
    {
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Codec = "h264", Index = 0 },
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, IsExternal = true, Language = "eng", Codec = "subrip", Index = 1, Path = svcCachedSource },
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, IsExternal = true, Language = "eng", Codec = "subrip", Index = 2, Path = svcFailedSource },
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, IsExternal = false, Language = "eng", Codec = "subrip", Index = 3, Path = Path.Combine(svcSweepDir, "Embedded.eng.srt") },
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, IsExternal = true, Language = "eng", Codec = "subrip", Index = 4, Path = Path.Combine(svcSweepDir, "Gone.eng.srt") },
        // G2: a bitmap sidecar (a DVD .sub/.idx pair or a Blu-ray .sup). It exists on disk and has no synced
        // version, so every other guard in the sweep would let it through and the queue would refuse it at
        // enqueue - one failed task per sweep, forever, on a track nothing can ever align.
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, IsExternal = true, Language = "eng", Codec = "dvd_subtitle", Index = 5, Path = Path.Combine(svcSweepDir, "Bitmap.eng.sub") }
    });
    svcSweepRoot.Children = new List<BaseItem> { svcSweepVideo };

    var svcStatePath = Path.Combine(svcPlugin.StatePath, "sweep-cache.json");
    if (File.Exists(svcStatePath))
    {
        File.Delete(svcStatePath);
    }

    var svcSeed = new SweepState(svcStatePath);
    svcSeed.Record(svcCachedSource, true, svcAlreadySynced, null);
    for (var i = 0; i < 3; i++)
    {
        svcSeed.Record(svcFailedSource, false, null, "the engine refused this track");
    }

    svcSeed.Flush();
    var svcProgress = new ServiceCheckProgress();
    var svcSweep = await svcAccess.SweepLibraryAsync(svcProgress, CancellationToken.None);
    // An embedded track is skipped without being counted (the sweep only ever enqueues external files); a
    // missing file and a not-yet-evaluated file both land in SkippedOther.
    Check("C8: the sweep scans the library and counts what it saw",
        svcSweep.ScannedItems == 1
        && svcSweep.SkippedCached == 1
        && svcSweep.SkippedFailed == 1
        && svcSweep.SkippedOther == 1
        && svcSweep.SkippedUnsupported == 1
        && svcSweep.CandidatesEnqueued == 0
        && svcSweep.Completed == 0
        && svcSweep.FailedOrCancelled == 0
        && svcProgress.Value == 1.0,
        $"scanned={svcSweep.ScannedItems} cached={svcSweep.SkippedCached} failed={svcSweep.SkippedFailed} "
        + $"other={svcSweep.SkippedOther} image={svcSweep.SkippedUnsupported} "
        + $"queued={svcSweep.CandidatesEnqueued} progress={svcProgress.Value}");

    // G2: the bitmap sidecar is skipped as an image format rather than queued and refused. The count is what
    // says which of the two happened: a queued one would show up in CandidatesEnqueued and fail later.
    Check("G2: the sweep skips an image-based subtitle file instead of queueing it",
        svcSweep.SkippedUnsupported == 1 && svcSweep.CandidatesEnqueued == 0,
        $"image={svcSweep.SkippedUnsupported} queued={svcSweep.CandidatesEnqueued}");

    // A second sweep of the same library finds the same picture: the cache is what makes a repeat cheap.
    var svcSecond = await svcAccess.SweepLibraryAsync(new ServiceCheckProgress(), CancellationToken.None);
    Check("C8: a repeat sweep skips the same tracks and queues nothing again",
        svcSecond.CandidatesEnqueued == 0 && svcSecond.SkippedCached == 1 && svcSecond.SkippedFailed == 1,
        $"queued={svcSecond.CandidatesEnqueued} cached={svcSecond.SkippedCached} failed={svcSecond.SkippedFailed}");

    // The recording half: a job's outcome is what a later sweep skips on, and a track that is not an external
    // file is not recorded at all.
    var svcRecordJob = new SyncJob { Id = "svc-record-" + Guid.NewGuid().ToString("N").Substring(0, 6) };
    var svcRecordContext = typeof(SubSyncService).GetField("_jobContexts", BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetValue(svcAccess)!;
    var svcContextType = svcRecordContext.GetType().GetGenericArguments()[1];
    var svcExternalStream = ((List<MediaBrowser.Model.Entities.MediaStream>)svcSweepVideo.Sources[0].MediaStreams!)
        .First(s => s.Index == 1);
    var svcEmbeddedStream = ((List<MediaBrowser.Model.Entities.MediaStream>)svcSweepVideo.Sources[0].MediaStreams!)
        .First(s => s.Index == 3);
    var svcConfig = new Jellyfin.Plugin.SubSync.Configuration.PluginConfiguration { SyncModeCopy = true };
    object SvcContext(MediaBrowser.Model.Entities.MediaStream stream) =>
        Activator.CreateInstance(svcContextType, svcSweepVideo, stream, 0, svcConfig)!;
    var svcContextsAdd = svcRecordContext.GetType().GetMethod("TryAdd")!;
    svcContextsAdd.Invoke(svcRecordContext, new[] { svcRecordJob.Id, SvcContext(svcExternalStream) });
    await SvcCall(svcAccess, "RecordSweepOutcome", svcRecordJob, false, null, "the engine refused this track");
    var svcFreshPath = Path.Combine(svcSweepDir, "Recorded.eng.srt");
    File.WriteAllText(svcFreshPath, "1\n00:00:01,000 --> 00:00:02,000\nrecorded\n");
    var svcRecordJob2 = new SyncJob { Id = "svc-record2-" + Guid.NewGuid().ToString("N").Substring(0, 6) };
    svcContextsAdd.Invoke(svcRecordContext, new[] { svcRecordJob2.Id, SvcContext(svcExternalStream) });
    svcContextsAdd.Invoke(svcRecordContext, new[] { "svc-embedded-" + svcRecordJob2.Id, SvcContext(svcEmbeddedStream) });
    var svcRecordedState = ((Lazy<SweepState>)SvcField(svcAccess, "_sweepState")!).Value;
    var svcRecordedEntry = svcRecordedState.Get(svcCachedSource);
    var svcEmbeddedJob = new SyncJob { Id = "svc-embedded-" + svcRecordJob2.Id };
    await SvcCall(svcAccess, "RecordSweepOutcome", svcEmbeddedJob, false, null, "embedded tracks are not swept");
    Check("C8: a sweep outcome is recorded for an external subtitle, and not for an embedded track",
        svcRecordedEntry is not null
        && svcRecordedEntry.FailStreak == 1
        && svcRecordedEntry.LastError == "the engine refused this track"
        && svcRecordedState.Get(svcSweepVideo.Sources[0].MediaStreams!.First(s => s.Index == 3).Path) is null,
        $"streak={svcRecordedEntry?.FailStreak} error='{svcRecordedEntry?.LastError}'");

    // The access surface's refusal log is written for the field, not for the check: what matters here is that
    // the denial path leaves the caller's own data alone.
    var svcDeniedPath = svcAccess.FirstItemNotVisibleTo(svcLibraryAUser.Id, new[] { svcVideoB.Id });
    Check("C8: a denial is a refusal, not an exception and not a mutation of the item it refused",
        svcDeniedPath == svcVideoB.Id && File.Exists(svcExternalSrt) && svcItems.ContainsKey(svcVideoB.Id),
        $"denied={svcDeniedPath} itemStillThere={svcItems.ContainsKey(svcVideoB.Id)}");

    // Put the settings source and the environment back the way the rest of the suite expects them.
    try
    {
        if (File.Exists(svcPlugin.SettingsFilePath))
        {
            File.Delete(svcPlugin.SettingsFilePath);
        }

        Jellyfin.Plugin.SubSync.Services.SettingsSource.Reset();
        Environment.SetEnvironmentVariable("PATH", svcRealPath);
        Directory.Delete(svcRoot, true);
    }
    catch (Exception svcCleanup)
    {
        _ = svcCleanup;
    }
}

// @@TYPES@@
/// <summary>A Video whose media sources are whatever the check says they are.</summary>
internal sealed class ServiceCheckVideo : MediaBrowser.Controller.Entities.Video
{
    /// <summary>Gets or sets the media sources this fake item reports.</summary>
    public IReadOnlyList<MediaBrowser.Model.Dto.MediaSourceInfo> Sources { get; set; }
        = Array.Empty<MediaBrowser.Model.Dto.MediaSourceInfo>();

    /// <inheritdoc />
    public override IReadOnlyList<MediaBrowser.Model.Dto.MediaSourceInfo> GetMediaSources(bool enablePathSubstitution)
        => Sources;
}

/// <summary>Stands in for Jellyfin's library manager: items by id, and a root folder for the sweep.</summary>
public class ServiceCheckLibrary : System.Reflection.DispatchProxy
{
    /// <summary>Gets or sets the items the library holds.</summary>
    public Dictionary<Guid, MediaBrowser.Controller.Entities.BaseItem> Items { get; set; } = new();

    /// <summary>Gets or sets what the root folder reports.</summary>
    public AggregateFolder? Root { get; set; }

    /// <inheritdoc />
    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case "GetItemById" when args is { Length: > 0 } && args[0] is Guid id:
                return Items.TryGetValue(id, out var item) ? item : null;
            case "get_RootFolder":
                return Root;
            case "GetItemList":
                return Items.Values.ToList();
        }

        var returnType = targetMethod?.ReturnType;
        if (returnType == typeof(string))
        {
            return string.Empty;
        }

        if (returnType == typeof(bool))
        {
            return false;
        }

        if (returnType == typeof(int))
        {
            return 0;
        }

        if (returnType is not null && returnType.IsGenericType
            && returnType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return Activator.CreateInstance(typeof(List<>).MakeGenericType(returnType.GetGenericArguments()));
        }

        return returnType is not null && returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}

/// <summary>Stands in for Jellyfin's user manager: accounts by id, and one that can be made to throw.</summary>
public class ServiceCheckUsers : System.Reflection.DispatchProxy
{
    /// <summary>Gets or sets the accounts.</summary>
    public Dictionary<Guid, Jellyfin.Database.Implementations.Entities.User> Users { get; set; } = new();

    /// <summary>Gets or sets an account whose lookup throws, for the fail-closed path.</summary>
    public Guid? ThrowFor { get; set; }

    /// <inheritdoc />
    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "GetUserById" && args is { Length: > 0 } && args[0] is Guid id)
        {
            if (ThrowFor == id)
            {
                throw new InvalidOperationException("the user store is unreachable");
            }

            return Users.TryGetValue(id, out var user) ? user : null;
        }

        var returnType = targetMethod?.ReturnType;
        if (returnType == typeof(string))
        {
            return string.Empty;
        }

        if (returnType == typeof(bool))
        {
            return false;
        }

        if (returnType == typeof(int))
        {
            return 0;
        }

        if (returnType is not null && returnType.IsGenericType
            && returnType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return Activator.CreateInstance(typeof(List<>).MakeGenericType(returnType.GetGenericArguments()));
        }

        return returnType is not null && returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}

/// <summary>Jellyfin's application paths, pointed at the harness's own temporary directories.</summary>
public class ServiceCheckPaths : System.Reflection.DispatchProxy
{
    /// <summary>Gets or sets the data path the plugin derives its venv and state from.</summary>
    public string DataPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the cache path the plugin derives its scratch directory from.</summary>
    public string CachePath { get; set; } = string.Empty;

    /// <summary>Gets or sets where the settings file is written.</summary>
    public string ConfigDir { get; set; } = string.Empty;

    /// <summary>Gets or sets the plugins directory.</summary>
    public string PluginsDir { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case "get_DataPath":
                return DataPath;
            case "get_CachePath":
                return CachePath;
            case "get_PluginConfigurationsPath":
                return ConfigDir;
            case "get_PluginsPath":
                return PluginsDir;
        }

        var returnType = targetMethod?.ReturnType;
        if (returnType == typeof(string))
        {
            return string.Empty;
        }

        if (returnType == typeof(bool))
        {
            return false;
        }

        if (returnType is not null && returnType.IsGenericType
            && returnType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return Activator.CreateInstance(typeof(List<>).MakeGenericType(returnType.GetGenericArguments()));
        }

        return returnType is not null && returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}

/// <summary>The XML serializer the plugin's base class asks for; the settings are read from the file instead.</summary>
public class ServiceCheckXml : System.Reflection.DispatchProxy
{
    /// <inheritdoc />
    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) => null;
}

/// <summary>An IProgress that records the value on the calling thread, so a check can read it back.</summary>
internal sealed class ServiceCheckProgress : IProgress<double>
{
    /// <summary>Gets the last value reported.</summary>
    public double Value { get; private set; }

    /// <inheritdoc />
    public void Report(double value) => Value = value;
}

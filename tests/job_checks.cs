// -----------------------------------------------------------------------------------------------------------
// RunSyncJob's terminals, characterized: P5, P7, P8, P9, P10, P12, P13, P14, P15, P17, P18, P19.
//
// RunSyncJob is private and has no internal wrapper, so it is driven by reflection here - the way this suite
// already reaches LogPluginCompletion. The engine it runs is the script the harness puts first on PATH (see
// FakeEngine in run_checks.py): it writes exactly the subtitle the case needs, so every terminal of the method
// can be reached without a real ffsubsync and without a media file. What these checks assert is the behaviour
// as it is today - the job state, the files on disk and the sentences in the outcome - because that is what an
// extraction must not change. Where today's behaviour looks wrong it is recorded as it is and called out in the
// report, not corrected here.
// -----------------------------------------------------------------------------------------------------------
{
    var sjRoot = Path.Combine(Path.GetTempPath(), "subsync-jobchecks-" + Guid.NewGuid().ToString("N").Substring(0, 6));
    Directory.CreateDirectory(sjRoot);
    var sjEngineDir = Environment.GetEnvironmentVariable("SUBSYNC_FAKE_ENGINE") ?? string.Empty;
    var sjHasEngine = sjEngineDir.Length > 0 && File.Exists(Path.Combine(sjEngineDir, "ffsubsync"));

    string SjSubtitle(int cues, int shiftSeconds = 0, double factor = 1.0)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < cues; i++)
        {
            var start = TimeSpan.FromSeconds(((600 + (i * 80)) * factor) + shiftSeconds);
            var end = start + TimeSpan.FromSeconds(2);
            sb.Append(i + 1).Append('\n')
              .Append(start.ToString(@"hh\:mm\:ss\,fff")).Append(" --> ").Append(end.ToString(@"hh\:mm\:ss\,fff")).Append('\n')
              .Append("Line ").Append(i + 1).Append(".\n\n");
        }

        return sb.ToString();
    }

    void SjWriteEngine(string behaviour, string payload, string? payload2 = null, string? payload3 = null, string stderr = "")
    {
        File.WriteAllText(Path.Combine(sjEngineDir, "behaviour"), behaviour);
        File.WriteAllText(Path.Combine(sjEngineDir, "calls"), "0");
        File.WriteAllText(Path.Combine(sjEngineDir, "stderr.txt"), stderr);
        File.WriteAllText(Path.Combine(sjEngineDir, "payload.srt"), payload);
        foreach (var (n, text) in new[] { (2, payload2), (3, payload3) })
        {
            var path = Path.Combine(sjEngineDir, "payload." + n + ".srt");
            if (text is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, text);
            }
        }

        var argv = Path.Combine(sjEngineDir, "argv.log");
        if (File.Exists(argv))
        {
            File.Delete(argv);
        }
    }

    var sjService = new SubSyncService(Microsoft.Extensions.Logging.Abstractions.NullLogger<SubSyncService>.Instance, null!, null!, null!);
    var sjRun = typeof(SubSyncService).GetMethod("RunSyncJob", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    var sjCases = new List<(string Label, SyncJob Job)>();
    var sjWritten = new List<string>();

    string SjSidecarFor(string caseDir, string sidecar)
        => Path.Combine(caseDir, Path.GetFileNameWithoutExtension(sidecar) + ".SYNCED.srt");

    async Task<(SyncJob Job, string CaseDir, string Sidecar, string Original, string Media)> SjRunCase(
        string label,
        string behaviour,
        string payload,
        bool embedded = false,
        bool sibling = false,
        bool fixFramerate = false,
        string? payload2 = null,
        string? payload3 = null,
        string stderr = "",
        int cues = 40,
        bool replaceMode = false)
    {
        var caseDir = Path.Combine(sjRoot, "case-" + label);
        Directory.CreateDirectory(caseDir);
        var media = Path.Combine(caseDir, "Probe Movie (2026).mkv");
        File.WriteAllText(media, "not a real video");
        var sidecar = Path.Combine(caseDir, "Probe Movie (2026).eng.srt");
        var original = SjSubtitle(cues);
        File.WriteAllText(sidecar, original);
        SjWriteEngine(behaviour, payload, payload2, payload3, stderr);

        var streams = new List<MediaBrowser.Model.Entities.MediaStream>
        {
            new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Codec = "h264", Index = 0 },
            new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Audio, Codec = "aac", Index = 1 },
        };
        if (sibling || embedded)
        {
            streams.Add(new MediaBrowser.Model.Entities.MediaStream
            {
                Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle,
                IsExternal = false,
                Language = "eng",
                Codec = "subrip",
                Index = 2,
            });
        }

        var video = new JobCheckVideo
        {
            Id = Guid.NewGuid(),
            Path = media,
            Name = "Probe Movie (2026)",
            RunTimeTicks = TimeSpan.FromMinutes(70).Ticks,
            Sources = new List<MediaBrowser.Model.Dto.MediaSourceInfo>
            {
                new() { Path = media, MediaStreams = streams },
            },
        };

        var stream = new MediaBrowser.Model.Entities.MediaStream
        {
            Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle,
            IsExternal = !embedded,
            Path = embedded ? null : sidecar,
            Language = "eng",
            Codec = "subrip",
            Index = embedded ? 2 : 1,
        };

        // The plugin builds a sibling reference from text it can already reach, and the first place it looks is
        // the extracted-subtitle cache for this file.
        if (sibling)
        {
            SubtitleCache.Store(media, "0", SjSubtitle(40));
            SubtitleCache.Store(media, "2", SjSubtitle(40));
        }

        var job = new SyncJob
        {
            Id = "sj-" + label,
            ItemId = video.Id,
            SubtitleIndex = stream.Index,
            Mode = "normal",
        };
        var config = new Jellyfin.Plugin.SubSync.Configuration.PluginConfiguration
        {
            SyncModeCopy = !replaceMode,
            MaxOffsetSeconds = 180,
            FixFramerate = fixFramerate,
            ParallelWorkers = 1,
        };

        if (sjRun is not null)
        {
            var task = (Task)sjRun.Invoke(
                sjService,
                new object?[] { job, video, stream, 0, config, CancellationToken.None })!;
            await task.ConfigureAwait(false);
        }

        sjCases.Add((label, job));
        if (job.Status == SyncJobStatus.Completed && job.OutputPath is not null)
        {
            sjWritten.Add(label);
        }

        return (job, caseDir, sidecar, original, media);
    }

    string SjArgv(int index)
    {
        var argv = Path.Combine(sjEngineDir, "argv.log");
        if (!File.Exists(argv))
        {
            return string.Empty;
        }

        var lines = File.ReadAllLines(argv);
        return index < lines.Count() ? lines[index] : string.Empty;
    }

    int SjEngineRuns()
    {
        var argv = Path.Combine(sjEngineDir, "argv.log");
        return File.Exists(argv) ? File.ReadAllLines(argv).Length : 0;
    }

    Check("RunSyncJob is reachable for characterization (its terminals can be driven at all)",
        sjHasEngine && sjRun is not null,
        $"fake engine on PATH={sjHasEngine} RunSyncJob found={sjRun is not null}");

    if (sjHasEngine && sjRun is not null)
    {
        // ---------------- P7: an engine that writes nothing is "already in sync" ----------------
        var sjA = await SjRunCase("p7-no-output", "none", SjSubtitle(40));
        Check("P7: an engine that writes no output completes as already in sync, with no output path (terminal A)",
            sjA.Job.Status == SyncJobStatus.Completed
            && sjA.Job.Phase == "Complete"
            && (sjA.Job.Outcome ?? string.Empty).StartsWith("already in sync (shift under 3 s)", StringComparison.Ordinal)
            && sjA.Job.OutputPath is null
            && sjA.Job.Progress == 1.0
            && !File.Exists(SjSidecarFor(sjA.CaseDir, sjA.Sidecar)),
            $"{sjA.Job.Status}/{sjA.Job.Phase} outcome='{sjA.Job.Outcome}' output={sjA.Job.OutputPath ?? "(null)"}");

        // ---------------- P12: an output identical to the input is "changed nothing" ----------------
        var sjF = await SjRunCase("p12-identical", "payload", SjSubtitle(40));
        Check("P12: an output identical to the engine's input completes as 'changed nothing' (terminal F)",
            sjF.Job.Status == SyncJobStatus.Completed
            && (sjF.Job.Outcome ?? string.Empty).StartsWith("already in sync (+0 ms offset) \u2014 nothing written", StringComparison.Ordinal)
            && sjF.Job.OutputPath is null
            && !File.Exists(SjSidecarFor(sjF.CaseDir, sjF.Sidecar)),
            $"{sjF.Job.Status} outcome='{sjF.Job.Outcome}' output={sjF.Job.OutputPath ?? "(null)"}");

        // ---------------- P13: an embedded track whose only ruler is the audio is unverified ----------------
        // The embedded input is probed with ffmpeg (ResolveContainerSubtitleIndexAsync) and extracted by the
        // fallback, so this one case points JELLYFIN_FFMPEG at the harness's stand-in for its own run only: the
        // suite's own ffmpeg checks (B8) must keep the real binary.
        var sjRealFfmpeg = Environment.GetEnvironmentVariable("JELLYFIN_FFMPEG");
        Environment.SetEnvironmentVariable("JELLYFIN_FFMPEG", Environment.GetEnvironmentVariable("SUBSYNC_FAKE_FFMPEG"));
        var sjG = await SjRunCase("p13-unverified", "payload", SjSubtitle(40, 5), embedded: true);
        Environment.SetEnvironmentVariable("JELLYFIN_FFMPEG", sjRealFfmpeg);
        Check("P13: an embedded track aligned against the audio alone is refused as unverified (terminal G)",
            sjG.Job.Status == SyncJobStatus.Failed
            && sjG.Job.Phase == "Unverified \u2014 audio-only alignment"
            && (sjG.Job.Error ?? string.Empty).StartsWith("unverified: this subtitle was aligned against the audio", StringComparison.Ordinal)
            && (sjG.Job.Error ?? string.Empty).Contains("+5000 ms offset", StringComparison.Ordinal)
            && sjG.Job.OutputPath is null
            && !File.Exists(SjSidecarFor(sjG.CaseDir, sjG.Sidecar)),
            $"{sjG.Job.Status}/{sjG.Job.Phase} error='{sjG.Job.Error}'");

        // ---------------- P14: an engine output with no subtitles is refused before the copy ----------------
        var sjEmpty = await SjRunCase("p14-empty-output", "empty", SjSubtitle(40, 5));
        Check("P14: an engine output with no subtitles in it fails before anything is written next to the media",
            sjEmpty.Job.Status == SyncJobStatus.Failed
            && (sjEmpty.Job.Error ?? string.Empty).Contains("synced output is missing or empty", StringComparison.Ordinal)
            && sjEmpty.Job.OutputPath is null
            && !File.Exists(SjSidecarFor(sjEmpty.CaseDir, sjEmpty.Sidecar)),
            $"{sjEmpty.Job.Status} error='{sjEmpty.Job.Error}' output={sjEmpty.Job.OutputPath ?? "(null)"}");

        // ---------------- P5: a non-zero engine exit fails the job and quotes the engine ----------------
        var sjFail = await SjRunCase("p5-engine-exit", "exit1", SjSubtitle(40, 5),
            stderr: "ffsubsync: could not read reference");
        Check("P5: a non-zero engine exit fails the job and quotes the engine's last output",
            sjFail.Job.Status == SyncJobStatus.Failed
            && (sjFail.Job.Error ?? string.Empty) == "ffsubsync exited with code 3. Last output: ffsubsync: could not read reference",
            $"{sjFail.Job.Status} error='{sjFail.Job.Error}'");

        // ---------------- P15 (copy mode) + P17 + P18 ----------------
        var sjCopy = await SjRunCase("p15-copy", "payload", SjSubtitle(40, 5));
        Check("P15: copy mode writes a .SYNCED sidecar, leaves the user's file untouched and reports the offset (P17)",
            sjCopy.Job.Status == SyncJobStatus.Completed
            && sjCopy.Job.OutputPath == SjSidecarFor(sjCopy.CaseDir, sjCopy.Sidecar)
            && File.Exists(SjSidecarFor(sjCopy.CaseDir, sjCopy.Sidecar))
            && new FileInfo(SjSidecarFor(sjCopy.CaseDir, sjCopy.Sidecar)).Length > 0
            && File.ReadAllText(sjCopy.Sidecar) == sjCopy.Original
            && string.Equals(sjCopy.Job.Outcome, "+5000 ms offset", StringComparison.Ordinal),
            $"status={sjCopy.Job.Status} output={sjCopy.Job.OutputPath} outcome='{sjCopy.Job.Outcome}'");

        // A signs-sized track still gets its note, and still writes: the note is reported, not a refusal.
        var sjSigns = await SjRunCase("p17-signs-note", "payload", SjSubtitle(3, 5), cues: 3);
        Check("P17: a signs-sized track adds its note to the outcome and the sidecar is still written",
            sjSigns.Job.Status == SyncJobStatus.Completed
            && (sjSigns.Job.Outcome ?? string.Empty).Contains("+5000 ms offset", StringComparison.Ordinal)
            && (sjSigns.Job.Outcome ?? string.Empty).Contains("looks like a forced/signs track", StringComparison.Ordinal)
            && File.Exists(SjSidecarFor(sjSigns.CaseDir, sjSigns.Sidecar)),
            $"outcome='{sjSigns.Job.Outcome}' written={File.Exists(SjSidecarFor(sjSigns.CaseDir, sjSigns.Sidecar))}");

        // ---------------- P15 (replace mode) ----------------
        var sjReplace = await SjRunCase("p15-replace", "payload", SjSubtitle(40, 5), replaceMode: true);
        var sjBackup = sjReplace.Sidecar + ".bak.subsync";
        Check("P15: replace mode replaces the user's file and keeps the original as <name>.bak.subsync",
            sjReplace.Job.Status == SyncJobStatus.Completed
            && sjReplace.Job.OutputPath == sjReplace.Sidecar
            && File.Exists(sjBackup)
            && File.ReadAllText(sjBackup) == sjReplace.Original
            && File.ReadAllText(sjReplace.Sidecar) != sjReplace.Original
            && (sjReplace.Job.Outcome ?? string.Empty).EndsWith("original replaced, kept at " + Path.GetFileName(sjBackup), StringComparison.Ordinal),
            $"status={sjReplace.Job.Status} backup={File.Exists(sjBackup)} outcome='{sjReplace.Job.Outcome}'");

        // ---------------- P19: the rollback ----------------
        // The engine writes an output this process cannot read, so the temp -> original copy fails after the
        // backup was taken: the shape the rollback exists for.
        var sjRollback = await SjRunCase("p19-rollback", "chmod000", SjSubtitle(40, 5), replaceMode: true);
        Check("P19: a replace that fails after the backup restores the original and removes the backup",
            sjRollback.Job.Status == SyncJobStatus.Failed
            && File.ReadAllText(sjRollback.Sidecar) == sjRollback.Original
            && !File.Exists(sjRollback.Sidecar + ".bak.subsync")
            && (sjRollback.Job.Error ?? string.Empty).Contains("is denied", StringComparison.Ordinal),
            $"status={sjRollback.Job.Status} original-restored={File.ReadAllText(sjRollback.Sidecar) == sjRollback.Original} "
            + $"backup-left={File.Exists(sjRollback.Sidecar + ".bak.subsync")} error='{sjRollback.Job.Error}'");

        // ---------------- P10: the rescale refusal and its accepted twin ----------------
        var sjRescale = await SjRunCase("p10-rescale-refused", "payload", SjSubtitle(40, factor: 1.05));
        Check("P10: a rescaled result that is not a framerate pair is refused, quoting the ratio",
            sjRescale.Job.Status == SyncJobStatus.Failed
            && sjRescale.Job.Phase == "Refused"
            && (sjRescale.Job.Error ?? string.Empty).Contains("refused: the engine rescaled the timings", StringComparison.Ordinal)
            && (sjRescale.Job.Error ?? string.Empty).Contains("1.0500", StringComparison.Ordinal)
            && (sjRescale.Job.Error ?? string.Empty).EndsWith("Turn on \"Correct framerate mismatch\" only for subtitles from a different framerate.", StringComparison.Ordinal)
            && sjRescale.Job.OutputPath is null,
            $"{sjRescale.Job.Status}/{sjRescale.Job.Phase} error='{sjRescale.Job.Error}'");

        var sjPal = await SjRunCase("p10-rescale-accepted", "payload", SjSubtitle(40, factor: 1.0416667), fixFramerate: true);
        Check("P10: a PAL-like rescale with correction on is written, and the outcome names the factor",
            sjPal.Job.Status == SyncJobStatus.Completed
            && (sjPal.Job.Outcome ?? string.Empty).Contains("+91666 ms offset at start", StringComparison.Ordinal)
            && (sjPal.Job.Outcome ?? string.Empty).Contains("ratio 1.0417", StringComparison.Ordinal)
            && sjPal.Job.OutputPath is not null
            && File.Exists(sjPal.Job.OutputPath),
            $"{sjPal.Job.Status} outcome='{sjPal.Job.Outcome}' output={sjPal.Job.OutputPath ?? "(null)"}");

        // ---------------- P9: the wide-window ladder ----------------
        var sjWideNone = await SjRunCase("p9-wide-nothing", "payload", SjSubtitle(40, 180), payload2: "__NONE__");
        Check("P9: a window-pinned answer whose wider retry writes nothing refuses and names the setting",
            sjWideNone.Job.Status == SyncJobStatus.Failed
            && sjWideNone.Job.Phase == "Refused"
            && (sjWideNone.Job.Error ?? string.Empty).StartsWith("refused: the 360 s window produced nothing (exit 0).", StringComparison.Ordinal)
            && (sjWideNone.Job.Error ?? string.Empty).Contains("Raise \"Maximum offset\"", StringComparison.Ordinal),
            $"{sjWideNone.Job.Status} error='{sjWideNone.Job.Error}' runs={SjEngineRuns()}");

        var sjWideClamped = await SjRunCase("p9-wide-clamped", "payload", SjSubtitle(40, 180), payload2: SjSubtitle(40, 360));
        Check("P9: a wider retry that is itself pinned refuses with the wider window's own number",
            sjWideClamped.Job.Status == SyncJobStatus.Failed
            && sjWideClamped.Job.Phase == "Refused"
            && (sjWideClamped.Job.Error ?? string.Empty).StartsWith("refused: the 360 s window also reached its limit (360000 ms).", StringComparison.Ordinal)
            && (sjWideClamped.Job.Error ?? string.Empty).Contains("Raise \"Maximum offset\"", StringComparison.Ordinal)
            && SjEngineRuns() == 2,
            $"{sjWideClamped.Job.Status} error='{sjWideClamped.Job.Error}' runs={SjEngineRuns()}");

        var sjWideVerify = await SjRunCase(
            "p9-wide-verify-refuses", "payload", SjSubtitle(40, 180), payload2: SjSubtitle(40, 250), payload3: SjSubtitle(40, 340));
        Check("P9: a wide-window answer the film's audio still disagrees with is refused with the residual",
            sjWideVerify.Job.Status == SyncJobStatus.Failed
            && (sjWideVerify.Job.Error ?? string.Empty).Contains("still asked for 90000 ms more (ratio 1.0000)", StringComparison.Ordinal)
            && SjEngineRuns() == 3,
            $"{sjWideVerify.Job.Status} error='{sjWideVerify.Job.Error}' runs={SjEngineRuns()}");

        var sjWideAccept = await SjRunCase(
            "p9-wide-accepted", "payload", SjSubtitle(40, 180), payload2: SjSubtitle(40, 250), payload3: SjSubtitle(40, 253));
        Check("P9: a wide-window answer that holds against the audio is written",
            sjWideAccept.Job.Status == SyncJobStatus.Completed
            && sjWideAccept.Job.OutputPath is not null
            && File.Exists(sjWideAccept.Job.OutputPath)
            && string.Equals(sjWideAccept.Job.Outcome, "+250000 ms offset", StringComparison.Ordinal),
            $"{sjWideAccept.Job.Status} outcome='{sjWideAccept.Job.Outcome}' runs={SjEngineRuns()}");

        // ---------------- P8: a wrong-cut subtitle ruler ----------------
        var sjWrongCut = await SjRunCase("p8-ruler-discarded", "payload", SjSubtitle(40, 45), sibling: true, payload2: SjSubtitle(40, 5));
        var sjWrongCutText = sjWrongCut.Job.OutputPath is not null && File.Exists(sjWrongCut.Job.OutputPath)
            ? File.ReadAllText(sjWrongCut.Job.OutputPath)
            : string.Empty;
        Check("P8: a subtitle ruler demanding a shift past the ceiling is discarded and the audio's answer written",
            sjWrongCut.Job.Status == SyncJobStatus.Completed
            && (sjWrongCut.Job.Outcome ?? string.Empty).StartsWith("the file's own subtitle track is not the same cut, so this was aligned against the audio", StringComparison.Ordinal)
            && sjWrongCut.Job.OutputPath is not null
            && SjEngineRuns() == 2
            && !SjArgv(1).Contains("/subsync/ref/", StringComparison.Ordinal)
            && sjWrongCutText.Contains("00:10:05,000", StringComparison.Ordinal)     // the audio's answer (+5 s)
            && !sjWrongCutText.Contains("00:10:45,000", StringComparison.Ordinal),   // not the discarded ruler's (+45 s)
            $"status={sjWrongCut.Job.Status} outcome='{sjWrongCut.Job.Outcome}' runs={SjEngineRuns()} "
            + $"written-cue={sjWrongCutText.Split('\n').Skip(1).FirstOrDefault() ?? "(none)"}");

        var sjRefused = await SjRunCase("p8-refusal", "payload", SjSubtitle(40, 45), sibling: true, payload2: "__DELETE__");
        Check("P8: a wrong-cut ruler whose audio retry produces nothing refuses and says nothing was written",
            sjRefused.Job.Status == SyncJobStatus.Failed
            && sjRefused.Job.Phase == "Refused"
            && (sjRefused.Job.Error ?? string.Empty).Contains("demanded a 45000 ms shift", StringComparison.Ordinal)
            && (sjRefused.Job.Error ?? string.Empty).Contains("against the file's own subtitle track s:0", StringComparison.Ordinal)
            && (sjRefused.Job.Error ?? string.Empty).Contains("Nothing was written.", StringComparison.Ordinal)
            && sjRefused.Job.OutputPath is null,
            $"{sjRefused.Job.Status}/{sjRefused.Job.Phase} error='{sjRefused.Job.Error}' runs={SjEngineRuns()}");

        // ---------------- S45: the audio retry must not write the discarded ruler's answer ----------------
        // The audio retry writes to a path of its own (audio-fallback.srt), so a run that exits 0 without writing
        // cannot be mistaken for the file the discarded ruler's run left behind. Before that fix this case wrote
        // the reference's +45 s answer to the library and reported it as the audio's; it now refuses, which is the
        // sentence the refusal is supposed to produce.
        var sjStale = await SjRunCase("p8-stale-output", "payload", SjSubtitle(40, 45), sibling: true, payload2: "__NONE__");
        Check("S45: the audio retry writes to its own path, so a stale reference output is not taken for its answer",
            sjStale.Job.Status == SyncJobStatus.Failed
            && sjStale.Job.Phase == "Refused"
            && (sjStale.Job.Error ?? string.Empty).Contains("demanded a 45000 ms shift", StringComparison.Ordinal)
            && (sjStale.Job.Error ?? string.Empty).Contains("Nothing was written.", StringComparison.Ordinal)
            && sjStale.Job.OutputPath is null
            && !File.Exists(SjSidecarFor(sjStale.CaseDir, sjStale.Sidecar))
            && SjEngineRuns() == 2,
            $"status={sjStale.Job.Status}/{sjStale.Job.Phase} error='{sjStale.Job.Error}' runs={SjEngineRuns()} "
            + $"sidecar={File.Exists(SjSidecarFor(sjStale.CaseDir, sjStale.Sidecar))}");

        // ---------------- the reference the engine is actually handed (S43) ----------------
        var sjVad = await SjRunCase("p8-audio-vad", "payload", SjSubtitle(40, 5), sibling: true);
        Check("S43: a vetted subtitle ruler is handed to the engine as a file and the audio VAD is not forced",
            SjEngineRuns() == 1
            && !SjArgv(0).Contains("--vad webrtc", StringComparison.Ordinal)
            && SjArgv(0).Contains("/subsync/ref/", StringComparison.Ordinal),
            $"runs={SjEngineRuns()} argv={SjArgv(0)}");

        // ---------------- P18: the library half must never fail a finished job ----------------
        // Every case above ran with a null library monitor and a null library manager. The plugin reports the
        // folder and refreshes the item inside try/catch for exactly this reason (the comment on that block says
        // "a library hiccup must never turn a finished job into a failure"), so the property to preserve is that
        // the cases that wrote a subtitle still completed.
        Check("P18: a job that wrote a subtitle completes even with no library monitor and no library manager",
            sjWritten.Count >= 5 && sjCases.Where(c => sjWritten.Contains(c.Label)).All(c => c.Job.Status == SyncJobStatus.Completed),
            $"written={string.Join(",", sjWritten)}");

        try
        {
            Directory.Delete(sjRoot, true);
        }
        catch
        {
            // The cases' own temp directories are removed by the plugin's finally block.
        }
    }
}

/// <summary>A Video whose media sources are whatever the characterization case says they are.</summary>
// @@TYPES@@
internal sealed class JobCheckVideo : MediaBrowser.Controller.Entities.Video
{
    /// <summary>Gets or sets the media sources this fake item reports.</summary>
    public IReadOnlyList<MediaBrowser.Model.Dto.MediaSourceInfo> Sources { get; set; }
        = Array.Empty<MediaBrowser.Model.Dto.MediaSourceInfo>();

    /// <inheritdoc />
    public override IReadOnlyList<MediaBrowser.Model.Dto.MediaSourceInfo> GetMediaSources(bool enablePathSubstitution)
        => Sources;
}

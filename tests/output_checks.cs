// -----------------------------------------------------------------------------------------------------------
// F19 and F21, the two defects the 2026-09-18 read against the code confirmed with a measurement.
//
// Both are unit-level and both used to be proved only by a throwaway probe outside the repository, which is
// why they are here now: a defect with a reproduction and no check is a defect that comes back.
//
//   F19 - a cue whose *stated* duration is exactly 2000 ms was treated as this reader's own no-duration
//         guess (the guess and the statement are the same number), so the renderer pulled its end back to
//         the next cue: a real two-second cue rendered as 00:00:04,999 against a next cue at 00:00:05,000.
//         The guess is a flag now (SrtWriter.Entry.DurationGuessed, MkvSubtitleExtractor's Cue), set where
//         the duration is actually known, and the renderers read the flag instead of the number.
//   F21 - SubSyncMiddleware left context.Response.Body on the MemoryStream it disposes on every path except
//         the HTML success one, so a non-HTML answer to an index path, an already-injected body, or a
//         throwing _next left the response body pointing at a disposed stream. A try/finally hands the
//         original stream back on every exit path now.
// -----------------------------------------------------------------------------------------------------------
{
    // ---------------- F19: the stated duration, through the MP4 path's renderer ----------------
    var f19Stated = SrtWriter.Render(new List<SrtWriter.Entry>
    {
        new(0, 2000, "a stated two seconds"),
        new(5000, 7000, "the next cue"),
    });
    Check("F19: a cue whose stated duration is 2 000 ms keeps it instead of being pulled back to the next cue",
        f19Stated.Contains("00:00:00,000 --> 00:00:02,000", StringComparison.Ordinal)
        && !f19Stated.Contains("00:00:04,999", StringComparison.Ordinal),
        $"rendered='{f19Stated.Split('\n').Skip(1).FirstOrDefault() ?? "(none)"}'");

    // The neighbours of the magic number were never affected, which is what pinned the trigger to the number.
    var f19Neighbour = SrtWriter.Render(new List<SrtWriter.Entry>
    {
        new(0, 1999, "one ms short"),
        new(3000, 3001, "one ms long"),
        new(5000, 7000, "the next cue"),
    });
    Check("F19: the duration either side of 2 000 ms is written as stated (the trigger was the exact number)",
        f19Neighbour.Contains("00:00:00,000 --> 00:00:01,999", StringComparison.Ordinal)
        && f19Neighbour.Contains("00:00:03,000 --> 00:00:03,001", StringComparison.Ordinal),
        $"rendered={string.Join(" | ", f19Neighbour.Split('\n').Where(l => l.Contains("-->", StringComparison.Ordinal)))}");

    // A guess is still a guess: it may not overrun the next cue, which is what the clamp exists for.
    var f19Guessed = SrtWriter.Render(new List<SrtWriter.Entry>
    {
        new(0, 2000, "no duration in the file", DurationGuessed: true),
        new(5000, 7000, "the next cue"),
    });
    Check("F19: a duration that really is this reader's guess is still clamped to the next cue",
        f19Guessed.Contains("00:00:00,000 --> 00:00:04,999", StringComparison.Ordinal),
        $"rendered='{f19Guessed.Split('\n').Skip(1).FirstOrDefault() ?? "(none)"}'");

    // The clamp must survive for a stated duration that genuinely overruns the next cue: overlap is overlap.
    var f19Overrun = SrtWriter.Render(new List<SrtWriter.Entry>
    {
        new(0, 2000, "stated, and it overlaps"),
        new(1500, 3000, "starts before the first ends"),
    });
    Check("F19: a stated duration that overlaps the next cue is still pulled back to it",
        f19Overrun.Contains("00:00:00,000 --> 00:00:01,499", StringComparison.Ordinal),
        $"rendered='{f19Overrun.Split('\n').Skip(1).FirstOrDefault() ?? "(none)"}'");

    // ---------------- F19: the same rule through the Matroska writer ----------------
    // MkvSubtitleExtractor writes its own SRT (its own ToSrt and its own Cue), so the flag has to be read
    // there too - a fix in SrtWriter alone would leave the container the user's library is full of wrong.
    var f19CueType = typeof(MkvSubtitleExtractor).GetNestedType("Cue", BindingFlags.NonPublic);
    var f19ToSrt = typeof(MkvSubtitleExtractor).GetMethod("ToSrt", BindingFlags.NonPublic | BindingFlags.Static);
    string F19Mkv(long startMs, string text, bool guessed, bool withNext)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(f19CueType!))!;
        var ctor = f19CueType!.GetConstructor(new[] { typeof(long), typeof(long), typeof(string), typeof(bool) })!;
        list.Add(ctor.Invoke(new object?[] { startMs, startMs + 2000, text, guessed }));
        if (withNext)
        {
            list.Add(ctor.Invoke(new object?[] { startMs + 5000, startMs + 7000, "the next cue", false }));
        }

        return (string)f19ToSrt!.Invoke(null, new object?[] { list })!;
    }

    Check("F19: the Matroska writer keeps a stated 2 000 ms cue (it has its own copy of the renderer)",
        f19CueType is not null && f19ToSrt is not null
        && F19Mkv(0, "stated", guessed: false, withNext: true).Contains("00:00:00,000 --> 00:00:02,000", StringComparison.Ordinal),
        f19CueType is null || f19ToSrt is null
            ? "the Cue type or ToSrt moved, so this check could not run"
            : $"rendered='{F19Mkv(0, "stated", guessed: false, withNext: true).Split('\n').Skip(1).FirstOrDefault() ?? "(none)"}'");

    Check("F19: the Matroska writer still clamps a guessed end to the next cue",
        f19CueType is not null && f19ToSrt is not null
        && F19Mkv(0, "guessed", guessed: true, withNext: true).Contains("00:00:00,000 --> 00:00:04,999", StringComparison.Ordinal),
        f19CueType is null || f19ToSrt is null
            ? "the Cue type or ToSrt moved, so this check could not run"
            : $"rendered='{F19Mkv(0, "guessed", guessed: true, withNext: true).Split('\n').Skip(1).FirstOrDefault() ?? "(none)"}'");

    // ---------------- F19 again, this time end to end on a real Matroska file ----------------
    // The checks above drive the renderer directly. This one runs the shipped extractor over a file a muxer
    // would write: each cue carries a *stated* 2 000 ms duration (a BlockGroup with a BlockDuration) and the
    // next cue is 5 s later, so a 2 000 ms end that is treated as a guess shows up as 00:00:04,999.
    // tests/run_checks.py generates it (`MKV_FIX_DURATION`); without that variable the check reports skipped.
    var f19File = Environment.GetEnvironmentVariable("MKV_FIX_DURATION");
    if (string.IsNullOrEmpty(f19File) || !File.Exists(f19File))
    {
        Check("F19: a Matroska file that states 2 000 ms renders it (end to end)",
            true, "skipped: MKV_FIX_DURATION is not set (the suite sets it)");
    }
    else
    {
        var f19Ok = MkvSubtitleExtractor.TryExtract(f19File, 0, out var f19Srt, out var f19Why);
        var f19Timings = f19Srt.Split('\n').Where(l => l.Contains("-->", StringComparison.Ordinal)).ToArray();
        Check("F19: a Matroska file that states 2 000 ms renders it (end to end, real file)",
            f19Ok
            && f19Timings.Length == 2
            && f19Timings[0] == "00:00:00,000 --> 00:00:02,000"
            && f19Timings[1] == "00:00:05,000 --> 00:00:07,000",
            f19Ok
                ? $"cues={f19Timings.Length} [{string.Join(" | ", f19Timings)}]"
                : $"extraction failed: {f19Why}");
    }

    // ---------------- F21: the response body comes back on every path ----------------
    async Task<(bool Restored, string Note, string Content)> F21Probe(Microsoft.AspNetCore.Http.RequestDelegate next, bool expectThrow)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Path = "/web/index.html";
        var capture = new MemoryStream();
        context.Response.Body = capture;
        var middleware = new SubSyncMiddleware(next, Microsoft.Extensions.Logging.Abstractions.NullLogger<SubSyncMiddleware>.Instance);

        var note = string.Empty;
        if (expectThrow)
        {
            try
            {
                await middleware.InvokeAsync(context).ConfigureAwait(false);
                note = "the middleware swallowed the exception";
            }
            catch (InvalidOperationException)
            {
                note = "the exception reached the caller";
            }
        }
        else
        {
            await middleware.InvokeAsync(context).ConfigureAwait(false);
        }

        var restored = ReferenceEquals(context.Response.Body, capture);
        try
        {
            await context.Response.Body.WriteAsync(new byte[] { 0x78 }).ConfigureAwait(false);
            note = note.Length > 0 ? note + ", a later write succeeded" : "a later write succeeded";
        }
        catch (Exception ex)
        {
            note = note.Length > 0 ? note + $", a later write threw {ex.GetType().Name}" : $"a later write threw {ex.GetType().Name}";
            restored = false;
        }

        return (restored, $"{note}, captured {capture.Length} byte(s)", System.Text.Encoding.UTF8.GetString(capture.ToArray()));
    }

    var f21Json = await F21Probe(
        async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{}")).ConfigureAwait(false);
        },
        expectThrow: false);
    Check("F21: a non-HTML answer to an index path hands the response body back", f21Json.Restored, f21Json.Note);

    var f21Bare = await F21Probe(_ => Task.CompletedTask, expectThrow: false);
    Check("F21: a response with no content type at all hands the response body back", f21Bare.Restored, f21Bare.Note);

    var f21Injected = await F21Probe(
        async context =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(
                "<html><body><!-- SubSync Client Script -->\n<script src=\"/SubSync/ClientScript\"></script>\n"
                + "<!-- End SubSync Client Script --></body></html>")).ConfigureAwait(false);
        },
        expectThrow: false);
    Check("F21: an already-injected body hands the response body back", f21Injected.Restored, f21Injected.Note);

    var f21Html = await F21Probe(
        async context =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("<html><body>page</body></html>")).ConfigureAwait(false);
        },
        expectThrow: false);
    Check("F21: an HTML page hands the response body back and still carries the injected script",
        f21Html.Restored && f21Html.Content.Contains("/SubSync/ClientScript", StringComparison.Ordinal),
        f21Html.Note);

    var f21Threw = await F21Probe(_ => throw new InvalidOperationException("from the next middleware"), expectThrow: true);
    Check("F21: a throwing next middleware hands the response body back before the exception escapes",
        f21Threw.Restored && f21Threw.Note.Contains("reached the caller", StringComparison.Ordinal), f21Threw.Note);

    // An already-injected page must pass through untouched: injecting twice would double the script.
    Check("F21: an already-injected page is passed through without a second copy of the script",
        f21Injected.Restored
        && f21Injected.Content.Split("<!-- SubSync Client Script -->").Length == 2
        && f21Injected.Content.Split("/SubSync/ClientScript").Length == 2,
        $"injection block(s)={f21Injected.Content.Split("<!-- SubSync Client Script -->").Length - 1} "
        + $"script tag(s)={f21Injected.Content.Split("/SubSync/ClientScript").Length - 1}");
}

// @@TYPES@@

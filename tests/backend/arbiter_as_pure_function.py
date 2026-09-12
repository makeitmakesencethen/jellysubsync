#!/usr/bin/env python3
"""Make the rescale arbiter a pure function that the C# harness can check, and fix the stale check.

The arbiter is what stops a mis-timed *reference* from dragging every correct subtitle of a file onto it, so it
belongs in the unit-level harness rather than only in a fixture: a fixture cannot easily make the plugin's own
reference PAL-timed (that file is written per run and removed with the run), while the decision itself is pure
arithmetic over two spans and the video's duration.
"""
import pathlib

svc = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = svc.read_text()

# 1. the decision, extracted
old = """        // Which side is off the video's timeline? A subtitle spans the film it was timed for, so a span a few
        // percent away from the file's duration is a subtitle timed for a different playback speed. If the
        // *reference* is that one, rescaling the target onto it would drag a correct subtitle onto a mis-timed
        // ruler - and in bulk that would happen to every subtitle of the file, because they share the reference.
        if (videoDuration > TimeSpan.FromSeconds(60))
        {
            var videoSeconds = videoDuration.TotalSeconds;
            var targetVsVideo = targetSpan / videoSeconds;
            var referenceVsVideo = referenceSpan / videoSeconds;
            var targetOnVideo = Math.Abs(targetVsVideo - 1.0) <= 0.03;
            var referenceOnVideo = Math.Abs(referenceVsVideo - 1.0) <= 0.03;
            if (referenceOnVideo && !targetOnVideo)
            {
                // The ordinary case: the subtitle is the one that is off.
            }
            else if (targetOnVideo && !referenceOnVideo)
            {"""
new = """        // Which side is off the video's timeline? A subtitle spans the film it was timed for, so a span a few
        // percent away from the file's duration is a subtitle timed for a different playback speed. If the
        // *reference* is that one, rescaling the target onto it would drag a correct subtitle onto a mis-timed
        // ruler - and in bulk that would happen to every subtitle of the file, because they share the reference.
        if (videoDuration > TimeSpan.FromSeconds(60))
        {
            var videoSeconds = videoDuration.TotalSeconds;
            if (IsTargetOffTheVideo(targetSpan, referenceSpan, videoSeconds))
            {
                // The ordinary case: the subtitle is the one that is off.
            }
            else if (Math.Abs(targetSpan / videoSeconds - 1.0) <= 0.03)
            {"""
assert old in t
t = t.replace(old, new, 1)

# 2. the function itself, next to the other measurement helpers
anchor = """    /// <summary>
    /// Reads one SRT timestamp ("00:01:02,345") as milliseconds.
    /// </summary>"""
helper = '''    /// <summary>
    /// Whether the subtitle being synced is the one that sits off the file's own timeline.
    /// </summary>
    /// <remarks>
    /// Both a subtitle timed for another playback speed and one from a longer cut show up as a span that is a few
    /// percent away from the reference's, so the references alone cannot say which of the two is the odd one out.
    /// The file's duration can: a subtitle spans the film it was timed for, so the side that disagrees with the
    /// duration is the one to correct. Getting this backwards would rescale a correct subtitle onto a mis-timed
    /// ruler, and every subtitle of the file shares that ruler in a bulk run.
    /// </remarks>
    /// <param name="targetSpan">Span of the subtitle being synced, in seconds.</param>
    /// <param name="referenceSpan">Span of the reference, in seconds.</param>
    /// <param name="videoSeconds">The file's duration, in seconds.</param>
    /// <returns>True when the subtitle is the side to rescale; false when the reference is, or when both or neither are.</returns>
    internal static bool IsTargetOffTheVideo(double targetSpan, double referenceSpan, double videoSeconds)
    {
        if (videoSeconds <= 60)
        {
            return true;
        }

        var targetOnVideo = Math.Abs((targetSpan / videoSeconds) - 1.0) <= 0.03;
        var referenceOnVideo = Math.Abs((referenceSpan / videoSeconds) - 1.0) <= 0.03;
        if (!referenceOnVideo && !targetOnVideo)
        {
            // Neither span matches the file: not a case this rule can settle, so the pair rule and the alignment
            // guards below decide, as before.
            return true;
        }

        return !targetOnVideo;
    }

''' + anchor
assert anchor in t
t = t.replace(anchor, helper, 1)
svc.write_text(t)
print('the arbiter is an internal static function now')

# 3. the stale check: the signature became multi-line
checks = pathlib.Path('tests/run_checks.py')
c = checks.read_text()
old = """           'private string? RescaleOntoReferenceSpan(string targetPath, string referencePath, string tempDir, SyncJob job)' in service_source"""
new = """           'private string? RescaleOntoReferenceSpan(' in service_source
           and 'TimeSpan videoDuration,' in service_source"""
assert old in c
checks.write_text(c.replace(old, new, 1))
print('check updated for the new signature')

#!/usr/bin/env python3
"""Anchor the rescale to the video's own timeline, not to whatever the reference happens to be.

The rescale compares the subtitle's span with the reference's and stretches the subtitle onto the reference. That
is right when the subtitle is the odd one out, and wrong when the *reference* is: an embedded track taken from a
PAL release on a 23.976 fps file is 4 % short, and stretching a correct subtitle onto it drags it off the video.
The old behaviour refused such a file on the reference ceiling instead; after the rescale the shift is small, so
that ceiling no longer sees it. The video's own duration settles which of the two is off, so it now decides:

  subtitle off the video (the PAL case)     -> rescale onto the reference, which is on the video's timeline;
  reference off the video (this case)       -> rescale nothing, say so, and let the alignment and its ceiling
                                               handle a reference that does not match the file;
  duration unusable or both on the timeline -> the pair rule decides, as before.

This matters in bulk: every subtitle of a file is aligned against the same reference, so a mis-timed reference did
not stay in its own job.
"""
import pathlib

p = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = p.read_text()

# 1. the helper takes the video's duration and uses it as the arbiter
old = '''    /// <param name="targetPath">The subtitle being synced.</param>
    /// <param name="referencePath">The reference subtitle.</param>
    /// <param name="tempDir">Directory to write the copy into.</param>
    /// <param name="job">The job, for the log.</param>
    /// <returns>The path of the rescaled copy, or null when nothing should change.</returns>
    private string? RescaleOntoReferenceSpan(string targetPath, string referencePath, string tempDir, SyncJob job)'''
new = '''    /// <param name="targetPath">The subtitle being synced.</param>
    /// <param name="referencePath">The reference subtitle.</param>
    /// <param name="videoDuration">The file's duration, which settles which side is off the video's timeline.</param>
    /// <param name="tempDir">Directory to write the copy into.</param>
    /// <param name="job">The job, for the log.</param>
    /// <returns>The path of the rescaled copy, or null when nothing should change.</returns>
    private string? RescaleOntoReferenceSpan(
        string targetPath,
        string referencePath,
        TimeSpan videoDuration,
        string tempDir,
        SyncJob job)'''
assert old in t
t = t.replace(old, new, 1)

# 2. the arbiter itself, placed after the span ratio has been worked out
old = '''        var targetSpan = target[^1] - target[0];
        var referenceSpan = reference[^1] - reference[0];
        if (targetSpan <= 60 || referenceSpan <= 60)
        {
            return null;
        }

        var scale = referenceSpan / targetSpan;'''
new = '''        var targetSpan = target[^1] - target[0];
        var referenceSpan = reference[^1] - reference[0];
        if (targetSpan <= 60 || referenceSpan <= 60)
        {
            return null;
        }

        // Which side is off the video's timeline? A subtitle spans the film it was timed for, so a span a few
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
            {
                PluginLog.Info(
                    $"[{job.Id}] framerate: the reference spans {referenceSpan:0.0} s of the file's {videoSeconds:0.0} s "
                    + $"(the subtitle's own span, {targetSpan:0.0} s, matches the file), so the reference is the odd one "
                    + "out \\u2014 no rescale; a shift from a reference this far off the video is refused as before");
                return null;
            }
        }

        var scale = referenceSpan / targetSpan;'''
assert old in t
t = t.replace(old, new, 1)

# 3. the call site passes the duration
old = "                engineInput = RescaleOntoReferenceSpan(subtitleInputPath, referenceArg, tempDir, job) ?? engineInput;"
new = "                engineInput = RescaleOntoReferenceSpan(subtitleInputPath, referenceArg, videoDuration, tempDir, job) ?? engineInput;"
assert old in t
t = t.replace(old, new, 1)

p.write_text(t)
print('the rescale is anchored to the video duration')

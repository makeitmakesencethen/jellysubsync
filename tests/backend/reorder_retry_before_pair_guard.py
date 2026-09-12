#!/usr/bin/env python3
"""Order matters: the wide retry has to run before the "unrequested rescale" guard, and must not rescale itself.

The 120 s fixture found this: with the subtitle 120 s out, the first alignment (allowed only 60 s) came back with a
0.9701x scale - the engine compensating for a shift it was not allowed to apply - and the pair guard refused it as an
unrequested rescale *before* the offset ceiling could trigger the retry. So:

  1. the offset-ceiling check now runs before the pair guard, because a result at or past the ceiling is exactly the
     case the retry exists for;
  2. the retry itself is built with framerate correction forced off (`--no-fix-framerate --skip-infer-framerate-ratio`),
     so it can only move the subtitle, which is what the wide allowance is for - and its result is still examined by
     the pair guard and the tight audio check afterwards.
"""
import pathlib

svc = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = svc.read_text()

pair_anchor = """            // Nothing destructive is ever written: a measured rescale that was not asked for (or that
            // is not a real framerate pair) means the engine moved the timeline, and the source
            // subtitle stays untouched while the job says exactly why."""
ceiling_start = """            // The engine clamps the shift at the configured ceiling, so a result sitting at or past it is the most it
            // was allowed to apply, not what the file needed: writing that would present a guess as a synced subtitle."""
assert pair_anchor in t and ceiling_start in t, 'anchors missing'

pair_at = t.index(pair_anchor)
ceiling_at = t.index(ceiling_start)
assert ceiling_at > pair_at, 'the ceiling block is already before the pair guard'

# the ceiling block ends where the next block begins (the reference note, or the notes section)
end_marker = """            if (usedSubtitleReference
                && measured is { } fromReferenceNote"""
end_at = t.index(end_marker, ceiling_at)
ceiling_block = t[ceiling_at:end_at]

# remove it, then insert it directly before the pair guard
t = t[:ceiling_at] + t[end_at:]
pair_at = t.index(pair_anchor)
t = t[:pair_at] + ceiling_block + t[pair_at:]

# the retry must not rescale: force the flags off in its args
old = """                var wideArgs = BuildFfSubSyncArgs(config, referenceArg, engineInput, wideOutput, tempDir, serializeSpeech, referenceStream);
                wideArgs[wideArgs.IndexOf("--max-offset-seconds") + 1] = wideSeconds.ToString(CultureInfo.InvariantCulture);"""
new = """                var wideArgs = BuildFfSubSyncArgs(config, referenceArg, engineInput, wideOutput, tempDir, serializeSpeech, referenceStream);
                wideArgs[wideArgs.IndexOf("--max-offset-seconds") + 1] = wideSeconds.ToString(CultureInfo.InvariantCulture);
                // A wide allowance is for moving the subtitle, not for rescaling it: an engine that compensates for a
                // clamped shift with a scale is how the first pass went wrong. The pair guard and the tight audio
                // check below still see this result.
                foreach (var flag in FramerateArgs(false, false))
                {
                    if (!wideArgs.Contains(flag))
                    {
                        wideArgs.Add(flag);
                    }
                }

                wideArgs.Remove("--gss");"""
assert old in t
t = t.replace(old, new, 1)

svc.write_text(t)
print('the ceiling retry now runs before the pair guard, and the retry cannot rescale')

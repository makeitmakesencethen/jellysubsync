#!/usr/bin/env python3
"""Two fixes the user's successful runs exposed (corrected anchors).

1. Order: Paradise's log shows the wide retry running before the bad-ruler fallback, so the retry aligned against a
   ruler already known to be from a different cut (nonsense 231400 ms result, then thrown away). The subtitle
   reference ceiling has to be checked first.
2. Diagnostics: Clara Sola's swe subtitle refused with "retry (exit 1) produced nothing" and nothing about why. The
   engine's last lines are kept and reported.
"""
import pathlib

svc = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = svc.read_text()

REF_START = "            // A shift that came from a subtitle reference is only ever as good as that track: a"
REF_END = "            if (usedSubtitleReference\n                && measured is { } fromReferenceNote"
CEILING = "            // The engine clamps the shift at the configured ceiling"

assert REF_START in t and REF_END in t and CEILING in t, 'anchors missing'
start = t.index(REF_START)
end = t.index(REF_END)
ref_block = t[start:end]
assert 'referenceCeilingMs' in ref_block, 'that is not the reference block'

t = t[:start] + t[end:]
ceiling_at = t.index(CEILING)
t = t[:ceiling_at] + ref_block + t[ceiling_at:]
print('reference check moved above the offset ceiling (%d lines)' % ref_block.count("\n"))

# the retry keeps the engine's last words
old = """                var wideExit = await RunProcessWithStderrCallbackAsync(
                    ffsubsyncExe, wideArgs, tempDir, null, cancellationToken).ConfigureAwait(false);"""
new = """                var retryErrors = new List<string>();
                var wideExit = await RunProcessWithStderrCallbackAsync(
                    ffsubsyncExe,
                    wideArgs,
                    tempDir,
                    line =>
                    {
                        lock (retryErrors)
                        {
                            retryErrors.Add(line);
                            if (retryErrors.Count > 8)
                            {
                                retryErrors.RemoveAt(0);
                            }
                        }
                    },
                    cancellationToken).ConfigureAwait(false);"""
assert old in t, 'retry run call not found'
t = t.replace(old, new, 1)

old = """                    var detail = $"the measured offset {onCeiling.ShiftMs} ms is at or past the configured ceiling "
                        + $"({config.MaxOffsetSeconds} s), and the retry with a {wideSeconds} s allowance (exit "
                        + $"{wideExit}) produced nothing";"""
new = """                    // The engine's own last lines name a failed retry; the exit code alone does not.
                    string engineTail;
                    lock (retryErrors)
                    {
                        engineTail = retryErrors.Count == 0
                            ? string.Empty
                            : " · engine said: " + string.Join(" | ", retryErrors).Trim();
                    }

                    var detail = $"the measured offset {onCeiling.ShiftMs} ms is at or past the configured ceiling "
                        + $"({config.MaxOffsetSeconds} s), and the retry with a {wideSeconds} s allowance (exit "
                        + $"{wideExit}) produced nothing{engineTail}";"""
assert old in t, 'refusal message not found'
t = t.replace(old, new, 1)

svc.write_text(t)
print('a failed retry now reports what the engine said')

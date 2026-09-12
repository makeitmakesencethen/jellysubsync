#!/usr/bin/env python3
"""2.0.20, part 2: the setting's default, its description, and the documented rule it changes."""
import pathlib

# 1. the default: 180 s, because the value is a search window and 60 s is too small for real libraries
cfg = pathlib.Path('Jellyfin.Plugin.SubSync/Configuration/PluginConfiguration.cs')
t = cfg.read_text()
old_default = 'public int MaxOffsetSeconds { get; set; } = 60;'
assert old_default in t, 'MaxOffsetSeconds default not found'
t = t.replace(old_default, 'public int MaxOffsetSeconds { get; set; } = 180;', 1)

old_doc = """    /// Maximum allowed offset in seconds for any subtitle segment."""
if old_doc in t:
    t = t.replace(old_doc, """    /// How far the alignment may look for a subtitle's position, in seconds.""", 1)
cfg.write_text(t)
print('default window: 180 s (was 60)')

# 2. the page says what the field is
page = pathlib.Path('Jellyfin.Plugin.SubSync/Web/subsyncMain.html')
h = page.read_text()
old_desc = '<div class="fieldDescription">Maximum allowed offset in seconds for any subtitle segment. Default: 60. A result clamped to this ceiling is refused rather than written.</div>'
new_desc = ('<div class="fieldDescription">How far from the video\'s audio the alignment may look for a subtitle\'s '
            'position. 180 by default: a subtitle can genuinely be minutes out, and an answer outside this window '
            'cannot be found. Too large invites matching the wrong place. A result that lands on the window is '
            'retried with twice it, and only written when the film\'s audio agrees.</div>')
if old_desc in h:
    h = h.replace(old_desc, new_desc, 1)
    page.write_text(h)
    print('page description updated')
else:
    print('page description not found verbatim; left as is')

# 3. AGENTS.md documents a rule this release changes
agents = pathlib.Path('AGENTS.md')
a = agents.read_text()
old_rule = """- **Never write a result that is pinned to `MaxOffsetSeconds`,** and never write a big shift that came
  from a subtitle reference (`MaxSubtitleReferenceOffsetSeconds`, default 30 s). Both are refused with
  the measured numbers: a clamped or reference-inherited offset is a wrong file that looks like a
  success. This is the one place the plugin deliberately does not finish a job — settled 2026-09-11
  against the older "never refuse a job" wording, which applies to a reference that cannot be *built*
  (that job still runs, against the audio), not to one that is provably from another cut."""
new_rule = """- **`MaxOffsetSeconds` is ffsubsync's search window, not a trust limit** (180 s by default as of 2.0.20;
  it mirrors `--max-offset-seconds`, whose own default is 60 s). An answer outside the window cannot be
  found at all, which is how a subtitle needing ~112 s came back as 56 s: the engine returns its best
  *wrong* answer. So a result that lands on the window is retried once with twice the window, and that
  result is written only when it is **not** pinned to the wider window either and one alignment against
  the film's audio asks for nothing more. If the wider window is also pinned, the job refuses and tells
  the user to raise the setting. Do not "fix" a big offset by applying the measured displacement as a
  shift: when the engine also re-times the subtitle, that number is the median displacement of a
  rescaled timeline, and applying it as a shift is wrong by construction (tried in 2.0.19, wrong on the
  user's file).
- **Never write a big shift that came from a subtitle reference** (`MaxSubtitleReferenceOffsetSeconds`,
  default 30 s): past it that track is not the same cut, so it is discarded as a ruler and the subtitle
  is aligned against the audio instead (2.0.17+). A reference that cannot be *built* is not fatal
  either — that job still runs, against the audio."""
assert old_rule in a, 'the documented rule was not found verbatim'
agents.write_text(a.replace(old_rule, new_rule, 1))
print('AGENTS.md updated: the window is a search range, and the rigid-shift idea is recorded as tried and wrong')

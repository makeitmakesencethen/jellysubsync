# Credits & Third-Party Notices

SubSync for Jellyfin is a standalone project, but it stands on work done by others.
Nothing here would exist without these projects — thank you.

## Origin

**camalolo/jellysubsync** — https://github.com/camalolo/jellysubsync — GPL-2.0

This project began as a fork of camalolo's Jellyfin subtitle-sync plugin and still
derives from it: the plugin skeleton, the detail-page menu injection approach and the
subtitles-via-media-streams concept come from his work. Everything else (bundled engine
handling, the server-side FIFO batch queue, the dashboard UI, copy/replace output modes,
embedded-track extraction, the library sweep and the reporting) was rebuilt on top of it.
The original project remains licensed under GPL-2.0, which this project inherits.

## Design & features ported

**Marnalas/jellyfin-subsync** — https://github.com/Marnalas/jellyfin-subsync — MIT

The library-sweep concept, the persistent content-hash skip cache and the
consecutive-failure cache (`SubSyncSweepTask`, `SweepState`) were ported from this
project's design. Its architecture differs (it runs a separate sidecar service); here
the ideas were reimplemented on this plugin's in-process engine. Licensed MIT —
attribution kept in the source files that implement it.

## Bundled components

**ffsubsync** — https://github.com/smacke/ffsubsync — MIT
The subtitle/audio alignment engine. A self-contained linux-x64 build is shipped inside
each release zip and extracted on first start.

**FFmpeg** — https://ffmpeg.org — LGPL/GPL
Used for audio analysis and subtitle extraction. Not bundled: the plugin uses the FFmpeg
that ships with Jellyfin.

**Jellyfin** — https://jellyfin.org — GPL-2.0
The media server this plugin targets.

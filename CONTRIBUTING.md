# Development & release workflow

This project is maintained by one person with an AI assistant, so the workflow is built
around two rules: **`master` is always what users install**, and **users never see
half-finished work**.

## Branches

- `master` — the release branch. Every commit on it is something a user could be running.
- Feature/fix branches — all work happens here, one branch per change
  (`fix/embedded-stream-index`, `feature/language-filter`, …).
- Merges into `master` are squashed so the public history reads as meaningful changes
  rather than iterations.

**Never rewrite published history.** Once a tag has been released, do not delete, move or
force-push it — someone may have installed it, and the plugin catalog references it. If a
release is wrong, publish a fix forward.

## Working on a change

1. Branch off `master`.
2. Implement; verify before hand-off:
   - `dotnet build Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj -c Release`
     (warnings are errors)
   - syntax-check changed JavaScript (embedded `Web/*.html` scripts and `subsync.js`)
   - test the real behaviour on a live Jellyfin server before publishing
3. Update `AGENTS.md` and `knowledge/*.md` in the same commit if the change affects
   documented behaviour — docs are part of the work, not a follow-up.
4. Squash-merge into `master`.

## Testing without publishing to everyone

Two levels, use both:

**1. Local loop (only you, fastest).** Build the plugin and drop it into your own
Jellyfin, without any GitHub round-trip:

```bash
dotnet build Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj -c Release
# docker: copy the DLL over the installed plugin and restart Jellyfin
docker cp Jellyfin.Plugin.SubSync/bin/Release/net9.0/Jellyfin.Plugin.SubSync.dll \
  jellyfin:/config/plugins/SubSync_<installed-version>/Jellyfin.Plugin.SubSync.dll
docker restart jellyfin
```

Plugin assemblies only load at server startup, so **a restart is always required**.
For code paths that don't touch the DLL (the embedded `Web/*.html` and `subsync.js`),
the files are embedded resources — rebuild + reinstall is still needed.

**2. Beta channel (safe public testing).** The `beta` branch is built by
`.github/workflows/beta.yml` into a *separate* catalog:

```
https://<owner>.github.io/<repo>/beta/manifest.json
```

Stable users are untouched — the beta catalog is a different URL that must be added
explicitly (Dashboard → Plugins → Catalog → add repository, then install the entry named
**SubSync (Beta)**). Anyone who wants to help test can add it; everyone else keeps
getting `master` releases.

Promotion flow: work on `beta` → test via the beta catalog (or locally) → merge `beta`
into `master` → bump version + `CHANGELOG.md` entry → tag `vX.Y.Z` → stable release.
Because the beta catalog is a different URL, a broken beta can never reach stable users.

## Releasing

Releases are cut when there is a meaningful set of changes — not per commit, and not
per fix. Weekly-ish, or when a fix matters to users.

1. Make sure `master` builds and the change was tested on a real server.
2. Add a `CHANGELOG.md` entry (user-facing wording, what changed and why it matters).
3. Bump the version in `Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj`,
   `Jellyfin.Plugin.SubSync/meta.json` and `build.yaml` (the release workflow refuses
   mismatched tags).
4. Commit as `Version X.Y.Z`, tag `vX.Y.Z`, push `master` **and** the tag.
5. The release workflow builds the bundled ffsubsync binary, packages the zip, writes
   the catalog manifest and deploys `gh-pages` (manifest + zip + logo). Confirm the
   published manifest reports the new version before announcing anything.
6. Announce in the support thread only for notable releases; skip routine fixes.

## Versioning

- `PATCH` — bug fixes, internal changes, wording.
- `MINOR` — new user-facing capability.
- `MAJOR` — breaking changes (config incompatibility, dropped Jellyfin support).

## Compatibility

- Current target: Jellyfin **10.11** (`targetAbi 10.11.0.0`).
- A future Jellyfin major needs its own build/branch; don't mix ABIs in one release.

## Principles that keep this project safe to run

- Video files are **never** written — the engine only reads them for audio analysis.
- Copy mode is the default: syncs write a new `-SYNCED.srt` and leave the original alone.
  Replace mode always backs up first and rolls back on failure.
- Anything that touches the user's files must fail loudly, not silently.

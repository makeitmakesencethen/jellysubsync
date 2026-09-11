# Migrating this plugin to a new Jellyfin server major (10.11 → 12.0)

Written while moving SubSync from Jellyfin 10.11 to 12.0, and verified against a real
12.0.0 server (portable build), not just against the compiler.

## What actually breaks

Jellyfin 12.0 targets **.NET 10** and rebuilds the library database, so a plugin carries
three separate compatibility risks. Only the first is visible at build time.

1. **Target framework and package versions.** The server no longer runs a .NET 9 runtime.
   A `net9.0` plugin references `MediaBrowser.Controller, Version=10.11.0.0` while the
   server has `12.0.0.0`, and Jellyfin reports it as `NotSupported` after
   `assembly.GetTypes()` throws — the log line is *"references an incompatible version of
   one of the shared libraries"*, which names the symptom, not the cause.
2. **Removed/changed interfaces.** Listed in the release notes; the compiler finds these.
3. **The HTTP authorization change**, which no compiler sees. **This is the one that
   silently breaks a working plugin.**

## The authorization break (12.0)

12.0 disables the legacy authorization mechanisms by default
(`ServerConfiguration.EnableLegacyAuthorization = false`, forced by a migration on
existing installs). In `Jellyfin.Server.Implementations/Security/AuthorizationContext.cs`
the token is read from:

| source | 10.11 | 12.0 |
|---|---|---|
| `Authorization: MediaBrowser Token="…"` header | yes | yes |
| `?ApiKey=<token>` query parameter | yes | yes |
| `X-Emby-Token` header | yes | **only when legacy auth is enabled** |
| `X-MediaBrowser-Token` header | yes | **only when legacy auth is enabled** |
| `X-Emby-Authorization` header, `Emby` scheme | yes | **only when legacy auth is enabled** |
| `?api_key=` query parameter | yes | **only when legacy auth is enabled** |

Measured on a live 12.0.0 server with a plugin whose endpoints are `[Authorize]`:

```
SubSync/InstallationStatus  + X-Emby-Token        -> 401
SubSync/InstallationStatus  + Authorization: ...  -> 200
SubSync/Log?api_key=<token>                       -> 401
SubSync/Log?ApiKey=<token>                        -> 200
```

Because 10.11 already accepts `Authorization` and `?ApiKey=`, **switch to the modern forms
outright** — one code path works on both generations. Sending `X-Emby-Token` as well is
pointless: 12.0 ignores it and 10.11 does not need it.

The header format jellyfin-web sends (`getAuthorizationHeader` in `@jellyfin/sdk`) is:

```
Authorization: MediaBrowser Client="…", Device="…", DeviceId="…", Version="…", Token="…"
```

Only `Token=` is required by the server's parser; the rest fill in blanks.

## Recipe

1. `Directory.Build.props` / `.csproj`: `net10.0`.
2. `PackageReference` `Jellyfin.Controller` / `Jellyfin.Model` → the matching server
   version (`12.0.0`), still with `<ExcludeAssets>runtime</ExcludeAssets>`.
3. `meta.json` **and** `build.yaml`: `targetAbi: "12.0.0.0"`, `framework: "net10.0"`,
   and the version. `meta.json` ships inside the zip and the server reads `targetAbi`
   back out of it on load (`PluginManager.LoadManifest`), so a stale value there is a
   real defect, not cosmetics.
4. Catalog manifest: `targetAbi` per version entry. Jellyfin filters versions by
   `targetAbi <= appVersion` (`InstallationManager.GetAvailablePluginVersions`) and
   removes the whole package when nothing is left, so a 12-only entry is invisible to a
   10.11 server rather than offered to it.
5. Bump the **major** version: dropping a server generation is a breaking change, and the
   version must be higher than the last build the older servers already have.
6. CI: `dotnet-version: '10.0.x'`, and the artifact path becomes
   `bin/Release/net10.0/`.
7. The regression harness (`tests/run_checks.py`) compiles its own project against the
   plugin, so its `TargetFramework` and Jellyfin package versions have to move too.

## Compile errors you will actually hit

Only one on this codebase, and it is a new .NET 10 analyzer rather than a Jellyfin API:

- **CA2024** — `Do not use 'reader.EndOfStream' in an async method`. Replace the
  `while (!reader.EndOfStream)` loop with `while (true)` + `if (line is null) break;`
  after `await reader.ReadLineAsync(ct)`.

Interfaces SubSync did *not* use, which are removed or changed in 12.0 and will hit other
plugins: `ISubtitleWriter` family and `SubtitleOptions`/`SubtitleConfigurationFactory`
(removed with the global subtitle configuration), `ISearchEngine` → `ISearchManager`,
`IAuthenticationProvider.HasPassword` (removed), `IUserManager.Users`/`UsersIds`
(properties → methods), parts of `IItemRepository` (moved to `IItemPersistenceService`,
`IItemCountService` and `INextUpService`),
`IDirectoryService.GetFilePaths` (no `sort` argument), the `IPathManager` subtitle and
attachment path getters (now nullable), `Jellyfin.Extensions.AlphanumericComparator`
(removed), `DtoExtensions.AddClientFields` (removed). Also: alternate versions and
playlist children are no longer serialized inside the parent item.

## Verifying the migration locally

A plugin that compiles is not a plugin that loads. Two cheap checks, in order of strength:

1. **Load the built DLL against the server's own assemblies** and call `GetTypes()` — the
   exact probe `PluginManager` uses to decide `NotSupported`. `AssemblyLoadContext
   .Default.Resolving` can point at a Jellyfin install's DLL folder. Also worth asserting:
   the assembly version string, the embedded-resource names (a renamed resource = a blank
   config page), and that each `PluginPageInfo.EmbeddedResourcePath` resolves.
2. **Run the real server.** The portable tarball
   (`repo.jellyfin.org/files/server/linux/latest-stable/amd64/jellyfin_<ver>-amd64.tar.gz`)
   is self-contained and needs no root: unpack, point `--datadir`, drop the DLL +
   `meta.json` into **`<datadir>/plugins/<Name>_<version>/`** (note: `plugins` lives under
   `--datadir`, *not* `--configdir`), start it, and read the log for
   `Loaded plugin: "SubSync" "2.0.0.0"`.
   Complete the wizard over the API (`POST /Startup/Configuration`, `GET /Startup/User`,
   `POST /Startup/User` with a non-empty password, `POST /Startup/RemoteAccess`,
   `POST /Startup/Complete`), authenticate with `POST /Users/AuthenticateByName` using the
   `MediaBrowser` header, then exercise the plugin's endpoints with **both** header styles.
   A self-contained build still needs libicu on the host; without root it can be fetched
   with `apt-get download libicuNN && dpkg-deb -x … /somewhere` and used via
   `LD_LIBRARY_PATH` (until then, `dotnet` only runs with
   `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`).

The web-interface side is easier than it looks: 12.0's Modern layout still renders
`data-action="menu"` buttons on cards and list rows, still opens `itemContextMenu.show()`
through the same `actionSheet` component (`.actionSheetContent`, `.actionSheetScroller`),
still selects items by `[data-id]`, still exposes `window.ApiClient`
(including `accessToken()`, `getUrl()`, `getPluginConfiguration()`), and still embeds
plugin pages through `configurationpage?name=<Name>` with a `div[data-role="page"]` root
carrying `class="page type-interior pluginConfigurationPage"`. Plugin pages with
`EnableInMainMenu` still appear in the dashboard drawer
(`PluginDrawerSection` → `useConfigurationPages({ enableInMainMenu: true })`).

One thing that *does* change in the web client: pages can no longer rely on helpers that
live inside the injected client script's IIFE. `subsyncMain.html` called `token()` and
`apiUrl()` that were only defined in `subsync.js`, so the log-link line threw a
`ReferenceError` and the rest of that render callback never ran. Give every page its own
copies of the helpers it calls.

## New server features worth adopting (12.0)

- `LibraryOptions.SubtitleDownloadLanguages` and `UserConfiguration.SubtitleLanguagePreference`
  — the server now has a per-library/per-user answer to "which subtitle languages does this
  person want", which is the natural default for a language picker.
- `ILocalizationManager.GetServerLocalizedString` / `GetLanguageDisplayName` for
  plugin-side strings.
- `IMediaSegmentProvider.CleanupExtractedData` — plugins are now told when an item's data
  is pruned, so extracted artefacts can be removed instead of accumulating.
- Batch APIs: `IUserDataManager.GetResumeUserDataBatch`, `ILibraryManager.GetPeopleByItems`,
  `IItemCountService`, `ILibraryManager.GetNextUpEpisodesBatch`.
- `ILibraryManager.ResolveAlternateVersion` / `GetLinkedAlternateVersions` /
  `UpsertLinkedChild` replace manipulating version links through serialized item data.

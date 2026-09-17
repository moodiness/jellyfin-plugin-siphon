# Siphon

Siphon brings **Stremio addons into Jellyfin**: catalogs, metadata, subtitles, and selectable stream versions through a server-side proxy and native Jellyfin libraries.

Siphon targets **Jellyfin 12.1** and .NET 10. It does not include a torrent engine, debrid client, or external-player integration.

The per-catalog libraries, incremental metadata, followed-series refresh, native stream versions, redesigned settings, and audit fixes described below target **1.4.0.0 (unreleased)**. They are not included in the published v1.3.1 archive.

## Features

- Create one native Jellyfin library and home row for each selected addon catalog, separating movie and series catalogs.
- Share canonical movies, series, seasons, and episodes between catalogs without duplicate home cards or JavaScript injection.
- Preserve IMDb, TMDB, TVDB, MyAnimeList, and addon-specific identities, along with existing Jellyfin favorites, watched state, and resume positions.
- Map plots, dates, runtimes, ratings, genres, credits with portraits, season posters, and episode-specific artwork into native metadata fields.
- Reuse fresh provider responses across restarts and avoid rewriting unchanged native metadata or credits.
- Refresh followed series directly, without browsing every catalog, while retaining episodes omitted by a partial response.
- Offer each supported stream as a native movie or episode **Version**, preserving the order returned by the addon.
- Discover addons advertised through Stremio `addon_catalog` resources.
- Proxy HTTP(S) progressive media and HLS server-side without exposing upstream URLs or addon headers to players.
- Manage addons and collapsible catalog selections in separate native settings tabs, alongside diagnostics, protected cleanup, and opt-in metadata providers.
- Enable the Cinemata addon by default as a removable example configuration. It has no catalog subscriptions selected by default.

## Requirements

- Jellyfin Server **12.1.x**.
- A server runtime supported by Jellyfin 12.1.
- .NET 10 is required to build Siphon from source.

## Installation from the Jellyfin plugin repository

1. Install Jellyfin Server 12.1.x.
2. Open **Dashboard → Plugins → Repositories**.
3. Add this repository manifest URL:

   ```text
   https://raw.githubusercontent.com/moodiness/jellyfin-plugin-siphon/main/manifest.json
   ```

The URL is intentionally tied to the repository's `main` branch. Siphon updates this manifest automatically when a new release is published, so Jellyfin can keep using the same repository entry across updates.

4. Open **Dashboard → Plugins → Catalog**, find **Siphon**, and install it.
5. Restart Jellyfin.
6. Open the Siphon configuration page from **Dashboard → Plugins → My Plugins → Siphon**.

The repository manifest points to the release archive and includes its Jellyfin ABI and checksum. Install only the version compatible with your Jellyfin major release.

## Manual installation

1. Download `siphon-1.3.1.0.zip` from the [v1.3.1 release](https://github.com/moodiness/jellyfin-plugin-siphon/releases/tag/v1.3.1).
2. Verify the archive against `SHA256SUMS`.
3. Create a versioned folder named `Jellyfin.Plugin.Siphon_1.3.1` inside Jellyfin's plugin directory and extract the archive contents into that folder. The folder must contain `Jellyfin.Plugin.Siphon.dll` and `meta.json`.
4. Restart Jellyfin.
5. Configure Siphon from the plugin dashboard.

The plugin directory depends on the installation method. For Docker, mount a persistent host directory at `/config/plugins` and create the versioned plugin folder there. Do not copy the build output's dependency assemblies into the plugin directory; Jellyfin supplies its own 12.1 assemblies.

## First configuration

1. Open **Dashboard → Siphon** in the **Plugins** sidebar section. The configuration page also remains accessible through **Plugins → My Plugins → Siphon**.
2. Under **Connection & limits**, set **Public Jellyfin base URL** to a URL reachable by Jellyfin and playback clients. Include Jellyfin's base path when one is configured.
3. Under **Addons**, review the preconfigured **Cinemata** addon. Open **Catalogs**, select the catalogs you want, and choose **Save changes**. Cinemata is enabled initially, but no catalog is imported until you select one.
4. Open **Tasks** and run **Sync catalogs**. This tab shows the current stage, completed/total counts, provider requests and cache hits, the last execution summary, and a **Stop sync** action.
5. Open **Dashboard → Libraries**: each selected catalog has its own native library, named from its catalog and media type. Jellyfin displays its native recently-added row on home (`#/home`).
6. If home was already open before the first import, fully reload the web page to refresh Jellyfin's cached library navigation.

Catalogs share the same canonical media identities rather than importing a separate copy for every row. Series contain native seasons and episodes. Siphon manages the backing directories under its private data directory; no manual Movies/TV Shows target path, `.strm` file, or NFO file is required.

Libraries are restored from saved state on startup without another addon fetch. Existing personal libraries are not adopted. Migration from previous Siphon library layouts retains canonical item IDs and user state, moves their descendants together, and transfers the old library's access and home preferences. A mixed library containing personal media retains its unrelated paths and permissions.

Enabling or disabling a catalog controls its native library and home row. Disabled catalogs stop importing; retained media and its state are not automatically deleted. **Remove items no longer returned by enabled catalogs** remains the explicit removal control. Empty catalog libraries keep a stable identity.

Jellyfin library names customized by an administrator and additional personal media paths are preserved on subsequent synchronizations. Catalog pagination follows the addon's actual page size. Repeated, malformed, failed, or import-limit-truncated results cannot establish that an item is missing.

### Working with settings

- **Addons** manages manifests, capabilities, enablement, and playback priority. **Catalogs** manages imported libraries separately. Catalog lists start collapsed; expand an addon or use **Manage catalogs** at the bottom right of its addon card to open its list. The selection count stays visible.
- **Hide catalogs** and **Hide all catalog lists** only collapse the interface: they never disable subscriptions or create unsaved configuration changes. **Selected**, **All available**, and search filter the lists independently of whether they are expanded.
- Expand a catalog's options to change its import limit and advertised filters. Movie, series, and anime catalogs are supported; live-TV catalogs are not.
- Addon manifests load in the background without blocking access to saved settings. Capabilities come from their declared resources, types, and resource-specific ID prefixes, not manual role switches. A declared `tv` type is shown but is not imported as native live TV. **Move up** and **Move down** change addon priority, which also controls stream ordering.
- **Save changes** persists the draft; **Discard changes** restores saved values. Unsaved edits survive navigation away and back within the same dashboard session, but are not stored as a recoverable draft after closing or reloading the browser.
- Saving checks for configuration changes made by another session rather than silently overwriting them. Reload and reapply the draft if a conflict is reported.
- Save or discard edits before synchronizing. Saving settings invalidates existing Siphon playback capabilities, so an active stream may need to be restarted.
- Addon discovery exposes additional results through **Show more**; discovering an addon does not install or enable it.
- **Tasks** groups catalog synchronization, followed-series refresh, and a confirmed **Full refresh** action. Each **Native schedule** link opens Jellyfin's existing Scheduled Tasks editor. Siphon displays the actual triggers and does not maintain a second scheduler.
- **Diagnostics** tests the saved Jellyfin address and addon manifests, shows synchronization outcomes, and exports a report without keys, private URLs, hosts, or headers. Tests require saved settings.
- Connection timeout remains an advanced network safeguard. There is no additional delay setting for local addons and no total playback-duration limit.

### Incremental synchronization and followed series

Normal catalog synchronization defaults to every twelve hours. It still retrieves selected catalogs and their episode listings, but reuses fresh successful metadata-provider responses and skips unchanged native metadata and credits. A durable publication checkpoint lets a later run repair publication interrupted after managed state was saved.

**Refresh followed series** defaults to every two hours. A series is followed when any user has favorited the series, a season, or an episode, or has watched, played, or started one of its episodes, including an alternate version. Normal synchronization processes these series first. The focused task requests their episode metadata directly and bypasses the metadata-provider cache; it does not request catalog pages. Unfollowed series and catalog ownership remain unchanged. Omitted episodes and failed series retain their previous items and user history.

**Full refresh** has no default schedule and requires confirmation in Siphon's Tasks tab. It bypasses the metadata-provider cache while respecting the configured update policy, selected fields, and native metadata locks. It does not rebuild item identities or erase watched state.

The last run records additions, changes, unchanged and preserved items, removals, failed subscriptions, provider errors, and cache hits. Cancellation and failures remain distinct from successful completion. An interrupted run is reported after restart rather than left permanently running.

### Protected cleanup

Cleanup only considers Siphon-owned media whose absence was confirmed by complete successful results from every owning subscription. Disabling or removing a subscription, an unavailable addon, or an incomplete catalog does not confirm absence.

The default grace period is seven days and can be set to zero. Favorites and resume positions are protected by default across all users and native stream versions, including favorites on parent series or seasons. If protection data cannot be read, cleanup is refused.

Use **Preview cleanup** to inspect eligible and protected items before confirming removal. Previews expire after five minutes; configuration, synchronization, and user protections are checked again when executing. A stale preview must be regenerated. Manual confirmed cleanup is available independently of automatic removal; personal media files are never deleted.

### Native home appearance

Row ordering, visibility, and watched-item filtering follow Jellyfin's per-user home settings. The **Recently Added in** prefix belongs to the native client; Siphon does not replace it with an injected home script. Library navigation shortcuts and My Media tiles can be hidden separately with Jellyfin Web's standard **Branding → Custom CSS**, without removing the underlying libraries. This affects the web client only. Excluding a library through My Media preferences can also hide its recently-added row.

### Optional metadata providers

The **Metadata & providers** tab supports real, individually opt-in integrations:

- **TMDB**: a Read Access Token for titles, plots, dates, genres, credits, episode details, and artwork.
- **TVDB**: an API key and, when required by the subscription, a subscriber PIN for additional metadata.
- **Fanart**: a project API key for artwork.
- **MDBList**: an API key for aggregate ratings, normalized to Jellyfin's 0–10 scale.

Storing a key alone does not enable a provider. Saved credentials can be explicitly tested. Enabled providers run during synchronization using existing provider identities rather than guessing matches by title; unavailable or invalid responses do not erase prior metadata.

Leave the language blank to inherit Jellyfin's metadata language or choose a language such as `fr-FR`. **Fill missing fields only** is the default. **Refresh selected fields** updates only the selected categories. Native global and per-field metadata locks are respected, and a probed stream version keeps its actual runtime. TMDB supplies primary metadata, TVDB fills gaps, Fanart contributes artwork, and MDBList contributes aggregate ratings when available.

Successful validated responses are cached on disk for **24 hours** by default; **Metadata cache lifetime** accepts 1–168 hours. Changes to provider credentials, metadata language, update policy, selected fields, content identity, or episode set invalidate the relevant cache entries. Failed or invalid responses are not cached. Cache files are private, bounded, atomically replaced, and use hashed identities; protect the plugin data directory as well as its configuration.

Authentication failures and three consecutive transient failures temporarily pause the affected provider rather than repeatedly delaying the entire library. Other providers continue. Rate limits respect `Retry-After`. Provider status shows the pause reason and deadline; an explicit saved-credential test can check recovery, but cannot bypass an active rate-limit deadline. These tests do not alter synchronization counters.

Synchronization reports progress through catalog retrieval, metadata enrichment, native item publication, and credit publication. Native credits are written in bounded, cancellable batches; repeated refreshes reuse person identities and leave unchanged credits alone.

Cast portraits are registered as on-demand native images, not downloaded as part of synchronization. Jellyfin retrieves and caches a portrait when a client displays it, through Siphon's signed image proxy. Existing native portraits and metadata locks are preserved, and synchronization does not wait for the entire cast's artwork to download.

Jellyfin stores these credentials in its plugin configuration. Masked fields are not encryption; protect configuration files and backups.

## Cinemata and addon management

Cinemata is included so a new installation immediately demonstrates the catalog and metadata workflow. It can be:

- disabled with **Enable addon**;
- configured by selecting one or more catalogs;
- removed with **Remove addon** and then **Save changes**;
- re-added later using its manifest URL:

```text
stremio://v3-cinemeta.strem.io/manifest.json
```

Addon manifest URLs are stored in Jellyfin's plugin configuration. Protect configuration backups when URLs contain credentials.

## Playback and security model

Open a movie or episode detail page and choose **Version** before playing. From a series, open the desired episode to select that episode's version. Siphon resolves sources lazily for the requested title, not for every episode during catalog synchronization.

Each supported stream is represented by a real native alternate version. Existing canonical IDs remain unchanged; each source has its own native version identity, while Jellyfin owns playback and resume tracking. Source ordering follows configured addon order, then each addon's response order—never a name, quality, or resolution sort. Unavailable versions leave the selector but retain their native records and user history. The selected source is probed at playback time so its actual container, tracks, and runtime are used.

Source-to-version bindings are persisted as hashes. Reordering or removing otherwise indistinguishable mirrors does not reassign an observed mirror's ordinal to another endpoint; rotating authentication query parameters does not create a new version. An unavailable selected source fails rather than silently playing another cut. Reappearing sources recover their existing native identities. On upgrade, older ordinal-only records contain no historical endpoint identity, so the first observation necessarily establishes the binding from the addon's current order.

Probed tracks remain available to Jellyfin's remux and subtitle-extraction paths. Finite HTTP playback does not retain a native tuner lease that can become stale during an audio change. Actual audio and subtitle indices belong to the selected version, not to another cut.

Native subtitle upload, provider download, and deletion work for Siphon's remote videos. Sidecars are stored under that version's private native metadata directory without changing its playable path or the library's subtitle-storage setting. Reopening or probing a source preserves its external subtitles; subtitles do not leak between versions.

Siphon fetches upstream resources from the Jellyfin server. Clients receive signed Siphon URLs; playback sessions use short-lived opaque capabilities. HTTP(S) progressive streams and HLS are supported; torrent URLs and unsupported protocols are rejected.

Private destinations are rejected by default. Add an exact trusted hostname under **Allowed private hosts** only when required, and never use wildcards or broad network ranges. See [SECURITY.md](SECURITY.md) for reporting security issues and the security boundaries.

The public base URL must also work **from inside the Jellyfin container**. A loopback address with a host-only forwarded port is not reachable at that port inside the container.

Upstream connection and idle-read deadlines use the configured addon timeout; this does not limit a film's total playback duration. Private-host exceptions do not leave reusable approved connections after revocation. Media ranges preserve original bytes, and HLS resources are classified and rewritten even when an upstream URL has a misleading extension.

Live-TV catalogs using Stremio's `tv` type are not imported. HTTP(S) support does not imply live-TV catalog support.

## Building and testing

```bash
dotnet test Jellyfin.Plugin.Siphon.sln -c Release
python3 scripts/package.py
```

The build targets `net10.0` and Jellyfin ABI `12.1.0.0`. Release packaging produces `artifacts/siphon-1.4.0.0.zip`, a repository manifest, and `SHA256SUMS`. The matching release tag is `v1.4.0`; generating these files does not create a tag or publish a release. The root `manifest.json` continues to advertise the latest published release until the release workflow updates it.

## License

Siphon is distributed under the GNU General Public License v3.0. See [LICENSE](LICENSE).

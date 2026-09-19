<h1 align="center">Siphon</h1>

<p align="center">
  <img src="assets/siphon.png" alt="Siphon" width="96" height="96">
</p>

Siphon brings **Stremio addons into Jellyfin**: catalogs, metadata, subtitles, and selectable stream versions through a server-side proxy and native Jellyfin libraries.

Siphon targets **Jellyfin 12.1** and .NET 10. The current source includes an opt-in, bounded MonoTorrent engine. It is not a debrid client or an external-player integration.

**[Latest published release](https://github.com/moodiness/jellyfin-plugin-siphon/releases/latest).** Git tags use three components and plugin versions use four: tag `v1.6.5` corresponds to plugin version `1.6.5.0`. The stable repository manifest on `main` advertises verified published archives and retains earlier compatible versions.

**Siphon 1.5.0.0** adds isolated per-user playback/subtitle addons and search preferences; durable native collections and playlists; catalog collections; selected-version resumable downloads; selective orphan-history recovery; field provenance and per-title source diagnostics; a followed-series calendar and opt-in notifications; all IntroDB timing types; and real, bounded P2P playback. It also includes native addon Search, targeted synchronization, a twenty-run decision history, provider quota/cooldown tracking, shared response caching, the supplied Siphon icon, and authoritative addon metadata with an optional missing-season-only TMDB supplement. The published package also validates media response types and constrains document rendering on media routes.

**Siphon 1.6.0.0** adds private persistent downloads with pause, priority, quotas, scheduling, batch preparation and track selection; finite-HLS offline exports; signed notification adapters and optional digests; French/English controls and local operational health; verified offline backup/restore and assets-first release automation. Background downloads and external delivery remain disabled by default. It also bounds request admission and state reads, moves saved-library restoration off the host startup path, and scopes search additions/removals to the affected titles.

**Current source: 1.6.5.0.** After a successful native opening, remux and seek requests carrying the same user, PlaySessionId and exact version reuse a private copy of that opening's source and probed tracks, even when discovery is withdrawn. The already-authorized opaque proxy lease is reused without silently substituting another version. Permissions, content identity, ownership, session identity and lease validity are rechecked; invalid or expired sessions fail closed. This release retains local FFmpeg/ffprobe routing, HLS rewriting and the existing SSRF, range and playback protections.

## Features

- Present each selected movie or series catalog as a native library, collection, or both.
- Share canonical movies, series, seasons, and episodes between catalogs without duplicate home cards or JavaScript injection.
- Preserve IMDb, TMDB, TVDB, MyAnimeList, and addon-specific identities, along with existing Jellyfin favorites, watched state, and resume positions.
- Map plots, dates, runtimes, ratings, genres, credits with portraits, season posters, and episode-specific artwork into native metadata fields.
- Select AIOMetadata or another installed metadata addon as the authoritative source, without silently falling back to Cinemata.
- Fill missing numbered season posters from TMDB without replacing the selected addon's other metadata or episode artwork.
- Reuse fresh provider responses across restarts and avoid rewriting unchanged native metadata or credits.
- Refresh followed series directly, without browsing every catalog, while retaining episodes omitted by a partial response.
- Refresh one catalog or one series without reconciling unrelated catalog membership.
- Review the latest twenty synchronization runs and page through each title's action and reasons.
- Discover addon titles in Jellyfin's ordinary global Search; retain explicit additions, collection/playlist members, favorites, and played or resumable discoveries.
- Offer each supported stream as a native movie or episode **Version**, preserving the order returned by the addon.
- Discover addons advertised through Stremio `addon_catalog` resources.
- Proxy HTTP(S) progressive media and HLS server-side without exposing upstream URLs or addon headers to players.
- Assign playback and subtitle addons per Jellyfin user without changing shared catalog or metadata ownership.
- Choose personal local-only or addon-inclusive search, with optional unreleased-title filtering.
- Download the exact progressive or P2P version with byte-range resume and current-user permission checks.
- Queue an exact native version for private HTTP/P2P downloading or finite-HLS export, with pause/resume, priority, user quotas, UTC windows, selected tracks and bounded batch preparation.
- Follow dated episodes in a personal calendar and receive opt-in, persistent release/date-change notifications.
- Deliver opted-in calendar events as JSON, Discord, Slack or ntfy requests, with event filters, optional digests, one-time signing secrets, encrypted settings and visible bounded retries.
- Project IntroDB intro, recap, end-credit and post-credit timings into native segments and chapters.
- Inspect recorded field provenance and source outcomes, or preview and selectively restore proven orphaned Siphon history.
- Stream supported torrents through an opt-in engine with connection, cache, rate and lifetime limits.
- Manage addons and catalog selections through French/English native settings, with a first-import guide, a shareable personal-portal link, aggregate local health, protected cleanup and opt-in metadata providers.
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

1. Download the complete plugin ZIP and `SHA256SUMS` from the [latest release](https://github.com/moodiness/jellyfin-plugin-siphon/releases/latest).
2. Verify the archive against `SHA256SUMS`.
3. Stop Jellyfin and extract the complete archive into one matching versioned plugin directory, for example `Siphon_1.6.5.0`. The folder must contain `Jellyfin.Plugin.Siphon.dll` and `meta.json`. Keep previous binaries outside the plugin directory for rollback; do not leave two copies installed or remove the separate Siphon data directory.
4. Restart Jellyfin.
5. Configure Siphon from the plugin dashboard.

The plugin directory depends on the installation method. For Docker, mount a persistent host directory at `/config/plugins` and create the versioned plugin folder there. Extract the **entire packaged archive**, including its MonoTorrent dependencies and `licenses/` directory. Do not copy arbitrary build-output assemblies: Jellyfin supplies its own 12.1 server assemblies, which are deliberately excluded from the package.

### Recovering from the 1.6.0.0 large-state startup failure

If the startup log reports `Siphon state exceeds the 268,435,456-byte document limit`, preserve the existing state: **do not delete or truncate `state.json`, regenerate `signing.key`, or remove the Jellyfin database**. The size check is a 1.6.0.0 regression, not evidence of a corrupt catalog or insufficient free disk space.

Stop Jellyfin before replacing plugin binaries. To bring the server back without Siphon temporarily, move every Siphon version directory outside the actual plugin directory reported in its logs, then restart. Docker layouts differ: the official image commonly uses `/config/plugins`, while a deployment with program data at `/config/data` uses `/config/data/plugins`. Keep the removed binaries for rollback and leave the separate Siphon data directory untouched. Avoid library scans while the plugin is absent.

Install the complete, verified 1.6.1.0-or-newer package into one matching versioned plugin directory with the server stopped, retaining the existing configuration, native database and Siphon data at their original paths. Restart and confirm both the loaded Siphon version and successful host startup. Large persisted metadata still consumes memory and disk; the fix does not silently discard records or replace invalid state with an empty catalog.

## Offline backup and restore

Use `scripts/backup.py` from this repository to preserve a **complete, stopped Jellyfin instance**, not just Siphon's settings. It is a local filesystem utility, not a live/remote backup API. It requires Python 3.11 or newer, a current SQLite library, and local POSIX filesystem semantics on Linux/macOS. Network filesystems, Windows, symlinked trees, hard-linked files, special files, special permission bits and ambiguous installed Siphon versions are refused. Pass real, non-symlinked paths, including their ancestors.

The snapshot includes the entire Jellyfin program-data tree (`data/`, `plugins/`, native library/user/history databases, server identity, metadata and library definitions) and the entire configuration tree, including every plugin configuration. Under `data/siphon/`, this includes `state.json`, the original 32-byte `signing.key`, managed library/catalog directories, recovery records, preferences, calendar/inbox state, download ownership/queue/files, encrypted webhook state and all other present private stores. These are copied together; no identities, favorites, watched state or resume positions are regenerated or merged. A configured instance with native `data/jellyfin.db`, `data/device.txt`, Siphon state/key, saved plugin configuration, and one installed Siphon DLL with matching `meta.json` is required.

**Scope and path mapping:** `--program-data` means Jellyfin's whole program-data directory, not its `data/` child. `--config-dir` defaults to `PROGRAM_DATA/config`; an external configuration directory is included separately. Include custom external metadata or other required persistent trees using repeatable `--extra-root NAME=PATH`; roots must not overlap. External personal media, cache/transcode directories and logs outside the selected roots are not automatically discovered or backed up. Inventory your deployment's mounts and configured paths first. The tool does not back up the Jellyfin executable/container image; retain the exact server image/version separately.

### Create, inspect and restore

Stop Jellyfin cleanly and disable service/container restarters for the entire operation. Run the tool as the owning account, or as root when preserving mixed ownership. Supply every approved **numeric** owner pair with repeatable `--owner UID:GID`; unapproved source/archive owners are refused. Ownership is preserved, never remapped. In this example all instance files belong to `1000:1000`; add `--owner 0:0` only if root-owned instance files are expected. Replace the example versions with the actual stopped server and installed plugin versions.

```bash
python3 scripts/backup.py create \
  --program-data /srv/jellyfin/config \
  --config-dir /srv/jellyfin/config/config \
  --output /srv/backups/jellyfin-offline.tar \
  --jellyfin-version 12.1.0 --siphon-version 1.6.0.0 \
  --owner 1000:1000 --ack-stopped
```

The output archive must be new, outside every source tree, and have an existing parent directory. The tool creates a private, uncompressed tar archive with strictly canonical bounded POSIX/PAX headers, per-file SHA-256 checksums and a manifest checksum, validates the copied SQLite databases, then publishes it without replacing an existing archive. PAX supports long UTF-8 names and files larger than USTAR's 8 GiB boundary; arbitrary extension records are refused. Its JSON result contains counts, versions and `archive_sha256`, not filenames, credentials or native user data. Retain that digest separately in trusted storage. The recorded Jellyfin version is the operator's assertion; the Siphon version is also checked against installed plugin metadata. Checksums detect corruption, not an attacker who can replace both archive and expected digest.

```bash
python3 scripts/backup.py validate /srv/backups/jellyfin-offline.tar \
  --jellyfin-version 12.1.0 --siphon-version 1.6.0.0 --owner 1000:1000

python3 scripts/backup.py plan /srv/backups/jellyfin-offline.tar \
  --destination /srv/jellyfin-restored \
  --jellyfin-version 12.1.0 --siphon-version 1.6.0.0 --owner 1000:1000

python3 scripts/backup.py restore /srv/backups/jellyfin-offline.tar \
  --destination /srv/jellyfin-restored \
  --jellyfin-version 12.1.0 --siphon-version 1.6.0.0 \
  --owner 1000:1000 --ack-stopped

python3 scripts/backup.py verify-restored /srv/backups/jellyfin-offline.tar \
  --destination /srv/jellyfin-restored \
  --jellyfin-version 12.1.0 --siphon-version 1.6.0.0 \
  --owner 1000:1000 --ack-stopped
```

For each archive-reading command, add `--sha256` followed by the separately retained `archive_sha256` to verify its expected identity. `validate` checks the entire archive, required state, checksums, members and copied SQLite integrity; `plan` performs the same checks and confirms that the selected destination is absent or empty, without restoring anything. `restore` repeats validation before staging any restored files. The destination's parent must already exist; **a populated destination is never overwritten or merged**, even if it contains newer native history. `verify-restored` checks the complete resulting inventory, file bytes, ordinary permissions and numeric ownership while the restored instance remains stopped. Starting Jellyfin changes native state, so run this comparison before its first start.

The restored envelope contains `program/`, plus `config/` only when configuration was external, and one directory per extra root. In the example, mount `/srv/jellyfin-restored/program` at the original container `/config`; configuration remains its `config/` child. For an external configuration tree, mount restored `config/` at its original runtime configuration path. Reuse **the same runtime paths and native media mounts**, numeric account identities, Jellyfin version and Siphon version; managed state can contain absolute paths. The tool deliberately does not rewrite paths, upgrade schemas, rotate keys, repair orphans, or merge history. Keep the original instance stopped and untouched while evaluating the restored copy. Starting both copies can duplicate external notifications and background work.

All commands are size-bounded: defaults are 128 GiB total payload, 32 GiB per file, 200,000 filesystem entries and 64 MiB metadata. Set `--max-bytes`, `--max-file-bytes` and `--max-members` explicitly for larger trusted instances, consistently on create and validation/restore. Allow space for the uncompressed archive, the complete restored tree and a private temporary copy of the largest SQLite database. No source file is intentionally modified. Source database files nevertheless require write-open permission to acquire POSIX exclusive byte-range locks.

### Stopped Docker container procedure

For a Linux Docker host with a bind mount `/srv/jellyfin/config:/config`, first record the exact image identifier/version, original restart policy, numeric container account, and all persistent mount destinations in your private operational records. Do not dump environment variables or a complete container inspection into shared evidence; those may contain credentials.

```bash
docker inspect --format '{{.Image}} {{.HostConfig.RestartPolicy.Name}} {{.Config.User}}' jellyfin
docker update --restart=no jellyfin
docker stop --time 120 jellyfin
docker inspect --format '{{.State.Status}} {{.State.Running}} {{.State.Restarting}} {{.State.ExitCode}} {{.State.OOMKilled}}' jellyfin
```

Require a clean completed stop, `Running=false`, `Restarting=false`, and no OOM/forced-kill indication. Disable the corresponding Compose/systemd/orchestrator restart mechanism as well; changing Docker's restart policy does not stop another supervisor. Run the host Python commands above against the stopped bind-mount source. For named volumes, locate the actual volume mountpoint on the **Linux Docker host** and use it as `--program-data`; Docker Desktop VM volumes are not ordinary macOS host paths. Do not use `docker cp` from a running server, and do not restart it during copying.

After restore and `verify-restored`, create an isolated replacement container using the recorded exact image and settings, bind the restored `program/` tree to the same `/config` destination, and retain the original media/mount destinations. Keep the original container stopped; use an isolated port and control outbound access during evaluation. Confirm the original native user/item IDs, favorite/watched/resume state, managed library visibility and Siphon private state before enabling scheduled work and the desired restart policy. Restoring the original container's former restart policy is an explicit operator action, not something this utility does.

**Offline checks have limits:** `--ack-stopped` acknowledges that the operator has stopped the instance and all writers. The utility also refuses any SQLite WAL/SHM/journal sidecar, acquires SQLite's conventional POSIX lock-byte range on every detected database for the full source copy/comparison, checks source stability, and runs `quick_check` on copied databases. These checks can reject active SQLite readers/writers but cannot prove the absence of an idle process, a process using different locking semantics, a supervisor that restarts later, or a writer to a non-database file. Never delete sidecars to pass a check: if a forced stop left them behind, let the original server recover and then stop it cleanly before retrying.

Handled interruption (Ctrl-C, SIGTERM, SIGHUP) removes private incomplete staging. SIGKILL, power loss or filesystem failure can prevent cleanup; they never make an unfinished staging file a published snapshot. After confirming no backup process remains, inspect only this tool's `.siphon-backup-*.tmp`, `.siphon-restore-*` and temporary `siphon-verify-*` entries in the chosen output/destination/temp directories. Keep incomplete data private and remove only entries known to belong to the interrupted operation. Archives and temporary copies contain credentials, signing material, private history and possibly downloaded media: use private storage and authenticated encryption for retained/off-host copies.

## First configuration

1. Open **Dashboard → Siphon** in the **Plugins** sidebar section. The configuration page also remains accessible through **Plugins → My Plugins → Siphon**.
2. Under **Connection & limits**, set **Public Jellyfin base URL** to a URL reachable by Jellyfin and playback clients. Include Jellyfin's base path when one is configured.
3. Under **Addons**, review the preconfigured **Cinemata** addon. Open **Catalogs**, select the catalogs you want, and choose **Save changes**. Cinemata is enabled initially, but no catalog is imported until you select one.
4. Open **Tasks** and run **Sync catalogs**. This tab shows the current stage, completed/total counts, provider requests and cache hits, the last execution summary, and a **Stop sync** action.
5. Open **Dashboard → Libraries**: each selected catalog has a backing native library, named from its catalog and media type. The default **Library** presentation exposes its My Media entry and recently-added row; **Collection** and **Both** also publish a native collection.
6. If home was already open before the first import, fully reload the web page to refresh Jellyfin's cached library navigation.

Catalogs share the same canonical media identities rather than importing a separate copy for every row. Series contain native seasons and episodes. Siphon manages the backing directories under its private data directory; no manual Movies/TV Shows target path, `.strm` file, or NFO file is required.

Libraries are restored from saved state without another addon fetch. Restoration starts in the background **after Jellyfin reports host startup complete**, rather than blocking its startup screen. Tasks records the startup run, including cancellation or failure; stopping the server cancels and joins the owned worker. Existing personal libraries are not adopted. Migration from previous Siphon library layouts retains canonical item IDs and user state, moves their descendants together, and transfers the old library's access and home preferences. A mixed library containing personal media retains its unrelated paths and permissions.

Enabling or disabling a catalog controls its presentation and importing; retained media and its state are not automatically deleted. **Remove items no longer returned by enabled catalogs** remains the explicit removal control. Empty catalog libraries keep a stable identity.

Jellyfin library names customized by an administrator and additional personal media paths are preserved on subsequent synchronizations. Catalog pagination follows the addon's actual page size. Repeated, malformed, failed, or import-limit-truncated results cannot establish that an item is missing.

Each catalog's **Presentation** selects **Library**, **Collection**, or **Both**. Collection-only mode hides that catalog's backing library from user views, not from administration or permission checks. Its titles keep the same canonical IDs. Reconciliation removes only Siphon's automatic collection links after confirmed catalog absence; deliberate additions to that collection, and unrelated collections or playlists, remain intact.

### Working with settings

- **Addons** manages shared manifests, capabilities, enablement, and playback priority. **Catalogs** manages subscriptions and presentation separately. Catalog lists start collapsed; expand an addon or use **Manage catalogs** at the bottom right of its addon card to open its list. The selection count stays visible.
- **Hide catalogs** and **Hide all catalog lists** only collapse the interface: they never disable subscriptions or create unsaved configuration changes. **Selected**, **All available**, and search filter the lists independently of whether they are expanded.
- Expand a catalog's options to change its import limit and advertised filters. **Catalog filter** uses addon-defined values, such as `Day` or `Week` for a trending period. Its help text stays neutral rather than displaying a potentially misleading technical parameter name. Parameter names and values stay unchanged in requests. Movie, series, and anime catalogs are supported; live-TV catalogs are not.
- Addon manifests load in the background without blocking access to saved settings. Capabilities come from their declared resources, types, and resource-specific ID prefixes, not manual role switches. A declared `tv` type is shown but is not imported as native live TV. **Move up** and **Move down** change addon priority, which also controls stream ordering.
- **Save changes** persists the draft; **Discard changes** restores saved values. Unsaved edits survive navigation away and back within the same dashboard session, but are not stored as a recoverable draft after closing or reloading the browser.
- Saving checks for configuration changes made by another session rather than silently overwriting them. Reload and reapply the draft if a conflict is reported.
- Save or discard edits before synchronizing. Changing a user's playback profile revokes that user's affected source capabilities, not another user's profile. Global transport/security changes can invalidate all affected streams. Personal search and notification preference changes do not revoke playback.
- Addon discovery exposes additional results through **Show more**; discovering an addon does not install or enable it.
- **Tasks** groups catalog synchronization, followed-series refresh, targeted catalog/series refresh, run history, and a confirmed **Full refresh** action. Each **Native schedule** link opens Jellyfin's existing Scheduled Tasks editor. Siphon displays the actual triggers and does not maintain a second scheduler.
- **Diagnostics** tests the saved Jellyfin address and addon manifests, shows synchronization outcomes, and exports a report without keys, private URLs, hosts, or headers. Tests require saved settings.
- **Users & defaults** manages server search defaults, calendar/webhook availability, and per-user addon overrides. **Playback & P2P** manages IntroDB timing types, the opt-in torrent engine and private offline-download limits. **Open my Siphon portal** opens the authenticated, non-admin personal interface.
- Connection timeout remains an advanced network safeguard. There is no additional delay setting for local addons and no total playback-duration limit.

### Incremental synchronization and followed series

Normal catalog synchronization defaults to every twelve hours. It still retrieves selected catalogs and their episode listings, but reuses fresh successful metadata-provider responses and skips unchanged native metadata and credits. A durable publication checkpoint lets a later run repair publication interrupted after managed state was saved.

Overlapping native scheduled tasks wait their turn rather than failing or dropping a requested refresh. Waiting tasks remain cancellable from Jellyfin's scheduled-task controls without interrupting the active synchronization. Siphon's Tasks tab distinguishes waiting tasks, and **Stop active task** stops the executing task, not other queued tasks.

Already-loaded media details and stream resolution do not wait for catalog or metadata fetches during synchronization. A newly selected discovery that needs metadata expansion returns a retryable busy response instead of queueing behind the synchronization. Publication and cleanup still serialize native library writes, and settings cannot be changed while a sync is running.

**Refresh followed series** defaults to every two hours. A series is followed when any user has favorited the series, a season, or an episode, or has watched, played, or started one of its episodes, including an alternate version. Normal synchronization processes these series first. The focused task requests their episode metadata directly and bypasses the metadata-provider cache; it does not request catalog pages. Unfollowed series and catalog ownership remain unchanged. Omitted episodes and failed series retain their previous items and user history.

To refresh a single saved catalog or known series, select it under **Tasks** and start a targeted run. A catalog run reconciles only that subscription's ownership; a title shared with another catalog remains available. A series run fetches only that series and retains episodes omitted by a partial response. Unrelated media, favorites, and playback history stay untouched. Targeted runs are manual, have no default schedule, and reject another target while one is pending or running.

**Full refresh** has no default schedule and requires confirmation in Siphon's Tasks tab. It bypasses the metadata-provider cache while respecting the configured update policy, selected fields, and native metadata locks. It does not rebuild item identities or erase watched state.

The latest **twenty executions** survive restarts, with additions, changes, unchanged and preserved items, removals, failed subscriptions, provider errors, and cache hits. Select an execution in **Tasks** to page through its per-title action and reasons. Cancellation and failures remain distinct from successful completion; an interrupted run is reported after restart rather than left permanently running. History is administrator-only and stores bounded title decisions rather than upstream request or response payloads.

When a catalog synchronization partially fails, the native task remains **Failed** even if retained items were published successfully. **Tasks** lists each recorded failed subscription with its saved catalog label, stable catalog identity, and safe failure cause; these details remain available in recent history after a restart or a later successful run. Failed or incomplete snapshots preserve prior items and never authorize their removal. Each run retains at most 256 subscription failure details, with the total failure count shown separately; older runs without these details cannot identify their failed subscriptions retroactively. The administrator-only `/Siphon/Sync/Status` and `/Siphon/Sync/History` responses expose `SubscriptionFailures` as catalog-identity/error-code pairs, never upstream URLs or raw exception messages.

### Native search and saved additions

Jellyfin's ordinary global **Search** searches compatible enabled addons alongside accessible native library results. It does not require opening Siphon's settings. Only advertised search catalogs whose required filters can be satisfied are queried; catalog subscriptions are not required for discovery.

Search cards are lightweight previews and do not publish series episodes. If a catalog result lacks a usable poster, the bounded search may fetch that title's metadata to obtain artwork; simply displaying the card or requesting its image does not expand the title. Opening a selected title loads its details and native seasons/episodes. Existing canonical items retain their identities instead of being duplicated. Search does not queue addon work behind synchronization; already available local results remain usable.

Administrators can also use **Siphon → Search & additions → Add to library** to retain a discovery outside any selected catalog. Adding the same title again is idempotent. Adding a discovery through Jellyfin's native collection or playlist actions also makes it durable, without changing its canonical ID or relaxing native permissions. A discovery favorited, played, or started by any user is retained by periodic maintenance. Saved additions survive synchronization and restarts in native **Siphon saved** libraries.

**Remove saved addition** is available on search results and saved-addition rows. It requires administrator confirmation because it changes the shared library for all users, not just a personal list. Removing the explicit saved owner leaves catalog-owned content available; manual-only native items are removed. Jellyfin's retained history is reattached on a later addition when the native user-data keys match unambiguously. Adding or removing a title reconciles only its affected native items, rather than hydrating and republishing every managed item.

Unprotected previews expire after 24 hours and are limited to 300 titles. Preview libraries stay out of My Media and recently-added rows. Jellyfin's library visibility rules still apply to search results and detail expansion. If user protection data is unavailable, maintenance retains previews rather than risking removal of a favorite or resume item.

### User profiles and personal preferences

Under **Users & defaults**, select a Jellyfin user and choose inherited shared playback addons or an explicit replacement list. The replacement applies to both streams and addon subtitles. An **empty override means no providers**, never fallback to shared credentials. Deleting the override restores inheritance. Catalog synchronization, addon discovery/search catalogs, and the selected metadata addon remain shared administrative settings.

Users open **My Siphon** at `/Siphon/User` (under the server's configured base path) and sign in with their own Jellyfin account. The portal keeps its session token in memory, not in download URLs or browser storage; reloading requires another sign-in. It exposes personal preferences, calendar, notifications, webhook settings, source diagnostics and private downloads, not administrative configuration or addon secrets.

Search can inherit the server default, search **Local library only**, or include addon discovery. Unreleased filtering affects Siphon search results, including native fallback searches and hints, before pagination; it does not hide personal media. Dates are announced metadata, not evidence of a playable stream. Unknown release dates remain visible. The administrator can configure a release buffer of 0–30 days.

### Followed-series calendar and notifications

The personal **Release calendar** uses dated, accessible native episodes from series that the current user follows through favorites, watched episodes or playback progress. It does not substitute another user's follows or call addon providers just to display the calendar. Entries link to native Jellyfin episodes; an announced release is not a source-availability check.

Notifications require both the administrator's **Enable calendar notifications** setting and the user's opt-in. The portal inbox records newly discovered dated episodes, date changes, and announced releases. Initial opt-in establishes a baseline rather than sending an old backlog. Read state and deduplication survive restarts; inaccessible items are not shown. Storage is bounded, and unavailable/oversized observations are not treated as an empty calendar that would replay the backlog. This is a Siphon inbox, not a promise of push notifications from every Jellyfin client.

### Signed external notification webhooks

External delivery additionally requires the administrator's **Enable notification webhooks** setting and the user's saved opt-in under **My Siphon → Notifications**. Choose **JSON** for a custom receiver, or the explicit **Discord**, **Slack** or **ntfy** adapter for its destination format. Select eligible event kinds and optionally group notifications into a digest. These requests disclose permitted series/episode titles and announced dates to the chosen destination; this is not browser push or a guarantee of mobile delivery.

Save the destination, generate a signing secret, then use **Send test**. For a receiver you operate, copy the secret to that receiver and verify requests as described below. Secrets are shown only when generated; ordinary status reads never reveal them. Tests are explicit synthetic events, and a failed send is reported as failure. Tests are limited to one per user per minute and eight server-wide per minute. URLs require HTTPS unless an administrator permits the exact hostname; embedded credentials, fragments, redirects and disallowed private destinations are rejected.

Every adapter's POST carries `X-Siphon-Event-Id` (a UUID) and `X-Siphon-Signature` (`sha256=` followed by lowercase hexadecimal HMAC-SHA256). Base64-decode the generated secret to obtain the 32-byte key, verify the **exact received UTF-8 request body** with a constant-time comparison, and deduplicate by event ID. The JSON adapter's version-1 payload contains `Type`, `EventId`, `CreatedUtc`, `Test`, and, for `calendar.notification`, `Kind`, `Episode`, `PreviousAnnouncedReleaseUtc` and `AvailabilityNote`. JSON synthetic events use `Type: webhook.test` and `Test: true`; grouped delivery uses `calendar.digest`. Other adapters use their destination-specific body. Reading an inbox item does not change a retry's payload.

Delivery is bounded, at-least-once rather than guaranteed: at most five attempts, with 30/120/480/1920-second backoffs and a 24-hour expiry. Network failures, HTTP 408/429 and 5xx are retryable; other HTTP failures are terminal. The portal shows delivery state, the latest attempt, the next attempt and the safe reason for abandonment. Pending state, attempts and event IDs survive restart. Setup, reconfiguration and secret rotation establish a baseline instead of sending an existing inbox backlog. Opt-out and lost item/account access stop further eligible delivery; an already-transmitted request cannot be recalled.

Settings, secrets and delivery state are encrypted together with AES-GCM in the private plugin data directory, using a domain-separated key derived from Siphon's signing key. Protect that key and backups: encryption does not hide data from the Jellyfin administrator. Storage is capped at 1,024 users, 200 pending deliveries per user, 10,000 total pending deliveries and a 16 MiB encrypted document.

### Protected cleanup

Cleanup only considers Siphon-owned media whose absence was confirmed by complete successful results from every owning subscription. Disabling or removing a subscription, an unavailable addon, or an incomplete catalog does not confirm absence.

The default grace period is seven days and can be set to zero. Favorites and resume positions are protected by default across all users and native stream versions, including favorites on parent series or seasons. Deliberate native collection and playlist membership is protected independently of those switches. If protection data cannot be read, cleanup is refused.

Use **Preview cleanup** to inspect eligible and protected items before confirming removal. Previews expire after five minutes; configuration, synchronization, and user protections are checked again when executing. A stale preview must be regenerated. Manual confirmed cleanup is available independently of automatic removal; personal media files are never deleted.

### Selective orphan-history recovery

Back up Jellyfin's data before repairing history. Under **Tasks → Selective watch-state recovery**, **Preview recoverable state** reads orphaned native rows without modifying user history. It shows the user, watched/favorite/resume/play-count values, ownership evidence, intended action and any blocking reason.

Select only the intended rows and confirm **Recover selected**. A current, single-use preview permits at most 50 selected candidates. History is copied to an existing managed target, or a missing title is restored only with proven identity and usable metadata from the selected addon. Unknown ownership, ambiguous targets and meaningful live-history collisions are refused. No personal item is adopted and no selected-addon failure triggers fallback.

Orphan aliases are separate candidates: inspect their displayed history rather than assuming every row for one title contains the same values. Unselected users/rows and original orphan records remain untouched. This is explicit repair, never automatic resurrection or a bulk overwrite of newer state; Jellyfin's own orphan-retention policy still applies.

### Native home appearance

Under **Siphon → Catalogs**, **Show catalog shortcuts in the top navigation** is off by default. Enable it to show Siphon libraries in Jellyfin Web's desktop header and its **More** menu. Save, then fully reload Jellyfin Web. This switch does not hide native home rows or My Media tiles, remove library access, or affect personal-library and custom navigation links. Siphon adds scoped CSS to native branding responses without modifying your saved custom CSS or user preferences.

Row ordering, visibility, and watched-item filtering follow Jellyfin's per-user home settings. The **Recently Added in** prefix belongs to the native client; Siphon does not replace it with an injected home script. To remove the My Media tile section while retaining rows, change Jellyfin's home-section layout rather than excluding its libraries.

If libraries contain media but their home rows are missing, open **User Menu → Settings → Home**, assign an available section to **Recently Added Media** (called **Latest Media** in some clients), and save. **Continue Watching** and **Next Up** do not replace the recent-media section and can both be empty. Keep the libraries included under **Display in other home screen sections**; **Hide watched content from 'Recently Added Media'** can also filter out every eligible item. The presence of a library shortcut alone does not establish that its catalog has been imported.

### AIOMetadata and authoritative addon metadata

1. Under **Addons**, install and enable your configured AIOMetadata manifest. Keep the URL produced by your AIOMetadata configuration; Siphon does not reconstruct its preferences or require copying its provider credentials.
2. Under **Metadata & providers → Metadata addon**, select that installed addon and save. An addon with metadata resources can be selected even when none of its catalogs are imported.
3. To replace existing addon fields, choose **Refresh selected fields**, select the desired categories (including **Images** for artwork), then refresh the relevant series/catalog or run **Full refresh**. Native locks and manually supplied images remain protected.
4. For missing season posters, optionally enable **Use TMDB only for missing season posters** and save a **TMDB Read Access Token**.

The selected addon supplies title details, parent artwork, credits, and episode metadata/artwork. If it is unavailable or returns unusable metadata, existing title data is retained: Siphon does not silently substitute Cinemata or another addon. **Automatic — catalog-origin addon first** restores automatic addon selection.

In selected-addon mode, direct TMDB enrichment is limited to missing season posters identified by explicit season numbers, including specials and sparse seasons. It does not replace existing season images, even during Full refresh, and does not supply movie, parent-series, or episode fields. Unnumbered `seasonPosters` arrays are not mapped by position: a missing special or skipped season would otherwise assign the wrong artwork. Missing child artwork is not copied from the series poster.

TVDB, Fanart, and MDBList are inactive in selected-addon mode; their saved settings remain intact. TMDB's response cache, pacing, quota pauses, and native image locks still apply to the optional season supplement.

### Optional metadata providers

In **Automatic** metadata-addon mode, **Metadata & providers** supports real, individually opt-in integrations:

- **TMDB**: a Read Access Token for titles, plots, dates, genres, credits, episode details, and artwork.
- **TVDB**: an API key and, when required by the subscription, a subscriber PIN for additional metadata.
- **Fanart**: a project API key for artwork.
- **MDBList**: an API key for aggregate ratings, normalized to Jellyfin's 0–10 scale.

Storing a key alone does not enable a provider. Saved credentials can be explicitly tested. Enabled providers run during synchronization using existing provider identities rather than guessing matches by title; unavailable or invalid responses do not erase prior metadata.

Leave the language blank to inherit Jellyfin's metadata language or choose a language such as `fr-FR`. **Fill missing fields only** is the default. **Refresh selected fields** updates only the selected categories. Native global and per-field metadata locks are respected, and a probed stream version keeps its actual runtime. In Automatic mode, TMDB supplies primary metadata, TVDB fills gaps, Fanart contributes artwork, and MDBList contributes aggregate ratings when available.

MDBList is queried only when a movie or parent-series rating can be updated under the selected policy and native locks. Existing parent ratings are consolidated across episodes before this decision; a missing episode rating does not cause an unnecessary parent-rating lookup.

Successful validated responses are cached on disk for **24 hours** by default; **Metadata cache lifetime** accepts 1–168 hours. Entries are keyed by the provider request and credentials, not unrelated provider switches or metadata merge preferences. Language-specific URLs remain separate, and a changed season episode set refreshes that season's response without discarding unrelated title or image-configuration responses. Concurrent requests recheck the cache after waiting for the provider. With usable cache storage, a forced run refreshes a shared response once and reuses it within that run; a subsequent forced run still refreshes it. Failed or invalid responses are not cached. Cache files are private, bounded, atomically replaced, and use hashed identities; protect the plugin data directory as well as its configuration.

Upgrading to this request-based cache layout repopulates older entries on demand, subject to provider limits. Existing native metadata is not erased.

Authentication failures and three consecutive transient failures temporarily pause the affected provider rather than repeatedly delaying the entire library. Other providers continue. Each provider has one in-flight request at most, with at least 150 ms between completed requests and the next upstream request; TVDB login uses the same gate as TVDB metadata.

Quota protection also inspects successful responses: `X-RateLimit-Remaining: 0` pauses further requests until the advertised Unix `X-RateLimit-Reset`. HTTP 429 respects `Retry-After` as seconds or an HTTP date, without resuming before a later exhausted-budget reset. When no usable deadline is supplied, Siphon pauses for five minutes rather than retrying immediately. Quota deadlines are stored separately from the evictable response cache, keyed by credential fingerprints, and survive restarts. Switching back to an exhausted credential restores its pause.

Provider status shows the pause reason and deadline, plus quota limit, remaining requests, reset time, and observation time when usable response headers supply them. These are **last-observed values**, not a live account balance: absent, invalid, or expired remaining-budget information is shown as unknown. Opening status does not make a provider request or refresh the observation timestamp. Credential changes isolate observations and deadlines.

Neither a forced refresh nor an explicit saved-credential test bypasses an active quota deadline; valid cached metadata remains available. Credential tests do not alter synchronization counters. Recovery is attempted on a later request after the deadline, not by a background retry loop.

[MDBList documents tier-specific daily quotas that reset at midnight UTC](https://api.mdblist.com/). Request pacing does not increase that daily allowance: a large catalog can require later synchronization runs after quota renewal, especially if other applications share the same key.

Synchronization reports progress through catalog retrieval, metadata enrichment, native item publication, and credit publication. Native credits are written in bounded, cancellable batches; repeated refreshes reuse person identities and leave unchanged credits alone.

Cast portraits are registered as on-demand native images, not downloaded as part of synchronization. Jellyfin retrieves and caches a portrait when a client displays it, through Siphon's signed image proxy. Existing native portraits and metadata locks are preserved, and synchronization does not wait for the entire cast's artwork to download.

Jellyfin stores these credentials in its plugin configuration. Masked fields are not encryption; protect configuration files and backups.

### Field-level metadata inspection

Under **Diagnostics**, enter a native item ID or Jellyfin details URL and choose **Inspect metadata**. The administrator-only report distinguishes managed and native fields, actual recorded contributing providers, observation/cache timestamps, native locks and preservation reasons. A native/manual difference is not attributed to a guessed editor. Missing historical evidence is shown as unknown, not retroactively assigned to whichever provider is currently enabled.

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

After a successful native opening, remux and seek requests carrying the same user, PlaySessionId and exact version reuse a private copy of that opening's source and probed tracks. They use the already-opened opaque HTTP lease, not a new addon discovery. Withdrawing a version from discovery therefore does not erase it from an existing authorized playback. Fresh PlaybackInfo requests still discover/open sources and do not re-advertise retired versions. Bindings expire six hours after opening, are limited to 1,024 snapshots of at most 1 MiB each, disappear with their proxy lease or relevant configuration invalidation, and are lost on restart. Native account/library permissions and source ownership are checked again on lookup. An unavailable upstream still fails; no different version is substituted.

Playback reports accept native source GUIDs with or without hyphens. When a client omits the source ID, the canonical item's metadata remains available without selecting another cut. Retired versions keep their own runtime for progress and stop reporting but return no playable path, opening token or playback capabilities, and remain absent from new playback choices. User ownership, content identity and version relationships are still checked. These reporting fixes do not diagnose arbitrary FFmpeg exit codes; a transcoding failure needs its corresponding FFmpeg log.

Native subtitle upload, provider download, and deletion work for Siphon's remote videos. Sidecars are stored under that version's private native metadata directory without changing its playable path or the library's subtitle-storage setting. Reopening or probing a source preserves its external subtitles; subtitles do not leak between versions.

Siphon fetches upstream resources from the Jellyfin server. Clients receive signed Siphon URLs; playback sessions use short-lived opaque capabilities. HTTP(S) progressive streams and HLS are supported, with P2P sources available only after explicit engine enablement. Unsupported protocols and external-player handoffs are rejected.

Private destinations are rejected by default. Add an exact trusted hostname under **Allowed private hosts** only when required, and never use wildcards or broad network ranges. See [SECURITY.md](SECURITY.md) for reporting security issues and the security boundaries.

The public base URL is for clients and must remain reachable by them. FFmpeg and ffprobe use Jellyfin's actual bound address, native HTTP port and configured BaseUrl inside the server/container, ignoring public URL overrides. HLS child references are relative so internal reads stay internal and public readers stay on their public route. No NAS IP, host-mapped port, public DNS override or private-host allowlist exception is required for this self-read. Addon/media destinations still use the normal SSRF policy. Other Jellyfin consumers, such as native image fetching, may still need access to the public base URL from the server.

Upstream connection and idle-read deadlines use the configured addon timeout; this does not limit a film's total playback duration. Private-host exceptions do not leave reusable approved connections after revocation. Media ranges preserve original bytes, and HLS resources are classified and rewritten even when an upstream URL has a misleading extension.

Ranged playback does not open a second upstream response while holding the requested media body unread. A sufficiently long byte-zero response supplies its own classification prefix; seeks and short ranges finish and dispose a separate prefix response first. HLS still passes through the playlist rewriter, and changed advertised representation validators or lengths are rejected rather than mixing responses. This addresses origins that refuse overlapping reads within a relay request; it does not serialize independent players or repair an unavailable origin.

Playback relay failures log only the failing stage, a fixed cause category, numeric upstream status when available, whether the client response started, and elapsed milliseconds. `classification-headers`/`classification-body`, `media-headers`/`media-prefix`/`media-body`, and playlist stages distinguish where a 502 or aborted transfer occurred. No source URL, capability, request header, exception message or upstream body is included in this diagnostic. A reproduced constrained-origin fix is not evidence that an uninstrumented failure on another server had the same cause.

Live-TV catalogs using Stremio's `tv` type are not imported. HTTP(S) support does not imply live-TV catalog support.

### Source diagnostics and selected-version downloads

In **My Siphon → My sources**, search native Jellyfin or enter an item ID/details URL. Diagnostics show your ordered versions, each addon's outcome and timing, rejection counts, and cache creation/expiry. **Refresh my item sources** invalidates only that title for your profile. It does not refresh all metadata or another user's cache. Upstream URLs, headers, private tracker data and addon credentials remain server-side.

Choose **Download this version** for a progressive or P2P source. The short-lived, download-scoped link supports `HEAD`, byte ranges and validators; current account, library and download permissions are checked again when it is used. It never contains the user's Jellyfin session token. Jellyfin's native download path also uses the selected managed version. This direct action streams the file immediately; use **Queue offline** for durable server preparation.

### Persistent private offline downloads

An administrator first enables **Private offline downloads** under **Playback & P2P**. A user with native Jellyfin download permission selects **Queue offline** on an exact version, then opens **Downloads** to inspect the phase, bytes, measured rate and any available remaining-time estimate; pause/resume, change priority, cancel, retry, delete or save a completed file. Estimates are withheld when the available totals cannot support them. Closing the portal does not cancel work. The queue is private to the authenticated account; even an administrator cannot select another user's bound native version or impersonate its queue with a `userId` query parameter.

Defaults are **2 concurrent jobs**, **51,200 MiB shared storage**, **20,480 MiB per user**, **10,240 MiB per job**, **20 retained jobs per user** and **7-day retention**. A job's budget includes temporary files and its result, not just the final video's size. HLS staging can therefore require substantially more space than the finished file. P2P also uses the independently bounded torrent cache. The portal shows the current user's storage and quota without exposing other users' files.

The private ledger stores stable native/source identities and configuration digests, not upstream URLs or request credentials. A worker re-resolves the exact selected source; disappearance or changed authority fails rather than silently selecting a different version. Interrupted running jobs return to the queue after restart; an individually paused job stays paused. HTTP appends only when a strong ETag, known total length and exact returned range identify the same representation; absent/changed validators safely restart. P2P partial reuse is bound to the exact torrent file. **HLS resumes by fresh staging:** previously downloaded segments are fetched again, not appended to an incomplete container.

Disabling the queue pauses work and withholds file access. Explicit cancellation survives restart; retry is deliberate. Current account, library, download rights and playback/transport configuration are rechecked during work and file reads. Cancelled writers stop before their reservations can be reused. Completed files use five-minute, in-memory capability links with `GET`, `HEAD` and byte-range support; links expire on restart and reject authenticated foreign users. Retention/delete operations touch only owned private artifacts.

Priorities range from **−10 to 10**. An optional **UTC download window** controls when background workers write; it does not remove access to already completed files. Batch preparation previews the available exact versions before explicit submission. Its user-bound confirmation expires after five minutes and can be consumed only once; preparation alone does not enqueue work.

### Finite HLS offline exports

**Queue offline** exports supported finite HLS to a real Matroska file using Jellyfin's configured **FFmpeg and FFprobe**. It selects one coherent video variant, preserves its matching audio renditions, assembles segmented WebVTT subtitle renditions with timestamp-map handling, and supports identity AES-128. Encoded audio/video are copied, not re-encoded. ADTS AAC receives a bounded fragmented-MP4 preparation pass when codec configuration is missing, so Matroska output remains strictly byte-bounded without needing a seekable child-process output.

Preparation exposes available audio/subtitle choices and conservative temporary-storage information before enqueueing. An omitted track selection preserves all supported matching tracks, an empty selection omits that track kind, and explicit selections are validated against the chosen source. Audio-only HLS is currently rejected with an explicit unsupported-format error; Siphon does not promise audio extraction or append-resume for HLS.

All playlists, segments and keys are fetched through Siphon's checked HTTP transport and staged under generated local filenames. Probe/remux inputs permit only local file/crypto protocols and restricted demuxer formats; every subprocess output is copied through the same storage budget. Cancellation, idle deadlines, quotas and process deadlines stop and reap owned children. Keep the native media tools patched; these restrictions are not an operating-system sandbox.

Limits are 4,096 HLS requests, 32 playlists, 128 parsed variants/renditions, 2 MiB per playlist, 256 MiB per resource, 48 hours of declared media duration, and 8 MiB/100,000 cues per subtitle rendition. Transfers have a 24-hour total deadline, reads a 60-second idle deadline, media preparation/remux processes a 30-minute deadline, and probing a one-minute deadline. Byte-range resources require a strong ETag and stable total length.

Live/EVENT/low-latency HLS, gaps, nested masters, alternate VIDEO groups, playlist variables/content steering, unsupported DRM/SAMPLE-AES and fragmented-MP4 subtitle renditions are rejected rather than silently omitted. Subtitle renditions must be WebVTT. A direct HLS **Download** still returns an explicit preparation-required response; it never saves a playlist as though it were an offline movie.

### Optional IntroDB timings

Enable IntroDB under **Playback & P2P** and select any of **intro**, **recap**, **outro** (end credits), and **post-credits**. Intro, recap and safe end-credit intervals become native media segments; post-credit scenes become navigable chapters, never skippable scenes. Known post-credit scenes are excluded from skip intervals even when their chapter display is disabled.

Invalid, unavailable or out-of-runtime timings are withheld. Existing native chapters and other segment providers are preserved. Timings are title-based and may not match every release/cut; Jellyfin clients decide which segment/skip controls to expose. Siphon does not replace the player or submit IntroDB timings.

### Opt-in P2P operation

**P2P is disabled by default.** Enable it only for content you are authorized to obtain **and share**. The MonoTorrent engine runs on the Jellyfin server: peers and trackers see its network identity, downloading can upload data, and BitTorrent wire encryption is not enabled. This is not anonymity, a VPN, or a privacy guarantee.

Supported addon sources provide an info hash, optional file index and tracker hints, or a supported magnet link. The chosen file is streamed through the same signed native version/download paths; seeking requests its required pieces. Clients are not redirected to magnets or local cache paths. Invalid metadata, unsafe paths, ambiguous file selection and exhausted budgets fail rather than silently selecting another file.

Defaults are **2 concurrent streams**, **20,480 MiB** cache reservation, **4,096 KiB/s download**, **256 KiB/s upload**, **5 minutes idle timeout**, and **90 seconds metadata timeout**. The settings page exposes these limits, listen-port selection, status and confirmed inactive-cache cleanup. Cache reservation covers torrent storage needs, not just a client's small requested range. Cancellation, disablement and session disposal release resources; this is not a permanent background seeding library.

Public DHT is enabled by default for trackerless sources once P2P itself is enabled, and can be disabled separately. Tracker-bearing sources disable DHT and peer exchange to preserve tracker scope. Private trackers and user credentials remain isolated. Private-network exceptions broaden reachable tracker/peer destinations and must be narrowly trusted. Containers and firewalls must permit the configured network paths; unavailable peers are reported as failures, not playable fallback sources.

## Building and testing

```bash
dotnet test Jellyfin.Plugin.Siphon.sln -c Release
python3 scripts/package.py
```

The build targets `net10.0` and Jellyfin ABI `12.1.0.0`. Release packaging verifies compiled version, PE assembly references, source provenance and runtime dependency bytes before producing `artifacts/siphon-1.6.5.0.zip`, a candidate repository manifest and internal `SHA256SUMS`. A stale binary, source/binary mismatch or incompatible ABI fails packaging. The tag for version 1.6.5.0 is `v1.6.5`; generating local artifacts does not publish another release.

Release automation publishes **only the plugin ZIP and a public `SHA256SUMS` covering that ZIP**. The candidate manifest and internal package checksums remain in workflow recovery artifacts, not as release attachments. New releases take their detailed notes from the **annotated Git tag**, not a file in the source tree. The annotation must begin with `## Siphon <four-component version>`, a blank line, then the complete release notes; the short `build.yaml` changelog remains the catalog summary. For example, `git tag -a v1.6.3 --cleanup=verbatim -F -` reads the Markdown annotation from standard input without adding a notes file to the repository. Include the changes, compatibility, installation and verification details; tag signatures are excluded from the release body.

The workflow uploads to a draft, checks immutable bytes, publishes, verifies public downloads, then opens or reuses a **manifest-only pull request from current main**. Retries preserve published assets and existing release notes; they never move main back to a historical tag. Do not advertise an unpublished local archive in the root manifest. Jellyfin uses the stable repository manifest URL, not a release attachment.

The ZIP includes the padded `siphon-jellyfin.png` catalog image, its `meta.json` declaration, MonoTorrent's runtime dependency closure and dependency licenses. It excludes Jellyfin's own SDK/server assemblies. The new 1280×720 image keeps the complete mark inside Jellyfin's card crop; the original `assets/siphon.png` and the image served at `/Siphon/Icon` remain unchanged. The repository manifest references `assets/siphon-jellyfin.png`.

### Owned Jellyfin runtime checks

The integration harness creates its own Docker network, Jellyfin instances, accounts and generated legal media. It exercises the actual ZIP, native playback/download routes, account isolation, queue restart, notification delivery, media segments and browser actions. The full scenario also installs the pinned published 1.4.1 binary before upgrading and checking retained native IDs, history and collections.

```bash
npm ci --prefix scripts/smoke --ignore-scripts --no-audit --no-fund
node scripts/smoke/node_modules/playwright/cli.js install chromium
python3 scripts/smoke/run.py full --image jellyfin/jellyfin:12.1 --package artifacts/siphon-1.6.5.0.zip
```

Docker must be running. Linux browser hosts also need Playwright's system dependencies; `--chromium-executable /absolute/path/to/chromium` selects an existing browser explicitly. Missing runtime prerequisites exit with code 77, not a passing result. The harness cleans its owned resources and exports credential-free evidence/screenshots; it neither reuses nor modifies personal Jellyfin services. These fixtures do not certify every addon, client, library size or HLS format.

## License

Siphon is distributed under the GNU General Public License v3.0. See [LICENSE](LICENSE).

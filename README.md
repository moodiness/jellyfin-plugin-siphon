# Siphon

<img src="assets/siphon.png" alt="Siphon" width="96" height="96">

Siphon brings **Stremio addons into Jellyfin**: catalogs, metadata, subtitles, and selectable stream versions through a server-side proxy and native Jellyfin libraries.

Siphon targets **Jellyfin 12.1** and .NET 10. The current source includes an opt-in, bounded MonoTorrent engine. It is not a debrid client or an external-player integration.

**Latest published release: 1.5.0.0. Prepared source version: 1.6.0.0.** The repository manifest advertises the published 1.5 archive. The newer 1.6 source is merged but is not yet released; merging source does not publish a release.

**Siphon 1.5.0.0** adds isolated per-user playback/subtitle addons and search preferences; durable native collections and playlists; catalog collections; selected-version resumable downloads; selective orphan-history recovery; field provenance and per-title source diagnostics; a followed-series calendar and opt-in notifications; all IntroDB timing types; and real, bounded P2P playback. It also includes native addon Search, targeted synchronization, a twenty-run decision history, provider quota/cooldown tracking, shared response caching, the supplied Siphon icon, and authoritative addon metadata with an optional missing-season-only TMDB supplement. The published package also validates media response types and constrains document rendering on media routes.

**Siphon 1.6.0.0 (not yet published)** adds a private, persistent server download queue, real finite-HLS offline exports, and opt-in signed notification webhooks. Both background downloads and external delivery are disabled by default. This candidate extends the published 1.5 functionality; it is not included in the published 1.5.0.0 package.

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
- Queue an exact native version for private server-side HTTP/P2P downloading or finite-HLS export, with restart recovery and bounded storage.
- Follow dated episodes in a personal calendar and receive opt-in, persistent release/date-change notifications.
- Deliver opted-in calendar events to a per-user webhook with a one-time signing secret, encrypted settings and durable bounded retries.
- Project IntroDB intro, recap, end-credit and post-credit timings into native segments and chapters.
- Inspect recorded field provenance and source outcomes, or preview and selectively restore proven orphaned Siphon history.
- Stream supported torrents through an opt-in engine with connection, cache, rate and lifetime limits.
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

1. For the prepared 1.6.0.0 source, build `siphon-1.6.0.0.zip` using the commands below or download the matching CI artifact. The intended release tag is `v1.6.0`; no download at that tag exists until publication. The current stable download is the [v1.5.0 release](https://github.com/moodiness/jellyfin-plugin-siphon/releases/tag/v1.5.0), containing `siphon-1.5.0.0.zip`.
2. Verify the archive against `SHA256SUMS`.
3. For 1.6.0.0, create a versioned folder named `Jellyfin.Plugin.Siphon_1.6.0.0` inside Jellyfin's plugin directory and extract the archive contents into that folder. The folder must contain `Jellyfin.Plugin.Siphon.dll` and `meta.json`. Use the matching versioned folder when installing an older release; do not leave two copies of the plugin installed.
4. Restart Jellyfin.
5. Configure Siphon from the plugin dashboard.

The plugin directory depends on the installation method. For Docker, mount a persistent host directory at `/config/plugins` and create the versioned plugin folder there. Extract the **entire packaged archive**, including its MonoTorrent dependencies and `licenses/` directory. Do not copy arbitrary build-output assemblies: Jellyfin supplies its own 12.1 server assemblies, which are deliberately excluded from the package.

## First configuration

1. Open **Dashboard → Siphon** in the **Plugins** sidebar section. The configuration page also remains accessible through **Plugins → My Plugins → Siphon**.
2. Under **Connection & limits**, set **Public Jellyfin base URL** to a URL reachable by Jellyfin and playback clients. Include Jellyfin's base path when one is configured.
3. Under **Addons**, review the preconfigured **Cinemata** addon. Open **Catalogs**, select the catalogs you want, and choose **Save changes**. Cinemata is enabled initially, but no catalog is imported until you select one.
4. Open **Tasks** and run **Sync catalogs**. This tab shows the current stage, completed/total counts, provider requests and cache hits, the last execution summary, and a **Stop sync** action.
5. Open **Dashboard → Libraries**: each selected catalog has a backing native library, named from its catalog and media type. The default **Library** presentation exposes its My Media entry and recently-added row; **Collection** and **Both** also publish a native collection.
6. If home was already open before the first import, fully reload the web page to refresh Jellyfin's cached library navigation.

Catalogs share the same canonical media identities rather than importing a separate copy for every row. Series contain native seasons and episodes. Siphon manages the backing directories under its private data directory; no manual Movies/TV Shows target path, `.strm` file, or NFO file is required.

Libraries are restored from saved state on startup without another addon fetch. Existing personal libraries are not adopted. Migration from previous Siphon library layouts retains canonical item IDs and user state, moves their descendants together, and transfers the old library's access and home preferences. A mixed library containing personal media retains its unrelated paths and permissions.

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

### Native search and saved additions

Jellyfin's ordinary global **Search** searches compatible enabled addons alongside accessible native library results. It does not require opening Siphon's settings. Only advertised search catalogs whose required filters can be satisfied are queried; catalog subscriptions are not required for discovery.

Search cards are lightweight previews. Displaying a card or fetching its image does not load full metadata. Opening a selected title loads that title's details and, for a series, its native seasons and episodes. Existing canonical items retain their identities instead of being duplicated. Search does not queue addon work behind synchronization; already available local results remain usable.

Administrators can also use **Siphon → Search & additions → Add to library** to retain a discovery outside any selected catalog. Adding the same title again is idempotent. Adding a discovery through Jellyfin's native collection or playlist actions also makes it durable, without changing its canonical ID or relaxing native permissions. A discovery favorited, played, or started by any user is retained by periodic maintenance. Saved additions survive synchronization and restarts in native **Siphon saved** libraries.

Unprotected previews expire after 24 hours and are limited to 300 titles. Preview libraries stay out of My Media and recently-added rows. Jellyfin's library visibility rules still apply to search results and detail expansion. If user protection data is unavailable, maintenance retains previews rather than risking removal of a favorite or resume item.

### User profiles and personal preferences

Under **Users & defaults**, select a Jellyfin user and choose inherited shared playback addons or an explicit replacement list. The replacement applies to both streams and addon subtitles. An **empty override means no providers**, never fallback to shared credentials. Deleting the override restores inheritance. Catalog synchronization, addon discovery/search catalogs, and the selected metadata addon remain shared administrative settings.

Users open **My Siphon** at `/Siphon/User` (under the server's configured base path) and sign in with their own Jellyfin account. The portal keeps its session token in memory, not in download URLs or browser storage; reloading requires another sign-in. It exposes personal preferences, calendar, notifications, webhook settings, source diagnostics and private downloads, not administrative configuration or addon secrets.

Search can inherit the server default, search **Local library only**, or include addon discovery. Unreleased filtering affects Siphon search results, including native fallback searches and hints, before pagination; it does not hide personal media. Dates are announced metadata, not evidence of a playable stream. Unknown release dates remain visible. The administrator can configure a release buffer of 0–30 days.

### Followed-series calendar and notifications

The personal **Release calendar** uses dated, accessible native episodes from series that the current user follows through favorites, watched episodes or playback progress. It does not substitute another user's follows or call addon providers just to display the calendar. Entries link to native Jellyfin episodes; an announced release is not a source-availability check.

Notifications require both the administrator's **Enable calendar notifications** setting and the user's opt-in. The portal inbox records newly discovered dated episodes, date changes, and announced releases. Initial opt-in establishes a baseline rather than sending an old backlog. Read state and deduplication survive restarts; inaccessible items are not shown. Storage is bounded, and unavailable/oversized observations are not treated as an empty calendar that would replay the backlog. This is a Siphon inbox, not a promise of push notifications from every Jellyfin client.

### Signed external notification webhooks

External delivery additionally requires the administrator's **Enable notification webhooks** setting and the user's saved webhook opt-in under **My Siphon → Notifications**. It sends calendar-event metadata, including series/episode titles and announced dates, to the destination the user chooses. This is not a browser-push or vendor-specific mobile-push integration.

Save the destination, generate a signing secret and copy it to the receiver, then use **Send test**. Secrets are shown only when generated; ordinary status reads never reveal them. Tests are explicit synthetic events, and a failed send is reported as failure. Tests are limited to one per user per minute and eight server-wide per minute. URLs require HTTPS unless an administrator permits the exact hostname; embedded credentials, fragments, redirects and disallowed private destinations are rejected.

Each POST carries `X-Siphon-Event-Id` (a UUID) and `X-Siphon-Signature` (`sha256=` followed by lowercase hexadecimal HMAC-SHA256). Base64-decode the generated secret to obtain the 32-byte key, verify the **exact received UTF-8 request body** with a constant-time comparison, and deduplicate by event ID. The version-1 JSON payload contains `Type`, `EventId`, `CreatedUtc`, `Test`, and, for `calendar.notification`, `Kind`, `Episode`, `PreviousAnnouncedReleaseUtc` and `AvailabilityNote`. Synthetic events use `Type: webhook.test` and `Test: true`. Reading an inbox item does not change a retry's payload.

Delivery is bounded, at-least-once rather than guaranteed: at most five attempts, with 30/120/480/1920-second backoffs and a 24-hour expiry. Network failures, HTTP 408/429 and 5xx are retryable; other HTTP failures are terminal. Pending state, attempts and event IDs survive restart. Setup, reconfiguration and secret rotation establish a baseline instead of sending an existing inbox backlog. Opt-out and lost item/account access stop further eligible delivery; an already-transmitted request cannot be recalled.

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

Native subtitle upload, provider download, and deletion work for Siphon's remote videos. Sidecars are stored under that version's private native metadata directory without changing its playable path or the library's subtitle-storage setting. Reopening or probing a source preserves its external subtitles; subtitles do not leak between versions.

Siphon fetches upstream resources from the Jellyfin server. Clients receive signed Siphon URLs; playback sessions use short-lived opaque capabilities. HTTP(S) progressive streams and HLS are supported, with P2P sources available only after explicit engine enablement. Unsupported protocols and external-player handoffs are rejected.

Private destinations are rejected by default. Add an exact trusted hostname under **Allowed private hosts** only when required, and never use wildcards or broad network ranges. See [SECURITY.md](SECURITY.md) for reporting security issues and the security boundaries.

The public base URL must also work **from inside the Jellyfin container**. A loopback address with a host-only forwarded port is not reachable at that port inside the container.

Upstream connection and idle-read deadlines use the configured addon timeout; this does not limit a film's total playback duration. Private-host exceptions do not leave reusable approved connections after revocation. Media ranges preserve original bytes, and HLS resources are classified and rewritten even when an upstream URL has a misleading extension.

Live-TV catalogs using Stremio's `tv` type are not imported. HTTP(S) support does not imply live-TV catalog support.

### Source diagnostics and selected-version downloads

In **My Siphon → My sources**, search native Jellyfin or enter an item ID/details URL. Diagnostics show your ordered versions, each addon's outcome and timing, rejection counts, and cache creation/expiry. **Refresh my item sources** invalidates only that title for your profile. It does not refresh all metadata or another user's cache. Upstream URLs, headers, private tracker data and addon credentials remain server-side.

Choose **Download this version** for a progressive or P2P source. The short-lived, download-scoped link supports `HEAD`, byte ranges and validators; current account, library and download permissions are checked again when it is used. It never contains the user's Jellyfin session token. Jellyfin's native download path also uses the selected managed version. This direct action streams the file immediately; use **Queue offline** for durable server preparation.

### Persistent private offline downloads

An administrator first enables **Private offline downloads** under **Playback & P2P**. A user with native Jellyfin download permission selects **Queue offline** on a specific version, then opens **Downloads** to follow progress, cancel, retry, delete or save a completed file. Closing the portal does not cancel work. The queue is private to the authenticated account; even an administrator cannot select another user's bound native version or impersonate its queue with a `userId` query parameter.

Defaults are **2 concurrent jobs**, **51,200 MiB shared storage**, **10,240 MiB per job**, **20 retained jobs per user** and **7-day retention**. A job's budget includes temporary files and its result, not just the final video's size. HLS staging can therefore require substantially more space than the finished file. P2P also uses the independently bounded torrent cache. The portal shows only the user's retained bytes against the shared server limit, not other users' files or free capacity.

The private ledger stores stable native/source identities and configuration digests, not upstream URLs or request credentials. A worker re-resolves the exact selected source; disappearance or changed authority fails rather than silently selecting a different version. Interrupted running jobs return to the queue after restart. HTTP appends only when a strong ETag, known total length and exact returned range identify the same representation; absent/changed validators safely restart. P2P partial reuse is bound to the exact torrent file. HLS restages rather than appending an incomplete container.

Disabling the queue pauses work and withholds file access. Explicit cancellation survives restart; retry is deliberate. Current account, library, download rights and playback/transport configuration are rechecked during work and file reads. Cancelled writers stop before their reservations can be reused. Completed files use five-minute, in-memory capability links with `GET`, `HEAD` and byte-range support; links expire on restart and reject authenticated foreign users. Retention/delete operations touch only owned private artifacts.

### Finite HLS offline exports

**Queue offline** exports supported finite HLS to a real Matroska file using Jellyfin's configured **FFmpeg and FFprobe**. It selects one coherent video variant, preserves its matching audio renditions, assembles segmented WebVTT subtitle renditions with timestamp-map handling, and supports identity AES-128. Encoded audio/video are copied, not re-encoded. ADTS AAC receives a bounded fragmented-MP4 preparation pass when codec configuration is missing, so Matroska output remains strictly byte-bounded without needing a seekable child-process output.

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

The build targets `net10.0` and Jellyfin ABI `12.1.0.0`. Release packaging produces `artifacts/siphon-1.6.0.0.zip`, a repository manifest, and `SHA256SUMS`. The intended release tag is `v1.6.0`; generating these files does not create a tag or publish a release. The release workflow publishes only the plugin ZIP and `SHA256SUMS`, and updates the root `manifest.json` in the repository. Until publication, do not replace the repository manifest with a local generated manifest: its 1.6.0.0 download URL is not live. Jellyfin should use the stable repository manifest URL above, not a release attachment.

The ZIP includes the supplied `siphon.png` icon, its `meta.json` declaration, MonoTorrent's runtime dependency closure, and dependency licenses. It deliberately excludes Jellyfin's own SDK/server assemblies. The plugin also serves the unchanged embedded image at `/Siphon/Icon`; the repository manifest references `assets/siphon.png`. Local source builds remain unreleased until an explicit versioned release is published.

## License

Siphon is distributed under the GNU General Public License v3.0. See [LICENSE](LICENSE).

# Security Policy

## Supported versions

| Version | Supported |
| --- | --- |
| 1.0.x, 1.1.x, 1.2.x, 1.3.x and 1.4.x / Jellyfin 12.1.x | Yes |
| 1.5.0.0 / Jellyfin 12.1.x | Yes |
| 1.6.0.0 / Jellyfin 12.1.x | Yes; latest published release |
| Other Jellyfin major versions | No |

Siphon is a Jellyfin plugin and must be installed only on a compatible Jellyfin server. Upgrade to the latest Siphon release compatible with your Jellyfin version before reporting an issue.

## Reporting a vulnerability

Do not open a public issue for an undisclosed security vulnerability.

Report vulnerabilities privately through GitHub's **Report a vulnerability** workflow on the repository's Security tab. Include:

- affected Siphon version and Jellyfin version;
- deployment type and relevant configuration shape, with credentials removed;
- reproducible steps or a minimal proof of concept;
- security impact and any required preconditions;
- logs or request examples after removing tokens, cookies, authorization headers, and upstream URLs containing secrets.

If GitHub private reporting is unavailable, contact the repository owner through the GitHub profile and ask for a private security contact. Do not send credentials, API keys, private addon URLs, or personal data in an initial report.

## Security boundaries

The current 1.6.0.0 release implements the following boundaries:

- upstream media and subtitle requests are made by the server, not directly by clients;
- clients receive opaque, expiring capability tokens rather than upstream URLs or addon headers;
- HTTP(S) media and HLS are supported; a separately opt-in, bounded MonoTorrent engine handles supported P2P sources. Debrid clients and external-player handoffs are not implemented;
- DNS resolution and redirects are checked against SSRF policy, including HTTPS downgrade and cross-origin credential handling;
- private destinations require exact hostname exceptions configured by an administrator; exception-approved connections are not pooled, so later requests cannot reuse them after an exception is removed;
- catalog media are native Jellyfin items, while Siphon keeps catalog state and backing directories in its private plugin data directory; personal library paths are preserved;
- addon responses, subtitles, playlists, headers, and proxy sessions are size- and concurrency-bounded; upstream body reads also have an idle deadline, without imposing a total playback-duration limit;
- media ranges retain their original byte representation; unexpectedly encoded responses are rejected, and HLS resources remain capability-proxied even behind misleading filenames;
- TMDB, TVDB, Fanart, and MDBList integrations are individually opt-in and use fixed HTTPS provider origins; storing a credential alone does not enable outbound enrichment, while testing saved credentials makes an explicit provider request;
- provider authentication POST bodies are bounded, and redirects cannot replay their credentials; provider failures are exposed as safe status codes rather than raw upstream responses;
- administrative diagnostics, field provenance, recovery, cleanup, targeted synchronization, run history and explicit search additions/removals require administrator elevation; removal changes shared ownership rather than a private user list, preserves other catalog ownership and retains native history for unambiguous later reattachment. Exported diagnostics omit credentials, private URLs, hosts and headers;
- native addon Search preserves Jellyfin library visibility; detail expansion checks the effective user's access and impersonation rules before addon I/O. Search may obtain missing posters through bounded title-metadata requests, but card/image requests do not publish series episodes;
- discoveries are bounded to 300 temporary titles with a 24-hour lifetime, and managed state is capped at 100,000 items before publication; explicit additions, deliberate collection/playlist membership and user-protected discoveries are retained, while unreadable protection data prevents preview removal;
- the latest twenty synchronization runs and their bounded title/action/reason records are stored privately rather than retaining upstream request or response payloads;
- selecting an authoritative metadata addon disables fallback to other addons; optional direct TMDB enrichment is then limited to missing numbered season posters, with native locks and existing images protected;
- provider quota observations come only from usable response headers, remain isolated by credential fingerprints, and are not refreshed by status reads; forced refreshes and credential tests do not bypass durable quota pauses;
- the self-connection diagnostic probes only the saved Jellyfin address, without credentials, cookies, redirects, or a proxy, and checks the returned server identity; its local-address access does not relax addon or media SSRF policy;
- missing-item cleanup requires confirmed complete catalog absence, applies a configurable grace period, and protects favorites and resume positions across users and native versions by default; deliberate collection/playlist membership remains protected. Deletion rechecks state and protections and refuses stale previews or unavailable protection data;
- optional provider credentials and potentially sensitive shared/per-user addon URLs are stored in Jellyfin's plugin configuration;
- administrator settings mask credentials in the interface, but this is not encryption of the configuration or its backups;
- the unauthenticated icon endpoint serves only the fixed embedded PNG with cache validators and does not expose configuration or fetch an upstream image;
- per-user overrides replace, rather than merge with, shared playback/subtitle addons; an empty override cannot fall back to global credentials. Catalogs and metadata remain shared, and the selected metadata addon stays authoritative;
- source caches, native version identities, subtitle tickets and download/playback capabilities are user-scoped. Current account/access/playback/download permissions and applicable profile changes are checked before protected upstream requests; a user's targeted refresh does not invalidate another user's source cache;
- download links are short-lived, purpose-scoped capabilities, not Jellyfin session tokens. Direct progressive/P2P downloads preserve ranges and validators; HLS requires the opt-in server queue rather than a misleading playlist download;
- the personal portal exposes authenticated personal preferences, permitted calendar/inbox entries, safe source diagnostics, private download jobs and the user's own webhook settings. Its session token is kept in memory; addon credentials and administrative settings are not returned;
- recovery previews do not change native user history. Execution revalidates selected row fingerprints, ownership, configuration and live-history collisions; original orphans and unselected users remain untouched. An IMDb key alone is not proof that an old orphan belonged to Siphon;
- metadata provenance reports recorded contributors and preservation decisions, not inferred provider success; unknown evidence and native/manual differences remain explicit;
- IntroDB is an independent, optional timing source. Invalid or out-of-runtime timings are withheld, post-credit scenes are never marked skippable, and other providers' segments/native chapters remain intact;
- calendar notifications require server enablement and individual opt-in. Durable, bounded observation/inbox state prevents restart backlogs; unavailable visibility data does not become an empty snapshot that would replay old events.
- durable downloads persist stable native identities, profile/transport digests and representation validators, never resolved URLs or request credentials. Exact-source re-resolution, live permission checks and authenticated foreign-ticket rejection protect restart and completed-file access; there is no fallback to another user's or another version's source;
- download concurrency, server/user reservations, job counts and retention are bounded. Temporary data and completed output share a per-job budget; interrupted/cancelled children are reaped before reservations are reused. Pauses and optional UTC windows control workers without relaxing file-access checks. Batch confirmations are expiring, single-use and user-bound. Filesystem cleanup requires ownership markers and rejects symlinks/unowned entries;
- offline HLS uses generated local paths, checked resource requests, restricted local-only FFmpeg/FFprobe inputs and bounded managed output. Live/unsupported/DRM playlists fail closed. Temporary clear media and AES keys reside in private staging, not an encrypted media vault; protect disk access and backups, and keep native media tools patched;
- external notifications require separate server and user opt-ins. JSON, Discord, Slack and ntfy adapters change the signed body format, not the destination/access policy. Event filters and optional digests include only currently permitted calendar events. Endpoints follow SSRF/HTTPS policy and never follow redirects; an already-transmitted payload cannot be recalled after revocation;
- webhook configuration, signing secrets and durable retry state are authenticated-encrypted with AES-GCM and a domain-separated signing-key derivative. This protects a copied state file without its key, not against an administrator or a backup containing both. Receivers must verify the exact-body HMAC and deduplicate stable event IDs; bounded retries are not exactly-once or guaranteed delivery.

### Offline backup boundary

`scripts/backup.py` is a local, offline full-tree snapshot utility, not an authenticated remote API or a live database copier. See the README's **Offline backup and restore** section for commands and the stopped-container procedure. The complete selected Jellyfin program-data/configuration trees include native identities and history together with Siphon's state, original signing key, ownership ledgers, managed paths and private stores. A configuration-only copy cannot preserve those relationships.

The operator must stop the instance and disable restarters, then explicitly acknowledge that state. Conventional local POSIX SQLite byte-range locks, refusal of all WAL/SHM/journal sidecars, source-stability checks and integrity checks on copied databases are additional controls; they do not establish the absence of every process or external writer. Never remove recovery sidecars manually to bypass a refusal. Network filesystems and unsupported link/ownership/layout semantics are outside the supported boundary.

Archives are private but **not encrypted or cryptographically signed**. They contain credentials, session/native-user data, the signing key and encrypted webhook state together, so webhook encryption does not protect the archive holder. Private temporary SQLite copies and restore staging share this sensitivity. Ordinary output contains only controlled status/version/count/checksum fields; do not attach archives, manifests, configuration XML or private state to public issues. Preserve the archive's SHA-256 separately in trusted storage and supply it with `--sha256`; an embedded checksum is not authenticity evidence.

Restore validates strict archive members, paths, owner allowlists, size limits, required native/Siphon records, checksums and copied SQLite integrity before restoring into private staging. Links, duplicate/case-colliding members, special files/permissions, unsupported snapshot versions and installed plugin identity/version mismatches fail closed. Numeric ownership and ordinary permissions are preserved; only explicitly approved owner pairs are accepted. Restore publishes only to an absent or empty destination, never over a populated tree or newer native history. It does not rewrite stored paths, migrate database versions, rotate signing keys or merge user history. Reuse original runtime mount paths and exact software versions, verify the stopped restored tree, and retain the original instance separately.

The utility assumes a trusted local administrator and a filesystem honoring local POSIX locking and rename semantics; it is not isolation from a malicious same-privilege process. Handled interruption cleans incomplete private staging. Uncatchable termination or power loss can leave private temporary entries for operator cleanup, but incomplete archives/staging are not published as successful results.

### P2P-specific risks

P2P is disabled by default. Enabling it makes the **Jellyfin server** a BitTorrent participant: peers and trackers can observe its IP and torrent participation, transfers may upload data, and BitTorrent wire encryption is not enabled. It is not a VPN or anonymity layer. Use only content you have the right to obtain and share.

The engine bounds concurrent sessions, torrent metadata, reserved cache storage, transfer rates and waiting/idle lifetimes. It rejects unsafe filesystem paths and symlinks, unsupported or ambiguous file selections, and disallowed network destinations. Tracker/peer access is subject to destination checks; private-network allowlist entries deliberately widen that boundary. Explicit-tracker sources do not use DHT or peer exchange; trackerless public DHT is separately configurable.

Torrent/private-tracker credentials, metadata and caches remain server-side and are isolated per user. Session cancellation, disablement and disposal release reservations and stop owned transfers; inactive-cache cleanup does not delete unrelated server files. Resource limits and endpoint validation reduce abuse, but do not make untrusted swarm data or private-network exceptions harmless.

These controls do not make an untrusted Jellyfin administrator trustworthy. Limit administrator access, protect Jellyfin configuration backups, and review private-host exceptions.

## Disclosure

Please allow reasonable time for investigation and coordinated remediation before public disclosure. Security fixes may be released as a new plugin version and documented in the release notes.

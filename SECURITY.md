# Security Policy

## Supported versions

| Version | Supported |
| --- | --- |
| 1.0.x, 1.1.x, 1.2.x and 1.3.x / Jellyfin 12.1.x | Yes |
| 1.4.x source builds / Jellyfin 12.1.x | Yes (not yet released) |
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

The current source implements the following boundaries. Unreleased changes documented in the README are not yet part of the published archive:

- upstream media and subtitle requests are made by the server, not directly by clients;
- clients receive opaque, expiring capability tokens rather than upstream URLs or addon headers;
- HTTP(S) only; torrent engines, debrid services, and external-player handoffs are not implemented;
- DNS resolution and redirects are checked against SSRF policy, including HTTPS downgrade and cross-origin credential handling;
- private destinations require exact hostname exceptions configured by an administrator; exception-approved connections are not pooled, so later requests cannot reuse them after an exception is removed;
- catalog media are native Jellyfin items, while Siphon keeps catalog state and backing directories in its private plugin data directory; personal library paths are preserved;
- addon responses, subtitles, playlists, headers, and proxy sessions are size- and concurrency-bounded; upstream body reads also have an idle deadline, without imposing a total playback-duration limit;
- media ranges retain their original byte representation; unexpectedly encoded responses are rejected, and HLS resources remain capability-proxied even behind misleading filenames;
- TMDB, TVDB, Fanart, and MDBList integrations are individually opt-in and use fixed HTTPS provider origins; storing a credential alone does not enable outbound enrichment, while testing saved credentials makes an explicit provider request;
- provider authentication POST bodies are bounded, and redirects cannot replay their credentials; provider failures are exposed as safe status codes rather than raw upstream responses;
- diagnostics and cleanup endpoints require administrator elevation; exported diagnostics omit credentials, private URLs, hosts, and headers;
- the self-connection diagnostic probes only the saved Jellyfin address, without credentials, cookies, redirects, or a proxy, and checks the returned server identity; its local-address access does not relax addon or media SSRF policy;
- missing-item cleanup requires confirmed complete catalog absence, applies a configurable grace period, and protects favorites and resume positions across users and native versions by default; deletion rechecks state and protections and refuses stale previews or unavailable protection data;
- optional provider credentials and potentially sensitive addon URLs are stored in Jellyfin's plugin configuration;
- administrator settings mask credentials in the interface, but this is not encryption of the configuration or its backups.

These controls do not make an untrusted Jellyfin administrator trustworthy. Limit administrator access, protect Jellyfin configuration backups, and review private-host exceptions.

## Disclosure

Please allow reasonable time for investigation and coordinated remediation before public disclosure. Security fixes may be released as a new plugin version and documented in the release notes.

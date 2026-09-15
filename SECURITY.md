# Security Policy

## Supported versions

| Version | Supported |
| --- | --- |
| 1.0.x and 1.1.x / Jellyfin 12.1.x | Yes |
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

Siphon is designed around these boundaries:

- upstream media and subtitle requests are made by the server, not directly by clients;
- clients receive opaque, expiring capability tokens rather than upstream URLs or addon headers;
- HTTP(S) only; torrent engines, debrid services, and external-player handoffs are not implemented;
- DNS resolution and redirects are checked against SSRF policy, including HTTPS downgrade and cross-origin credential handling;
- private destinations require exact hostname exceptions configured by an administrator;
- channel items are persisted by Jellyfin while Siphon keeps catalog state in its private plugin data directory; Siphon does not manage user media files;
- addon responses, subtitles, playlists, headers, and proxy sessions are size- and concurrency-bounded;
- Siphon does not store provider API keys or debrid credentials.

These controls do not make an untrusted Jellyfin administrator trustworthy. Limit administrator access, protect Jellyfin configuration backups, and review private-host exceptions.

## Disclosure

Please allow reasonable time for investigation and coordinated remediation before public disclosure. Security fixes may be released as a new plugin version and documented in the release notes.

# Siphon

Siphon brings **Stremio addons into Jellyfin**: catalogs, metadata, subtitles, and playback through a server-side proxy.

Siphon targets **Jellyfin 12.1** and .NET 10. It does not include a torrent engine, debrid client, or external-player integration.

## Features

- Import Stremio movie and series catalogs, including mixed catalogs.
- Expand series metadata into Jellyfin seasons and episodes.
- Preserve IMDb, TMDB, TVDB, MyAnimeList, and addon-specific identities.
- Write Jellyfin-compatible NFO metadata and local artwork.
- Discover addons advertised through Stremio `addon_catalog` resources.
- Search and download HTTP(S) subtitles exposed by enabled addons.
- Proxy HTTP(S) progressive media and HLS server-side without exposing upstream URLs or addon headers to players.
- Keep managed files isolated in separate Movies and TV Shows library roots.
- Enable the official Cinemeta addon by default as a removable example configuration. It has no catalog subscriptions selected by default.

## Requirements

- Jellyfin Server **12.1.x**.
- A server runtime supported by Jellyfin 12.1.
- .NET 10 is required to build Siphon from source.
- Two empty, separate directories for Siphon-managed Movies and TV Shows. Each directory must be registered as a folder location in an existing Jellyfin library of the matching type.

## Installation from the Jellyfin plugin repository

1. Install Jellyfin Server 12.1.x.
2. Open **Dashboard → Plugins → Repositories**.
3. Add this repository manifest URL:

   ```text
   https://github.com/moodiness/jellyfin-plugin-siphon/releases/download/v1.0.0/manifest.json
   ```

4. Open **Dashboard → Plugins → Catalog**, find **Siphon**, and install it.
5. Restart Jellyfin.
6. Open the Siphon configuration page from **Dashboard → Plugins → My Plugins → Siphon**.

The repository manifest points to the release archive and includes its Jellyfin ABI and checksum. Install only the version compatible with your Jellyfin major release.

## Manual installation

1. Download `siphon-1.0.0.0.zip` from the [v1.0.0 release](https://github.com/moodiness/jellyfin-plugin-siphon/releases/tag/v1.0.0).
2. Verify the archive against `SHA256SUMS`.
3. Create a versioned folder named `Jellyfin.Plugin.Siphon_1.0.0` inside Jellyfin's plugin directory and extract the archive contents into that folder. The folder must contain `Jellyfin.Plugin.Siphon.dll` and `meta.json`.
4. Restart Jellyfin.
5. Configure Siphon from the plugin dashboard.

The plugin directory depends on the installation method. For Docker, mount a persistent host directory at `/config/plugins` and create the versioned plugin folder there. Do not copy the build output's dependency assemblies into the plugin directory; Jellyfin supplies its own 12.1 assemblies.

## First configuration

1. Create separate empty directories, for example:

   ```text
   /srv/jellyfin/siphon/movies
   /srv/jellyfin/siphon/tv
   ```

2. Add the Movies directory to an existing Jellyfin Movies library and the TV directory to an existing Jellyfin Shows library.
3. Set **Public Jellyfin base URL** to a URL reachable by both Jellyfin and playback clients. Include Jellyfin's base path when one is configured.
4. Set the Movies and TV Shows root paths using the paths as seen by the Jellyfin server/container.
5. Review the preconfigured **Cinemeta (official)** addon. It is enabled, but no catalog is imported until you select one.
6. Enable the catalogs you want and save the configuration.
7. Run **Synchronize saved catalogs**.

Siphon writes only files under owned roots marked with `.siphon-owned`. It refuses to overwrite or remove files that are not owned by Siphon.

## Cinemeta and addon management

Cinemeta is included so a new installation immediately demonstrates the catalog and metadata workflow. It can be:

- disabled with **Enable addon**;
- configured by selecting one or more catalogs;
- removed with **Remove addon** and then **Save configuration**;
- re-added later using its manifest URL:

```text
stremio://v3-cinemeta.strem.io/manifest.json
```

Addon manifest URLs are stored in Jellyfin's plugin configuration. Protect configuration backups when URLs contain credentials.

## Playback and security model

Siphon fetches upstream resources from the Jellyfin server. Clients receive only Siphon URLs with short-lived opaque capabilities. HTTP(S) progressive streams and HLS are supported; torrent URLs and unsupported protocols are rejected.

Private destinations are rejected by default. Add an exact trusted hostname under **Allowed private hosts** only when required, and never use wildcards or broad network ranges. See [SECURITY.md](SECURITY.md) for reporting security issues and the security boundaries.

## Building and testing

```bash
dotnet test Jellyfin.Plugin.Siphon.sln -c Release
python3 scripts/package.py
```

The build targets `net10.0` and Jellyfin ABI `12.1.0.0`. Release packaging produces the plugin archive, repository manifest, and SHA-256 checksum file in `artifacts/`.

## License

Siphon is distributed under the GNU General Public License v3.0. See [LICENSE](LICENSE).

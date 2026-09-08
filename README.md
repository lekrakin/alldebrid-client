# AllDebrid Client

[![CI](https://github.com/krakn-dev/alldebrid-client/actions/workflows/ci.yml/badge.svg)](https://github.com/krakn-dev/alldebrid-client/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/krakn-dev/alldebrid-client)](https://github.com/krakn-dev/alldebrid-client/releases/latest)
[![Container image](https://img.shields.io/badge/container-ghcr.io-blue?logo=docker)](https://github.com/krakn-dev/alldebrid-client/pkgs/container/alldebrid-client)
[![Docker pulls](https://img.shields.io/docker/pulls/krakal/alldebrid-client)](https://hub.docker.com/r/krakal/alldebrid-client)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

AllDebrid Client is a self-hosted web application for sending torrents to [AllDebrid](https://alldebrid.com), downloading completed files to local storage, and exposing the [qBittorrent](https://www.qbittorrent.org/) Web API surface used by [Sonarr](https://sonarr.tv/), [Radarr](https://radarr.video/), and [Logpose](https://github.com/jasanpreetn9/logpose).

This is an independent community project and is not affiliated with AllDebrid or qBittorrent. An AllDebrid account and API key are required.

## Features

- Add magnet links and `.torrent` files from the web interface, a watch folder, or a compatible application.
- Filter files by size or path, set priorities, and control retry and retention behavior.
- Download completed content to the host with bounded parallel transfers.
- Connect Sonarr, Radarr, and Logpose through their normal qBittorrent configuration.
- Run as a Docker container, Windows service, or framework-dependent .NET application.

## Quick start with Docker

Save this as `compose.yaml`:

```yaml
services:
  alldebrid-client:
    image: krakal/alldebrid-client:latest
    container_name: alldebrid-client
    environment:
      PUID: 1000
      PGID: 1000
      TZ: Etc/UTC
    volumes:
      - ./data/db:/data/db
      - ./data/downloads:/data/downloads
    ports:
      - "6500:6500"
    restart: unless-stopped
```

Run `docker compose up -d`, then open `http://<host>:6500`. This uses the published Docker Hub image; building from source is not required.

No login is required under the **No Authentication** default. Add the AllDebrid API key under **Settings → AllDebrid**, then review **Downloads** and **Storage** before adding a torrent. To enable username-and-password authentication, first save both credentials under **Settings → Account**, then select **General → Authentication → Username + Password** and sign in.

> [!IMPORTANT]
> Authentication is disabled by default. Enable it before exposing the application beyond a trusted network.

See the [Docker guide](docs/docker.md) for health checks, updates, release tags, digest pinning, and local source builds. Windows releases include a verified service updater; Windows and native Linux instructions are in the [installation guide](docs/installation.md).

## Integrations

AllDebrid Client provides a focused qBittorrent-compatible API for download-client integrations; it is not a general qBittorrent daemon or replacement Web UI.

| Application | Connection method           | Recommended category |
| ----------- | --------------------------- | -------------------- |
| Sonarr      | qBittorrent download client | `sonarr`             |
| Radarr      | qBittorrent download client | `radarr`             |
| Logpose     | qBittorrent configuration   | Created by Logpose   |

Integrated jobs inherit the same **Downloads** defaults used elsewhere in AllDebrid Client. Fresh Docker installations use `/data/downloads`; fresh Windows installations use `C:\ProgramData\AllDebridClient\downloads`. Mount the same download directory at the same path in cooperating containers and no Remote Path Mapping is needed. **Integrations → Client-visible download path** is an advanced reporting override for mixed native/container path namespaces; it does not create a second download location.

Follow the [integration guide](docs/integrations.md) for exact settings, container path examples, and deletion and retention behavior.

## Documentation

- [Installation and updates](docs/installation.md)
- [Configuration and paths](docs/configuration.md)
- [Docker](docs/docker.md)
- [Sonarr, Radarr, and Logpose](docs/integrations.md)
- [Contributing and local development](CONTRIBUTING.md)
- [Changelog](CHANGELOG.md)
- [Security policy](SECURITY.md)

## Support

Use [GitHub Issues](https://github.com/krakn-dev/alldebrid-client/issues) for reproducible bugs. Remove API keys, credentials, private links, and local personal data from logs before posting them. Report vulnerabilities privately as described in the [security policy](SECURITY.md).

## License

AllDebrid Client is distributed under the [MIT License](LICENSE).

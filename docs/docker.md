# Docker

The release workflow publishes multi-platform images to [GitHub Container Registry](https://github.com/krakn-dev/alldebrid-client/pkgs/container/alldebrid-client) as `ghcr.io/krakn-dev/alldebrid-client` and [Docker Hub](https://hub.docker.com/r/krakal/alldebrid-client) as `krakal/alldebrid-client` for `linux/amd64` and `linux/arm64`. Both registries receive the same tags from the same release build.

## Docker Compose

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
    healthcheck:
      test: ["CMD", "curl", "--fail", "http://localhost:6500/health"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 60s
```

Run `docker compose up -d`, then open `http://<host>:6500`.

> [!IMPORTANT]
> Authentication is disabled by default. Bind the published port only to a trusted network, or create an account and enable username-and-password authentication before exposing it more broadly.

`PUID` and `PGID` should identify the host user that owns the mounted directories. Set `TZ` to an [IANA time zone](https://en.wikipedia.org/wiki/List_of_tz_database_time_zones), such as `America/New_York`.

Keep the container-side application port at `6500`; change only the host side of the mapping when another host port is needed, for example `"8080:6500"`. A custom internal port also requires matching port and health-check overrides.

## Persistent storage

| Container path    | Purpose                             |
| ----------------- | ----------------------------------- |
| `/data/db`        | SQLite database, settings, and logs |
| `/data/downloads` | Downloaded files                    |

Both paths must use persistent mounts. `/data/downloads` is also the application's default local download path in Docker. Back up `/data/db` before replacing or migrating an installation.

Startup and runtime configuration are separate. The image sets `DataPath=/data/db`; application behavior is configured in the web interface and persisted in the database. See [Configuration](configuration.md) for the complete settings layout.

For integrations running in containers, mount the same host download directory as `/data/downloads` in every container. The shared path lets Sonarr and Radarr import files directly without a Remote Path Mapping. See the [integration guide](integrations.md) for mixed native/container installations.

## Updating

Update a registry-backed Compose installation with:

```bash
docker compose pull
docker compose up -d
```

Successful release builds publish full-version tags plus rolling major, minor, and `latest` tags. Pin an image digest, rather than a mutable tag, when an exactly reproducible deployment is required.

Existing Docker Hub installations can continue using `krakal/alldebrid-client` without changing their Compose file.

Every successfully published release image is built from its matching Git tag and includes provenance and a software bill of materials.

## Local source build

From the repository root:

```bash
docker compose -f tools/docker-compose.yml up -d --build
```

On Windows, the project launcher provides the same operation:

```powershell
.\dev.ps1 docker
```

The development Compose file stores data under the ignored `data/` directory in the repository and tags the image as `alldebrid-client:local`.

To rebuild without Docker's layer cache:

```powershell
.\dev.ps1 docker -SkipCache
```

## Docker CLI

Compose is recommended because it records the complete configuration. The equivalent direct commands are:

```bash
docker build -t alldebrid-client:local .

docker run -d \
  --name alldebrid-client \
  -e PUID=1000 \
  -e PGID=1000 \
  -e TZ=Etc/UTC \
  -p 6500:6500 \
  -v ./data/db:/data/db \
  -v ./data/downloads:/data/downloads \
  --restart unless-stopped \
  alldebrid-client:local
```

# Configuration

AllDebrid Client has two configuration layers:

- **Startup configuration** controls where the application stores its database and logs, which port it listens on, and whether it is hosted below a URL base path. Set these values before starting the process or container.
- **Runtime settings** control downloads, AllDebrid behavior, integrations, and watch-folder imports. Change these in the web interface under **Settings**.

Runtime settings are stored in the application database. They do not belong in `appsettings.json`, and updating the application preserves them when the data directory is persistent.

The Settings page groups related controls without renaming their stored identifiers. Active setting identifiers from version 1.6.0 are retained in the database and settings API.

For a standard Docker or Windows installation, leave the startup defaults in place and complete configuration in the web interface.

## Startup configuration

Native installations read startup values from `appsettings.json` in the application directory. Docker installations normally use the image defaults shown below; environment variables can override the same keys. Restart the application after changing a startup value.

| Key        | Purpose                                                     | Windows release default          | Docker default |
| ---------- | ----------------------------------------------------------- | -------------------------------- | -------------- |
| `DataPath` | Persistent application database and default log location    | `C:\ProgramData\AllDebridClient` | `/data/db`     |
| `Port`     | HTTP listening port                                         | `6500`                           | `6500`         |
| `BasePath` | Optional URL path segment when hosted below a reverse proxy | Blank                            | Blank          |

Use an absolute, writable `DataPath` outside the application directory. This keeps the database and logs separate from replaceable application files and allows the Windows updater to preserve them. In Docker, persist `/data/db`; changing `DataPath` is normally unnecessary.

`BasePath` is one or more URL-safe path segments, such as `alldebrid` or `services/alldebrid`, without a scheme or host name. When it is set, open the application below that path and use the same value as the URL base in connected applications.

The supplied container and its built-in health check use internal port `6500`. To expose another host port, change only the host side of the Docker mapping, such as `8080:6500`. If you override the container's internal `Port`, also update its port mapping and health check.

Advanced installations can set `Database:Path` and `Logging:File:Path`. When unset, these default to `adbclient.db` and `adbclient.log` beneath `DataPath`. Explicit relative overrides, like relative `DataPath` values, retain their version 1.6.0 meaning: they resolve against the process working directory, not the application directory or `DataPath`. The database and log must resolve to different files. Nested environment-variable keys use double underscores, such as `Database__Path`. `Logging:File:FileSizeLimitBytes` and `Logging:File:MaxRollingFiles` override log rotation limits. Most installations should leave these overrides unset.

Before replacing an installation with custom relative startup paths, set those values to the absolute locations of its **existing** data. The Windows updater and source deployment/publish scripts refuse ambiguous relative persistent paths rather than guess where an existing database resides. This does not move any files. Standard Windows and Docker defaults already use absolute paths and require no change.

## Runtime settings

The Settings page is organized by responsibility:

| Group            | What it controls                                                                                         |
| ---------------- | -------------------------------------------------------------------------------------------------------- |
| **General**      | Log level, application authentication, and update notifications                                          |
| **Downloads**    | Transfer concurrency, speed and connection limits, plus defaults copied to each new torrent              |
| **Storage**      | The physical local directory where downloaded payloads are written                                       |
| **AllDebrid**    | Provider credentials, polling, synchronization, queue limits, and tracker safeguards                     |
| **Integrations** | qBittorrent categories, the path reported to external clients, and optional metadata or completion hooks |
| **Watch Folder** | Optional `.torrent` and `.magnet` inbox processing                                                       |
| **Account**      | Initial or replacement credentials for username-and-password authentication                              |

Settings under **Downloads → New torrent defaults** are copied when a torrent is added through the web interface, provider synchronization, the watch folder, or the qBittorrent API. Changing a default does not rewrite an existing job.

To enable username-and-password authentication, save both fields under **Account** while **General → Authentication** is still set to **No Authentication**. Then enable username-and-password authentication and sign in. Existing accounts can change either field independently.

Host-local runtime paths may be absolute or relative to the application directory; relative values are normalized when saved. **Client-visible download path** is reported to another application and is therefore kept in that application's path syntax rather than resolved on the AllDebrid Client host.

When **Concurrent extractions** is greater than zero, downloaded `.zip` and `.rar` files are staging payloads: after a successful extraction, the archive is removed and the extracted files remain. Set the value to `0` to retain archives without extracting them.

The watch folder is an optional ingestion method. Sonarr, Radarr, and Logpose connect directly through the qBittorrent-compatible API and do not need it.

## Download paths

Three paths serve different purposes:

| Path                                            | Meaning                                                                                                                                                                      |
| ----------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `DataPath`                                      | Application state: database and logs. It is not the media download directory.                                                                                                |
| **Storage → Local download path**               | Physical directory where AllDebrid Client writes payloads. A job category becomes a subdirectory, such as `<download path>/radarr`.                                          |
| **Integrations → Client-visible download path** | Optional path returned through the qBittorrent API when another application sees the same physical directory under a different name. It does not move, copy, or mount files. |

Leave **Client-visible download path** blank when AllDebrid Client and the importing application can both access the local download path by the same name. This is common when both are native applications on one host, or when all containers mount the same host directory at `/data/downloads`. No Sonarr or Radarr Remote Path Mapping is needed in that layout.

When AllDebrid Client runs natively on Windows and Sonarr or Radarr runs in Docker, mount the local download directory into the container and report that container path:

- **Local download path:** `D:\Media\Downloads`
- Sonarr or Radarr volume: `D:\Media\Downloads:/data/downloads`
- **Client-visible download path:** `/data/downloads`

The qBittorrent API then reports `/data/downloads/radarr` or `/data/downloads/sonarr`, which the importing container can read directly. No Remote Path Mapping is needed.

A Remote Path Mapping is required only when the importing application cannot access the directory under the path reported by AllDebrid Client. In that case, map the reported path to the importing application's path for the same physical directory. Remote Path Mapping translates a path; it does not transfer files.

Each job captures its physical download path when it is added, so changing **Local download path** never retargets an existing transfer or relocates existing files. Correcting **Client-visible download path** updates how existing jobs on the current physical root are reported; jobs captured on an older physical root retain their original mapping.

## Records and downloaded files

**Downloads → New torrent defaults → Completed record action** runs automatically after a job completes and its configured delay elapses. It controls whether AllDebrid Client and AllDebrid records are retained; the automatic action never deletes downloaded files. For Sonarr, Radarr, and Logpose, **No Action** is the safest default. An action that removes the client record needs enough delay for the importing application to observe and import the completed job.

**AllDebrid → Remove missing provider records** removes the matching AllDebrid Client record when a provider torrent disappears. It does not delete downloaded files.

An external qBittorrent client can separately request source-file removal after a successful import:

- `deleteFiles=false` leaves the downloaded payload in place.
- `deleteFiles=true` requests removal of the job's local payload.

With **Completed record action** set to **No Action**, the AllDebrid Client record remains visible with its original category and path even after an external qBittorrent client removes the job from its own view. Other record actions remove only the client and/or provider records named by the setting.

Earlier builds represented a retained external deletion by appending `-retained` to the job category. Existing rows already changed that way are left untouched because the suffix can also be a legitimate category; review or rename those historical records manually if needed.

Sonarr and Radarr normally hardlink imported torrent payloads when the download and library locations are on the same filesystem. If hardlinking is unavailable, they copy the payload. Their completed-download removal setting determines whether they later ask the download client to remove the source; it is independent of AllDebrid Client's record-retention setting.

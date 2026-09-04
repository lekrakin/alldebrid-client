# Sonarr, Radarr, and Logpose

AllDebrid Client implements the qBittorrent Web API surface required by Sonarr, Radarr, and Logpose. It accepts magnets and uploaded `.torrent` files, reports progress and paths, and supports the category operations these applications use. It is not a general qBittorrent daemon or replacement Web UI.

## Shared AllDebrid Client settings

Review these settings before connecting another application:

- **Storage → Local download path** is the physical directory where AllDebrid Client writes files.
- **Integrations → Client-visible download path** is an advanced override for the path returned through the qBittorrent API. Leave it blank unless another application sees the same physical directory at a different path.
- **Downloads → New torrent defaults → Host download action** must download files to the host when another application needs to import them.
- **Downloads → New torrent defaults → Completed record action** should normally be **No Action** for integrations. It runs automatically after completion; an action that removes the client record needs enough delay for the importing application to observe and import the job. It does not prevent an external client from explicitly requesting removal of an imported local payload.

Jobs added through the qBittorrent API inherit the regular exposed download defaults at creation, including filters, retries, priority, host download action, and record retention. Each category receives its own directory under the local download path. Direct integrations do not use the watch folder and do not create loose magnet or `.torrent` files unless **Integrations → Copy added torrent metadata to** is configured.

Use the AllDebrid Client login when username and password authentication is enabled. Leave client credentials blank when authentication is disabled.

## Paths and containers

Use the same download path in every container whenever possible. For example, mount the same host directory as `/data/downloads` in AllDebrid Client, Sonarr, and Radarr, then leave **Client-visible download path** blank. No Remote Path Mapping is needed.

For a native Windows AllDebrid Client used by containerized Sonarr or Radarr, mount the Windows download directory into the other container at `/data/downloads` and set **Client-visible download path** to `/data/downloads`. This keeps the path reported by AllDebrid Client identical to the path the container can read, so no Remote Path Mapping is needed there either.

For example:

- AllDebrid Client **Local download path**: `D:\Media\Downloads`
- Sonarr or Radarr volume: `D:\Media\Downloads:/data/downloads`
- AllDebrid Client **Client-visible download path**: `/data/downloads`

Remote Path Mapping is only necessary when the importing application cannot access the directory under the path AllDebrid Client reports. Map that reported path to the importing application's path for the same physical directory. It translates paths; it does not transfer files. The [configuration guide](configuration.md#download-paths) covers the path namespaces in more detail.

## Sonarr and Radarr

Add AllDebrid Client as a qBittorrent download client with the following values:

| Setting        | Value                                                    |
| -------------- | -------------------------------------------------------- |
| Host           | AllDebrid Client host name or address                    |
| Port           | `6500` unless changed in startup configuration           |
| URL base       | Blank unless AllDebrid Client has a base path configured |
| Category       | `sonarr` or `radarr`                                     |
| Initial state  | Started                                                  |
| Content layout | Default                                                  |

Leave sequential download, first/last-piece priority, and post-import category options disabled or blank. Test the client before removing an existing download client.

Enable completed-download removal when successfully imported source data should be cleaned up. This makes Sonarr or Radarr request source-file removal after import. AllDebrid Client separately applies the job's configured **Completed record action** to its own and the provider's records. Failed-download removal is independent and can remain disabled when failures should stay visible for diagnosis.

Sonarr and Radarr normally hardlink torrent payloads into their libraries when the download and library directories are on the same filesystem. When they are on different volumes, hardlinks are impossible and the application copies the files; completed-download removal then removes the source after a successful import.

## Logpose

Set **Client-visible download path** to the same directory as Logpose sees it. Leave it blank when both use the same path. For containers sharing `/media/downloads`, use `/media/downloads` in both applications.

Point Logpose's qBittorrent configuration at AllDebrid Client:

```yaml
downloadPath: "/media/downloads"

qbittorrent:
  enabled: true
  host: "http://alldebrid-client:6500/"
  username: ""
  password: ""
```

Leave `username` and `password` empty under the default **No Authentication** mode. Fill them in only when username and password authentication is enabled in AllDebrid Client.

Logpose creates and uses its category automatically, and files are downloaded beneath `<Local download path>/logpose`. After importing, Logpose calls the delete endpoint with `deleteFiles=false`, so it does not delete the downloaded payload. When the AllDebrid Client record is retained, it remains visible there with its original category and path but is hidden from subsequent qBittorrent API listings. Client and provider records are otherwise retained or removed according to the job's configured **Completed record action**. Only safe empty per-job directories are removed.

## Deletion behavior

External applications complete their workflows by calling the qBittorrent delete endpoint. AllDebrid Client handles that request according to both the request and the job's **Downloads → New torrent defaults → Completed record action** value captured when the job was added:

- `deleteFiles=false` does not delete the local payload.
- `deleteFiles=true` requests removal of the job's local payload.
- **No Action** retains the AllDebrid Client and provider records after any requested local-file cleanup. A retained record remains visible in AllDebrid Client but is hidden from the qBittorrent API after that external client removes it.
- Independently, other completed-record actions remove the client and provider records selected by that setting.

This separation lets an external application clean its imported source without silently overriding the configured provider and history retention policy.

Earlier builds represented a retained external deletion by appending `-retained` to the job category. Existing rows already changed that way are not guessed or rewritten during upgrade; review or rename those historical records manually if needed.

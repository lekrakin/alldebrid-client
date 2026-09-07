# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Unreleased

### Fixed

- Use a shared orange-accent dark theme and standard buttons while preserving premium status colors.
- Sort torrent counts correctly without mutating live data, and restrict bulk selection to matching rows.
- Add resizable, single-line torrent columns, a wider list layout, and compact dates with full timestamp tooltips.
- Pin settings, add-torrent, and torrent-management actions to the bottom of the window on short and long pages, including save feedback.
- Show external-client removal separately from download status without changing categories or assuming an import succeeded.
- Remove seeder tracking and seeder-based stalled states; use AllDebrid's reported download state instead.
- Preserve encoded trackers and literal percent signs when opening magnet links.
- Explain browser magnet-handler permissions without claiming a request is an active registration.
- Reuse the source deployment script for UAC elevation, with a read-only dry run and output in the original terminal.

## [1.6.0](https://github.com/krakn-dev/alldebrid-client/compare/v1.5.2...v1.6.0) (2026-09-04)


### Features

* **updates:** add verified Windows service updater ([474b89d](https://github.com/krakn-dev/alldebrid-client/commit/474b89d5d6efbf98e37280900985ce5e06d847e1))


### Bug Fixes

* **settings:** use platform-appropriate download paths ([022fca8](https://github.com/krakn-dev/alldebrid-client/commit/022fca845aac52ccde75dda87ea5d85afac36cf0))

## [1.5.2](https://github.com/krakn-dev/alldebrid-client/compare/v1.5.1...v1.5.2) (2026-09-04)


### Bug Fixes

* **qbittorrent:** clean imported payloads safely ([df4a4ff](https://github.com/krakn-dev/alldebrid-client/commit/df4a4ff0ed83ecfcbecc2375916cc3d32b0e3fbe))

## [1.5.1](https://github.com/krakn-dev/alldebrid-client/compare/v1.5.0...v1.5.1) (2026-09-04)


### Bug Fixes

* **settings:** clarify reported download paths ([b6e15bf](https://github.com/krakn-dev/alldebrid-client/commit/b6e15bf4780d2b8c115b8882c7184f721b36f433))

## [1.5.0](https://github.com/krakn-dev/alldebrid-client/compare/v1.4.0...v1.5.0) (2026-09-04)


### Features

* **qbittorrent:** support Sonarr and Radarr ([7773e76](https://github.com/krakn-dev/alldebrid-client/commit/7773e767ef8767b727d4a731a7964097ebfe593b))


### Bug Fixes

* **updates:** correct repository release URLs ([e61a275](https://github.com/krakn-dev/alldebrid-client/commit/e61a275dda720d0bff7e0201824c29b92db13095))

## [1.4.0](https://github.com/krakn-dev/alldebrid-client/compare/v1.3.1...v1.4.0) (2026-09-02)


### Features

* **downloads:** replace the legacy transfer engine ([c8a9294](https://github.com/krakn-dev/alldebrid-client/commit/c8a9294b96480d79da85b848d2b852596e3e6515))


### Bug Fixes

* **logpose:** retain completed provider jobs ([5275e36](https://github.com/krakn-dev/alldebrid-client/commit/5275e366c516ff0a16442ba6c65f33e2f91dd6a5))
* **qbittorrent:** restore post-import cleanup ([afb7cb9](https://github.com/krakn-dev/alldebrid-client/commit/afb7cb908d0cf9067cfd69ce746a5a8858d32679))

## [1.3.1](https://github.com/krakn-dev/alldebrid-client/compare/v1.3.0...v1.3.1) (2026-09-02)


### Bug Fixes

* **client:** restore automatic view updates ([14c7ac6](https://github.com/krakn-dev/alldebrid-client/commit/14c7ac620961d394bc49dae5ba8cfa28eb68d9e7))

## [1.3.0](https://github.com/krakn-dev/alldebrid-client/compare/v1.2.0...v1.3.0) (2026-09-02)


### Features

* **logpose:** add native qBittorrent compatibility ([a3edfbb](https://github.com/krakn-dev/alldebrid-client/commit/a3edfbba3b64b41e8f948198fc9876900e6bfc34))


### Bug Fixes

* **logpose:** remove empty imported job directories ([5922ab7](https://github.com/krakn-dev/alldebrid-client/commit/5922ab736aa4fd63f5471e30b1ae5717a1419a25))
* **logpose:** support legacy Nyaa infohash URLs ([947c2cc](https://github.com/krakn-dev/alldebrid-client/commit/947c2ccc47ce887f8cc5f6079c9c4e86aa79062a))
* **qbittorrent:** honor download retention settings ([01caafa](https://github.com/krakn-dev/alldebrid-client/commit/01caafa4a3758e82027d6d82d7ec7909b0ef7772))

## [1.2.0](https://github.com/krakn-dev/alldebrid-client/compare/v1.1.0...v1.2.0) (2026-08-31)


### Features

* **platform:** move to .NET 10 and harden runtime ([95d5f2b](https://github.com/krakn-dev/alldebrid-client/commit/95d5f2b7d668858b4095edc330c676bf944ab1fa))
* **release:** automate verified semantic releases ([6773cad](https://github.com/krakn-dev/alldebrid-client/commit/6773cad168be6b3c745a63e436048febc3fa31c8))
* **windows:** add guarded service deployment ([4a83881](https://github.com/krakn-dev/alldebrid-client/commit/4a83881879afc1a3e8f25deebe60500c44fd33ad))


### Bug Fixes

* **client:** repair landing route and release notices ([4b64ccd](https://github.com/krakn-dev/alldebrid-client/commit/4b64ccd689b95b77cf55c394609c8509f45402a3))
* **docker:** use local persistent compose builds ([8a48a2a](https://github.com/krakn-dev/alldebrid-client/commit/8a48a2aeba2fe9d95288618b2b8964ff80348d39))
* **release:** preserve notes and project formatting ([34970e9](https://github.com/krakn-dev/alldebrid-client/commit/34970e95ad28918b3bef18c7f9335e824f54ce48))
* **service:** treat shutdown cancellation normally ([9cf582d](https://github.com/krakn-dev/alldebrid-client/commit/9cf582d68f2cfd3c05af1e4cbfa792169111c558))
* **web:** resolve runtime files from executable ([10901eb](https://github.com/krakn-dev/alldebrid-client/commit/10901ebe23e41430bf5f22fb0cb29c7ae4130fcb))

## [1.1.0] - 2026-05-09

### Added

- Settings: Profile tab for changing login username and password
- `publish.ps1` — single script to build frontend + backend and deploy to a local install path

### Changed

- Docker: runtime base image bumped to Alpine 3.22
- Docker: `tools/docker-compose.yml` image name corrected to `krakal/alldebrid-client`
- DataPath now stamped into `appsettings.json` by `publish.ps1`; data stored under `<install>/data/`

### Fixed

- Downloader: `OutOfMemoryException` caused by chunk size never being applied to `DownloaderNET.Settings`
- Server: `GET /Api/Settings/Profile` returned HTTP 500 when API key was not set; now returns `null`
- Client: settings tabs and navbar (version, premium days) did not render on page refresh until a click triggered change detection
- Client: torrent list did not update from SignalR events because the hub callback ran outside Angular's zone
- CI: Docker image workflow now produces correct semver tags (`latest`, `1`, `1.1`, `1.1.0`)

## [1.0.0] - 2026-05-03

### Added

- Torrent table: filter by name
- Torrent table: sortable columns with direction indicators

### Changed

- Rebranded to AllDebrid Client — new package name `alldebrid-client`, assemblies renamed to `AdbClient.*`
- Database file renamed from `rdtclient.db` to `adbclient.db`
- Settings UI: shorter descriptions, responsive CSS grid layout for compact fields
- Navbar: simplified premium indicator (green/red dot + days remaining)
- Default authentication mode changed to `None`
- Path configuration: `DataPath` moved to `appsettings.json`; Windows defaults retained in settings UI descriptions
- Server: C# type alias standardization throughout
- Server: controller DTOs extracted to `Models/Requests/`
- Server: explicit `Exception` types on all throw statements
- Server: `await using` on `IFormFile` streams

### Removed

- All torrent providers except AllDebrid
- qBittorrent download client integration
- Sonarr/Radarr (*arr) API layer
- External download client selector — internal downloader only
- Download client selector from the add-torrent dialog

### Fixed

- Test suite updated for AllDebrid-only provider configuration
- Cross-platform path handling in `RunTorrentComplete` test (was failing on Linux CI)

[1.1.0]: https://github.com/krakn-dev/alldebrid-client/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/krakn-dev/alldebrid-client/releases/tag/v1.0.0

# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Unreleased

### Fixed

- Preserve existing databases when upgrading custom relative startup paths; reject ambiguous paths before replacing application files.
- Preserve the cursor and selection when pasting into the magnet field.
- Keep torrent-settings drafts and show retry feedback when saving fails, without displaying unsaved values.
- Use a shared orange-accent dark theme and standard buttons while preserving premium status colors.
- Sort torrent counts correctly without mutating live data, and restrict bulk selection to matching rows.
- Add resizable, single-line torrent columns, a wider list layout, and compact dates with full timestamp tooltips.
- Pin settings, add-torrent, and torrent-management actions to the bottom of the window on short and long pages, including save feedback.
- Show external-client removal separately from download status without changing categories or assuming an import succeeded.
- Remove seeder tracking and seeder-based stalled states; use AllDebrid's reported download state instead.
- Preserve encoded trackers and literal percent signs when opening magnet links.
- Explain browser magnet-handler permissions without claiming a request is an active registration.
- Reuse the source deployment script for UAC elevation, with a read-only dry run and output in the original terminal.

## [1.6.1](https://github.com/krakn-dev/alldebrid-client/compare/v1.6.0...v1.6.1) (2026-09-08)


### Features

* **settings:** streamline configuration workflow ([0d8505a](https://github.com/krakn-dev/alldebrid-client/commit/0d8505a19f2d9460bedf53e8d518149d42f2bb69))
* **ui:** add resizable single-line torrent columns ([09bbe2c](https://github.com/krakn-dev/alldebrid-client/commit/09bbe2c2eb8301296b4ddd1c5d3b672560ecd7da))
* **ui:** keep page actions visible while scrolling ([0ad2812](https://github.com/krakn-dev/alldebrid-client/commit/0ad281211a61bf5f0b0b4848c5953db89d14c569))


### Bug Fixes

* **account:** allow partial credential updates ([a89bdb1](https://github.com/krakn-dev/alldebrid-client/commit/a89bdb1087c0cd52e006f5d7c09c2081b6879a3d))
* **account:** clarify initial credential setup ([03f54d9](https://github.com/krakn-dev/alldebrid-client/commit/03f54d99ba78c8e5282345e6bae44912d6cdf613))
* **account:** create initial credentials from settings ([38675bd](https://github.com/krakn-dev/alldebrid-client/commit/38675bde803d77d04a4d57f3c191c843c27ab650))
* **account:** serialize initial user creation ([7d85cfc](https://github.com/krakn-dev/alldebrid-client/commit/7d85cfc2f232ee33e0facff60891b65117f9a3d5))
* **build:** guard publish destinations ([9f46742](https://github.com/krakn-dev/alldebrid-client/commit/9f46742db4fb69c98c2cd2c23168008ba555e5c4))
* **client:** make async views reliably reactive ([e89d67c](https://github.com/krakn-dev/alldebrid-client/commit/e89d67c642edf0d5eaca1d48b122068750b9e4e7))
* **client:** require an explicit delete action ([d5c6dd2](https://github.com/krakn-dev/alldebrid-client/commit/d5c6dd220b3686c50fa5d7ff67c353db896b5993))
* **client:** respect base path in magnet handler ([64beddd](https://github.com/krakn-dev/alldebrid-client/commit/64beddd96bc3e53163a396535aa0ad20f48e0d61))
* **docker:** avoid scanning completed downloads at startup ([298c288](https://github.com/krakn-dev/alldebrid-client/commit/298c288a4f96c83901bb60bcb9937ecf08e8f66b))
* **downloads:** confirm active work before deletion ([2ed62e8](https://github.com/krakn-dev/alldebrid-client/commit/2ed62e882efafbbb86461bd17112d869515cfa8e))
* **downloads:** remove ineffective availability mode ([e351f46](https://github.com/krakn-dev/alldebrid-client/commit/e351f46cd246e2a43778a8183146ac55e767ba06))
* **downloads:** wait for active work to stop ([6d76413](https://github.com/krakn-dev/alldebrid-client/commit/6d764134e07d6cfd53a26035283cdb13b958314a))
* **filters:** bound size and regex evaluation ([82c17bb](https://github.com/krakn-dev/alldebrid-client/commit/82c17bbf0c19ed8b8ef1950221f7c25418d4040a))
* **integrations:** bound completion command runtime ([bb4900e](https://github.com/krakn-dev/alldebrid-client/commit/bb4900ebea2bf42664212241171005a80c1c03f6))
* **integrations:** skip completion command without files ([bcc3484](https://github.com/krakn-dev/alldebrid-client/commit/bcc34848fc98b1726f1c96edde04aa130557f795))
* **logging:** omit database values from conflicts ([3cae548](https://github.com/krakn-dev/alldebrid-client/commit/3cae548fd4500c8990b7cc6aba2a15f4c9b2402d))
* **provider:** bound polling interval calculation ([cb96491](https://github.com/krakn-dev/alldebrid-client/commit/cb96491b79858cc3483f68edaf4152f58040418f))
* **provider:** preserve files during reconciliation ([b94ece2](https://github.com/krakn-dev/alldebrid-client/commit/b94ece2cf7f56d37dcbc6ca6739d7863fca4e711))
* **provider:** validate enriched tracker metadata ([2f98c24](https://github.com/krakn-dev/alldebrid-client/commit/2f98c240807cf22daec37bf3c8a3fcb2d67fd3b3))
* **qbittorrent:** honor configured deletion lifecycle ([63120c2](https://github.com/krakn-dev/alldebrid-client/commit/63120c230c2e0da576aa8e22277a758ef3e28381))
* **qbittorrent:** honor configured retention policy ([4363e18](https://github.com/krakn-dev/alldebrid-client/commit/4363e1847ab2ef07e0def7c6ccc0435be0e15f86))
* **qbittorrent:** reactivate retained torrents ([668aea0](https://github.com/krakn-dev/alldebrid-client/commit/668aea0f989a5f19775e278a063425e0aa6d00a6))
* refine configuration, torrent UI, and upgrade safety ([b600f35](https://github.com/krakn-dev/alldebrid-client/commit/b600f3581871c58ce92652fbd890710edb38c71e))
* **security:** keep secrets out of request logs ([d9bde8a](https://github.com/krakn-dev/alldebrid-client/commit/d9bde8a249f35f44b9d512660b28720a897a6557))
* **security:** protect live update connections ([bce54a1](https://github.com/krakn-dev/alldebrid-client/commit/bce54a17a5f503db364a0a24f2c8c145009591f0))
* **security:** redact sensitive download URLs ([d558d05](https://github.com/krakn-dev/alldebrid-client/commit/d558d05eca2bf28a8e88b7dabb30564478e68ccd))
* **settings:** explain insecure magnet-handler connections ([1054e48](https://github.com/krakn-dev/alldebrid-client/commit/1054e483b26354639b22d255e476fbae2121204a))
* **settings:** isolate diagnostic files ([8b8092e](https://github.com/krakn-dev/alldebrid-client/commit/8b8092eb1369dd727865796d33b446adb4720f54))
* **settings:** make browser magnet registration reliable ([bb5e298](https://github.com/krakn-dev/alldebrid-client/commit/bb5e298c0dee13715b96c1d47ccf85645171c5f3))
* **settings:** normalize host-local paths ([d85f4a3](https://github.com/krakn-dev/alldebrid-client/commit/d85f4a3051fa518507fd6e03d4f5be3ead003084))
* **settings:** preserve legacy download configuration ([df64f36](https://github.com/krakn-dev/alldebrid-client/commit/df64f36be4c8fa804d887cf025fd43025616cbfa))
* **settings:** preserve released keys across upgrades and rollbacks ([d9ffec7](https://github.com/krakn-dev/alldebrid-client/commit/d9ffec710fdd7482cdd3b6dd81c40d681331d8cd))
* **settings:** prevent heading and subtitle overlap ([04ca36e](https://github.com/krakn-dev/alldebrid-client/commit/04ca36e83d7d077baa03b819e82673426174b2ce))
* **settings:** retain completed records by default ([3b441c8](https://github.com/krakn-dev/alldebrid-client/commit/3b441c85520fcfc982c8d8604b75b3fc9bf9e79f))
* **settings:** validate and apply runtime values ([dff9b9f](https://github.com/krakn-dev/alldebrid-client/commit/dff9b9fa510863e469de163a15cfb1d11000c79f))
* **setup:** preserve existing provider configuration ([d6fe376](https://github.com/krakn-dev/alldebrid-client/commit/d6fe37688ee4feb86544e5694f0a8e96fab2dc74))
* **startup:** preserve existing relative data locations ([0d88a72](https://github.com/krakn-dev/alldebrid-client/commit/0d88a72a259498f3a4fefb20e07d3e2c5e137c0b))
* **startup:** separate database and log files ([a82c81d](https://github.com/krakn-dev/alldebrid-client/commit/a82c81dff322e72897b1f12695725900d7812a24))
* **startup:** validate paths and isolate dev data ([9c954a2](https://github.com/krakn-dev/alldebrid-client/commit/9c954a276bdbbbb98b8a075e7d53679e9eba35c8))
* **storage:** keep existing jobs on captured paths ([e91258a](https://github.com/krakn-dev/alldebrid-client/commit/e91258a5d477862d4579bb73e08091d1a320d0b1))
* **torrents:** make list sorting and selection predictable ([263eeba](https://github.com/krakn-dev/alldebrid-client/commit/263eeba30b0145f29eb72f9308124fa9f3bce068))
* **torrents:** report client lifecycle without seeder heuristics ([0165349](https://github.com/krakn-dev/alldebrid-client/commit/01653497619d39aad5055c679b49d2ad5d37d698))
* **ui:** anchor page actions to the viewport bottom ([464c491](https://github.com/krakn-dev/alldebrid-client/commit/464c491753826fd3a45cb5e1737bcfdee7424aec))
* **ui:** preserve native magnet paste behavior ([b724a77](https://github.com/krakn-dev/alldebrid-client/commit/b724a77fe67cf583e9f2ede8f7bb8dba1d0291bf))
* **ui:** retain torrent settings drafts after failed saves ([29d96c6](https://github.com/krakn-dev/alldebrid-client/commit/29d96c69090cab8deb2de79b93bddab523aa0ac3))
* **updates:** honor service configuration safely ([e1a025d](https://github.com/krakn-dev/alldebrid-client/commit/e1a025da9e5cf4e045ae0fc355aedfcd326d125e))
* **updates:** protect configured persistent paths ([2fcacc8](https://github.com/krakn-dev/alldebrid-client/commit/2fcacc82e39119641a34a8d0de7ff1d096880069))
* **windows:** make service deployment recoverable ([eabbf80](https://github.com/krakn-dev/alldebrid-client/commit/eabbf80ff52e2559d28670c91aa4411737e0e56e))
* **windows:** make source deployment self-elevating ([db2291b](https://github.com/krakn-dev/alldebrid-client/commit/db2291b6989cb45d28218dac3e4c99ec7ee617f4))
* **windows:** manage the exact firewall rule ([968f063](https://github.com/krakn-dev/alldebrid-client/commit/968f063afc0389ee74a14376a02e80f088a2d966))
* **windows:** protect effective service data paths ([02433b0](https://github.com/krakn-dev/alldebrid-client/commit/02433b0f950d7fe57083b53259bbec82e280d786))


### Miscellaneous Chores

* **release:** prepare 1.6.1 for local deployment ([a8d2224](https://github.com/krakn-dev/alldebrid-client/commit/a8d2224f61dd4c6e9f045dd222cc0f274411b1fe))

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

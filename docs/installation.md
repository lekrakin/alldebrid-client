# Installation and updates

Docker is the recommended installation method on Linux. A framework-dependent release package is available for Windows, and the application can also run directly with the .NET runtime.

## First-time setup

1. Open `http://127.0.0.1:6500`, or replace `127.0.0.1` with the host address.
2. Open **Settings → AllDebrid** and enter an API key from [alldebrid.com/apikeys](https://alldebrid.com/apikeys/).
3. Under **Settings → Storage**, review the local download path. Under **Settings → Downloads**, review the new-torrent download and retention defaults. The platform defaults are usable without editing them.
4. Save the settings before adding a torrent or configuring an integration.

The intentional default authentication mode is **No Authentication**, so a fresh installation does not ask for credentials. To enable authentication reliably, first enter and save both fields under **Settings → Account**. Then select **Settings → General → Authentication → Username + Password**, save, and sign in with that account. If authentication is enabled before an account exists, the setup flow can create the first account and preserves an API key that is already configured. Enable authentication before exposing the application beyond a trusted network. AllDebrid Client does not provide TLS termination; use a trusted reverse proxy when HTTPS is required.

See [Configuration](configuration.md) for the distinction between startup values and runtime settings, including mixed native/container download paths.

## Docker

Stable releases are published to GitHub Container Registry and Docker Hub when the container release job succeeds. Persist both `/data/db` and `/data/downloads`; the complete Compose examples and update procedure are in the [Docker guide](docker.md). Building from source is optional for local development.

## Windows service

1. Install the [.NET 10 ASP.NET Core Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Download and extract the [latest release ZIP](https://github.com/krakn-dev/alldebrid-client/releases/latest) to its permanent location.
3. Test the application by running `AdbClient.Web.exe` and opening `http://127.0.0.1:6500`.
4. To run it in the background, stop the test process and run `service-install.bat` as Administrator.

The installer creates an automatically started `AllDebridClient` Windows service and an inbound firewall rule for its executable. Enable application authentication before exposing the service beyond a trusted network. The service runs under the built-in LocalSystem account, so local download directories work without a separate account setup; network shares require an intentionally configured service identity and matching permissions. Run `service-remove.bat` as Administrator to remove the service and its managed firewall rule.

The default persistent data directory is `C:\ProgramData\AllDebridClient`. To use another location, edit `appsettings.json` before first launch and set `DataPath` to a writable directory. JSON backslashes must be escaped, for example `D:\\AllDebridClient\\Data`. Other startup values are documented in [Configuration](configuration.md).

Keep application files and persistent data in separate directories.

### Updating a Windows service

Double-click `update.cmd` in the application directory. Approve the Windows administrator prompt and confirm the update. The updater:

- accepts stable releases from this repository only;
- verifies the release ZIP against its published SHA-256 checksum and, when available, the digest recorded by GitHub;
- preserves `appsettings.json` and requires persistent data to be outside the application directory;
- stops and restarts the service only when it was already running;
- retains the previous application directory in a sibling backup directory; and
- restores the previous version automatically if the service does not become healthy.

Check for an update without changing the installation:

```powershell
.\update.ps1 -CheckOnly
```

Download, verify, and inspect the latest package without changing the installation:

```powershell
.\update.ps1 -ValidateOnly
```

Running the updater again when the current release is installed makes no changes. Use `-Force` only to reinstall that same release.

Repository maintainers with a checkout, the build prerequisites, and the standard `<install-root>\App`, `Data`, and `Backups` layout can deploy the current source with `./deploy.ps1` from a normal PowerShell session. The script discovers the installation from the service, asks for confirmation, and requests Windows administrator approval when needed. Build output and errors stay in the original terminal; no separate deployment wrapper is needed. It builds before stopping the service, preserves configuration and data, retains the previous application directory, and rolls back when the restarted service fails its health check. A stopped service remains stopped. Use `./deploy.ps1 -WhatIf` for a read-only preflight with no build, staging files, or administrator prompt. Windows requires approval for each new elevated process; the script does not disable UAC or install a background updater. This source deployment is separate from `update.cmd`, which installs published releases only.

## Native Linux service

Native Linux installations are built from source; use Docker when a prebuilt Linux package is preferred. Install Node.js 24, npm, and the .NET 10 SDK, then build and publish from a checkout:

```bash
npm --prefix client ci
npm --prefix client run build
dotnet restore server
dotnet publish server/AdbClient.Web/AdbClient.Web.csproj \
  --configuration Release \
  --no-restore \
  --output publish
```

Copy the publish output to `/opt/alldebrid-client` and install the .NET 10 ASP.NET Core Runtime on the target host. Before starting the application, create the dedicated service account and writable state and download directories. For distributions that provide `useradd` and `install`:

```bash
sudo useradd --system --user-group --home-dir /nonexistent --shell /usr/sbin/nologin alldebrid-client
sudo install -d -o alldebrid-client -g alldebrid-client /var/lib/alldebrid-client /data/downloads
```

Set `DataPath` to `/var/lib/alldebrid-client` in `appsettings.json`. If you choose different state or download directories, substitute those paths consistently and grant the service account access. Verify the application starts as that account:

```bash
sudo -u alldebrid-client dotnet /opt/alldebrid-client/AdbClient.Web.dll
```

Save the following minimal unit as `/etc/systemd/system/alldebrid-client.service`:

```ini
[Unit]
Description=AllDebrid Client
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=alldebrid-client
WorkingDirectory=/opt/alldebrid-client
ExecStart=/usr/bin/dotnet /opt/alldebrid-client/AdbClient.Web.dll
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

After stopping the foreground verification process, enable and start the unit:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now alldebrid-client
```

# Contributing

Contributions should be focused, reproducible, and consistent with the existing architecture. Open an issue before a broad redesign so the scope can be agreed before implementation.

## Repository layout

| Path                 | Purpose                                                     |
| -------------------- | ----------------------------------------------------------- |
| `client/`            | Angular web application                                     |
| `server/`            | .NET solution, application services, persistence, and tests |
| `root/`              | Container runtime overlay                                   |
| `tools/`             | Local development support files                             |
| `.github/workflows/` | Verification and release automation                         |

Generated frontend output, dependency folders, local databases, logs, and publish directories are intentionally untracked.

## Prerequisites

- Node.js 24 and npm
- .NET 10 SDK
- Docker Desktop or Docker Engine with Compose, only for container builds

A global Angular CLI installation is not required.

## Local development

On Windows, use the repository launcher:

```powershell
.\dev.ps1
```

The default menu action restores dependencies, builds and verifies both applications, and starts the backend only if verification succeeds. Direct commands are also available:

```powershell
.\dev.ps1 info
.\dev.ps1 deps
.\dev.ps1 rebuild
.\dev.ps1 verify
.\dev.ps1 run
.\dev.ps1 frontend
.\dev.ps1 backend
.\dev.ps1 docker
```

The Angular development server listens on port `4200` and proxies API and SignalR requests to the backend on port `6500`. Development commands do not install or replace the Windows service.

Equivalent direct commands can be run from any supported platform:

```bash
cd client
npm ci
npm run build

cd ../
dotnet restore server
dotnet build server --configuration Release --no-restore
dotnet test server --configuration Release --no-build
```

## Verification

Run the complete local check before opening a pull request:

```powershell
.\dev.ps1 verify
```

For repeated checks after dependencies are already current, `-SkipNpmCi` avoids reinstalling frontend packages. Continuous integration repeats the backend build and tests, frontend lint, format check, production build and dependency audit, and an `amd64` container build.

Keep tests close to the behavior they cover. A focused test is useful during development, but the complete verification command should pass before a pull request is ready.

## Commits and pull requests

Use [Conventional Commits](https://www.conventionalcommits.org/):

```text
fix(downloads): handle an interrupted transfer
feat(settings): add a configurable retry limit
docs: clarify the Docker volume layout
feat(api)!: remove a deprecated endpoint
```

Keep each commit independently understandable and limited to one concern. Explain non-obvious behavior and migration requirements in the commit body. Pull requests target `main`, must pass continuous integration, and should not contain credentials, local paths, generated output, or unrelated formatting changes.

## Versions and releases

Versions follow Semantic Versioning:

- `fix:` increments the patch version.
- `feat:` increments the minor version.
- A `!` or `BREAKING CHANGE:` footer increments the major version.
- Documentation, tests, refactors, build changes, CI changes, and chores do not create a release unless they include a breaking change.

Release Please maintains a release pull request from commits merged since the latest release. That pull request updates `CHANGELOG.md`, `version.txt`, the .NET assembly version, frontend package metadata, and Docker defaults together. Merging it creates the Git tag and GitHub release. Post-release jobs then build the Windows package and multi-platform container image; successful jobs upload the package, checksum, and image.

Do not manually edit managed version fields or create release tags.

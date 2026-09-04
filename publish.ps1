param(
    [string]$InstallPath,
    [string]$DataPath,
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
$root = $PSScriptRoot

function Get-NormalizedDirectoryPath([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath)

    if ($fullPath.Length -gt $pathRoot.Length) {
        return $fullPath.TrimEnd('\', '/')
    }

    return $fullPath
}

function Test-SameOrDescendantPath([string]$Parent, [string]$Candidate) {
    $normalizedParent = Get-NormalizedDirectoryPath $Parent
    $normalizedCandidate = Get-NormalizedDirectoryPath $Candidate

    if ($normalizedCandidate.Equals($normalizedParent, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $prefix = $normalizedParent
    if ($prefix[$prefix.Length - 1] -ne [System.IO.Path]::DirectorySeparatorChar) {
        $prefix += [System.IO.Path]::DirectorySeparatorChar
    }

    return $normalizedCandidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Resolve-ConfiguredPath([string]$Path, [string]$BasePath) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Get-ServiceApplicationPath([string]$PathName) {
    $tokens = [regex]::Matches($PathName, '"([^"]+)"|(\S+)') | ForEach-Object {
        if ($_.Groups[1].Success) { $_.Groups[1].Value } else { $_.Groups[2].Value }
    }

    return $tokens | Where-Object {
        $_ -match '(?i)AdbClient\.Web\.(dll|exe)$'
    } | Select-Object -First 1
}

function Assert-TemporaryPublishPath([string]$Parent, [string]$Candidate) {
    $candidateName = Split-Path -Leaf $Candidate
    if (-not (Test-SameOrDescendantPath $Parent $Candidate) -or
        (Get-NormalizedDirectoryPath $Parent) -ieq (Get-NormalizedDirectoryPath $Candidate) -or
        -not $candidateName.StartsWith('.adbclient-publish-', [StringComparison]::Ordinal)) {
        throw "Refusing to use an unexpected staging path: $Candidate"
    }
}

function Assert-PublishDestinationOwned([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $destination = Get-Item -LiteralPath $Path -Force
    if (($destination.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to replace a publish destination that is a reparse point: $Path"
    }

    $entries = @(Get-ChildItem -LiteralPath $Path -Force)
    if ($entries.Count -eq 0) {
        return
    }

    $requiredOutput = @(
        "AdbClient.Web.dll",
        "AdbClient.Web.deps.json",
        "AdbClient.Web.runtimeconfig.json",
        "appsettings.json",
        (Join-Path "wwwroot" "index.html")
    )
    $missingOutput = @($requiredOutput | Where-Object {
        $requiredPath = Join-Path $Path $_
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            return $true
        }

        $requiredItem = Get-Item -LiteralPath $requiredPath -Force
        return ($requiredItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
    })

    $webRootPath = Join-Path $Path "wwwroot"
    if (Test-Path -LiteralPath $webRootPath -PathType Container) {
        $webRoot = Get-Item -LiteralPath $webRootPath -Force
        if (($webRoot.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            $missingOutput += (Join-Path "wwwroot" "index.html")
        }
    }

    if ($missingOutput.Count -gt 0) {
        throw "Refusing to clean non-empty destination because it is not a recognizable AllDebrid Client publish output: $Path"
    }
}

if ([string]::IsNullOrWhiteSpace($InstallPath)) {
    $InstallPath = Join-Path $root "publish"
}

$InstallPath = Get-NormalizedDirectoryPath $InstallPath
$projectRoot = Get-NormalizedDirectoryPath $root
$driveRoot = Get-NormalizedDirectoryPath ([System.IO.Path]::GetPathRoot($InstallPath))

if ($InstallPath -ieq $projectRoot -or $InstallPath -ieq $driveRoot) {
    throw "Refusing to publish over the project root or a drive root: $InstallPath"
}

if (Test-Path -LiteralPath $InstallPath -PathType Leaf) {
    throw "Publish destination is a file, not a directory: $InstallPath"
}

Assert-PublishDestinationOwned $InstallPath

if ($env:OS -eq "Windows_NT" -and $null -ne (Get-Command Get-CimInstance -ErrorAction SilentlyContinue)) {
    $service = Get-CimInstance Win32_Service -Filter "Name='AllDebridClient'" -ErrorAction SilentlyContinue

    if ($null -ne $service) {
        $serviceApplicationPath = Get-ServiceApplicationPath $service.PathName

        if (-not [string]::IsNullOrWhiteSpace($serviceApplicationPath)) {
            $serviceApplicationPath = [System.IO.Path]::GetFullPath($serviceApplicationPath)
            $serviceDirectory = Split-Path -Parent $serviceApplicationPath
            $pathsOverlap = (Test-SameOrDescendantPath $InstallPath $serviceApplicationPath) -or
                            (Test-SameOrDescendantPath $serviceDirectory $InstallPath)

            if ($pathsOverlap) {
                throw "Refusing to publish into files registered to the AllDebridClient service ($($service.State)): $serviceDirectory. Use deploy.ps1 for service installations."
            }
        }
    }
}

$existingSettingsPath = Join-Path $InstallPath "appsettings.json"
$existingSettings = $null

if (Test-Path -LiteralPath $existingSettingsPath -PathType Leaf) {
    try {
        $existingSettings = Get-Content -LiteralPath $existingSettingsPath -Raw | ConvertFrom-Json
    } catch {
        throw "Existing startup configuration is not valid JSON: $existingSettingsPath"
    }
}

if ([string]::IsNullOrWhiteSpace($DataPath)) {
    if ($null -ne $existingSettings -and -not [string]::IsNullOrWhiteSpace($existingSettings.DataPath)) {
        $DataPath = $existingSettings.DataPath
    } else {
        $DataPath = Join-Path $InstallPath "data"
    }
}

$DataPath = Resolve-ConfiguredPath $DataPath $InstallPath

if ((Get-NormalizedDirectoryPath $DataPath) -ieq $InstallPath) {
    throw "DataPath cannot be the publish destination because application files cannot be replaced without risking persistent data: $DataPath"
}

if (Test-Path -LiteralPath $DataPath -PathType Leaf) {
    throw "DataPath is a file, not a directory: $DataPath"
}

$protectedPaths = @($DataPath)

if ($null -ne $existingSettings) {
    if (-not [string]::IsNullOrWhiteSpace($existingSettings.Database.Path)) {
        $protectedPaths += Resolve-ConfiguredPath $existingSettings.Database.Path $DataPath
    }

    if (-not [string]::IsNullOrWhiteSpace($existingSettings.Logging.File.Path)) {
        $protectedPaths += Resolve-ConfiguredPath $existingSettings.Logging.File.Path $DataPath
    }
}

$protectedTopLevelNames = @($protectedPaths | ForEach-Object {
    $protectedPath = [System.IO.Path]::GetFullPath($_)

    if (Test-SameOrDescendantPath $InstallPath $protectedPath) {
        $installPrefix = $InstallPath.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        $relativePath = $protectedPath.Substring($installPrefix.Length)
        $relativePath.Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)[0]
    }
} | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)

$installParent = Split-Path -Parent $InstallPath
$stagingDirectory = Join-Path $installParent ".adbclient-publish-$([Guid]::NewGuid().ToString('N'))"
$stagingDirectory = Get-NormalizedDirectoryPath $stagingDirectory
Assert-TemporaryPublishPath $installParent $stagingDirectory

try {
    Write-Host "==> Building frontend"
    Push-Location (Join-Path $root "client")
    try {
        npm run build
        if ($LASTEXITCODE -ne 0) {
            throw "Frontend build failed with exit code $LASTEXITCODE."
        }
    } finally {
        Pop-Location
    }

    New-Item -ItemType Directory -Path $installParent -Force | Out-Null
    New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

    Write-Host "==> Publishing into staging"
    $publishArguments = @(
        "publish",
        (Join-Path $root "server\AdbClient.Web\AdbClient.Web.csproj"),
        "--configuration", "Release",
        "--output", $stagingDirectory
    )

    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        $publishArguments += "-p:Version=$Version"
        $publishArguments += "-p:AssemblyVersion=$Version"
    }

    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Backend publish failed with exit code $LASTEXITCODE."
    }

    $stagedSettingsPath = Join-Path $stagingDirectory "appsettings.json"
    if ($null -ne $existingSettings) {
        Copy-Item -LiteralPath $existingSettingsPath -Destination $stagedSettingsPath -Force
    }

    Write-Host "==> Setting data path to $DataPath"
    $settings = Get-Content -LiteralPath $stagedSettingsPath -Raw | ConvertFrom-Json
    $settings.DataPath = $DataPath
    $settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $stagedSettingsPath -Encoding UTF8

    foreach ($protectedName in $protectedTopLevelNames) {
        if (Test-Path -LiteralPath (Join-Path $stagingDirectory $protectedName)) {
            throw "Published application output conflicts with protected persistent data path: $protectedName"
        }
    }

    New-Item -ItemType Directory -Path $DataPath -Force | Out-Null

    Write-Host "==> Replacing application files in $InstallPath"
    if (-not (Test-Path -LiteralPath $InstallPath -PathType Container)) {
        New-Item -ItemType Directory -Path $InstallPath | Out-Null
    }

    foreach ($item in @(Get-ChildItem -LiteralPath $InstallPath -Force |
            Where-Object { $protectedTopLevelNames -notcontains $_.Name })) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            Remove-Item -LiteralPath $item.FullName -Force
        } else {
            Remove-Item -LiteralPath $item.FullName -Recurse -Force
        }
    }

    Get-ChildItem -LiteralPath $stagingDirectory -Force |
        Move-Item -Destination $InstallPath

    Write-Host "==> Done: $InstallPath"
} finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Assert-TemporaryPublishPath $installParent $stagingDirectory
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "High")]
param(
    [switch]$CheckOnly,
    [switch]$ValidateOnly,
    [switch]$Force,
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$ServiceName = "AllDebridClient",
    [string]$ApplicationDirectory,
    [switch]$Pause,
    [Parameter(DontShow)]
    [switch]$Elevated
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertTo-SingleQuotedLiteral([string]$Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}

function Start-ElevatedUpdater {
    $commandParts = @(
        "&",
        (ConvertTo-SingleQuotedLiteral $PSCommandPath),
        "-Elevated",
        "-ServiceName",
        (ConvertTo-SingleQuotedLiteral $ServiceName)
    )

    if (-not [string]::IsNullOrWhiteSpace($ApplicationDirectory)) {
        $commandParts += "-ApplicationDirectory"
        $commandParts += ConvertTo-SingleQuotedLiteral $ApplicationDirectory
    }

    if ($Force) {
        $commandParts += "-Force"
    }

    if ($Pause) {
        $commandParts += "-Pause"
    }

    $encodedCommand = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes(($commandParts -join " ")))
    $powerShell = (Get-Process -Id $PID).Path

    try {
        $process = Start-Process -FilePath $powerShell `
                                 -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encodedCommand" `
                                 -Verb RunAs `
                                 -Wait `
                                 -PassThru
    } catch {
        throw "Administrator approval is required to update the Windows service."
    }

    return $process.ExitCode
}

function Get-ServiceApplicationPath([string]$PathName) {
    $tokens = [regex]::Matches($PathName, '"([^"]+)"|(\S+)') | ForEach-Object {
        if ($_.Groups[1].Success) { $_.Groups[1].Value } else { $_.Groups[2].Value }
    }

    return $tokens | Where-Object {
        $_ -match '(?i)AdbClient\.Web\.(dll|exe)$'
    } | Select-Object -First 1
}

function Get-ServiceEnvironmentValues([string]$Name) {
    $values = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::OrdinalIgnoreCase)

    foreach ($settingName in @("Port", "BasePath", "BASE_PATH", "DataPath", "Database__Path", "Logging__File__Path")) {
        $machineValue = [Environment]::GetEnvironmentVariable(
            $settingName,
            [EnvironmentVariableTarget]::Machine)
        if ($null -ne $machineValue) {
            $values[$settingName] = $machineValue
        }
    }

    $serviceKey = "Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\$Name"
    $serviceValues = Get-ItemProperty -LiteralPath $serviceKey `
                                      -Name Environment `
                                      -ErrorAction SilentlyContinue
    if ($null -ne $serviceValues -and $null -ne $serviceValues.Environment) {
        foreach ($entry in @($serviceValues.Environment)) {
            $separator = $entry.IndexOf('=')
            if ($separator -gt 0) {
                $values[$entry.Substring(0, $separator)] = $entry.Substring($separator + 1)
            }
        }
    }

    return $values
}

function Get-CommandLineSetting([string]$PathName, [string]$Name) {
    $pattern = '(?i)(?:^|\s)(?:--|/)?{0}(?:\s*=\s*|\s+)(?:"(?<quoted>[^"]*)"|(?<plain>\S*))' -f
               [regex]::Escape($Name)
    $matches = [regex]::Matches($PathName, $pattern)
    if ($matches.Count -eq 0) {
        return $null
    }

    $match = $matches[$matches.Count - 1]
    if ($match.Groups["quoted"].Success) {
        return $match.Groups["quoted"].Value
    }

    return $match.Groups["plain"].Value
}

function Test-SameOrDescendant([string]$Parent, [string]$Child) {
    $normalizedParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    $normalizedChild = [IO.Path]::GetFullPath($Child).TrimEnd('\', '/')
    $prefix = $normalizedParent + [IO.Path]::DirectorySeparatorChar

    return $normalizedChild.Equals($normalizedParent, [StringComparison]::OrdinalIgnoreCase) -or
           $normalizedChild.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Get-ConfiguredValue($Settings, [string]$Key, $DefaultValue, $EnvironmentValues, [string]$PathName) {
    $commandLineValue = Get-CommandLineSetting $PathName $Key
    if ($null -ne $commandLineValue) {
        return $commandLineValue
    }

    $environmentName = $Key.Replace(':', '__')
    if ($EnvironmentValues.ContainsKey($environmentName)) {
        return $EnvironmentValues[$environmentName]
    }

    $configuredValue = $Settings
    foreach ($segment in $Key.Split(':')) {
        if ($null -eq $configuredValue) {
            return $DefaultValue
        }

        $property = $configuredValue.PSObject.Properties[$segment]
        if ($null -eq $property) {
            return $DefaultValue
        }

        $configuredValue = $property.Value
    }

    if ($null -eq $configuredValue) {
        return $DefaultValue
    }

    return $configuredValue.ToString()
}

function Test-AbsolutePath([string]$Path) {
    # IsPathRooted also accepts C:relative and \root-relative on Windows.
    $normalized = $Path.Replace('/', '\')
    return $normalized -match '^[A-Za-z]:\\' -or
           $normalized -match '^\\\\(?![.?](?:\\|$))[^\\]+\\[^\\]+(?:\\|$)'
}

function Resolve-ConfiguredPath([string]$Path, [string]$SettingName) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$SettingName must not be blank."
    }

    $trimmedPath = $Path.Trim()
    if (-not (Test-AbsolutePath $trimmedPath)) {
        throw "$SettingName '$trimmedPath' is relative or not fully qualified. Configure an absolute path to its existing location before updating. The previous process working directory cannot be inferred safely; do not create a new data location."
    }

    try {
        return [IO.Path]::GetFullPath($trimmedPath)
    } catch {
        throw "$SettingName is not a valid filesystem path."
    }
}

function Resolve-ConfiguredFilePath(
    [string]$Path,
    [string]$DataPath,
    [string]$SettingName,
    [string]$DefaultFileName
) {
    $configuredPath = if ([string]::IsNullOrWhiteSpace($Path)) { Join-Path $DataPath $DefaultFileName } else { $Path.Trim() }
    $resolvedPath = Resolve-ConfiguredPath $configuredPath $SettingName

    try {
        $fileName = [IO.Path]::GetFileName($resolvedPath)
    } catch {
        throw "$SettingName is not a valid filesystem path."
    }

    if ([string]::IsNullOrWhiteSpace($fileName) -or $fileName -in @('.', '..')) {
        throw "$SettingName must identify a file, not a directory."
    }

    return $resolvedPath
}

function Get-PersistentPaths($Settings, [string]$ApplicationDirectory, $EnvironmentValues, [string]$PathName) {
    $dataPath = Resolve-ConfiguredPath `
        (Get-ConfiguredValue $Settings 'DataPath' './data' $EnvironmentValues $PathName) `
        'DataPath'
    $databasePath = Resolve-ConfiguredFilePath `
        (Get-ConfiguredValue $Settings 'Database:Path' $null $EnvironmentValues $PathName) `
        $dataPath `
        'Database:Path' `
        'adbclient.db'
    $logPath = Resolve-ConfiguredFilePath `
        (Get-ConfiguredValue $Settings 'Logging:File:Path' $null $EnvironmentValues $PathName) `
        $dataPath `
        'Logging:File:Path' `
        'adbclient.log'

    return @(
        [pscustomobject]@{ Name = 'DataPath'; Path = $dataPath }
        [pscustomobject]@{ Name = 'Database:Path'; Path = $databasePath }
        [pscustomobject]@{ Name = 'Logging:File:Path'; Path = $logPath }
    )
}

function Assert-PersistentPathsOutsideApplication($Settings, [string]$ApplicationDirectory, $EnvironmentValues, [string]$PathName) {
    foreach ($persistentPath in (Get-PersistentPaths $Settings $ApplicationDirectory $EnvironmentValues $PathName)) {
        if (Test-SameOrDescendant $ApplicationDirectory $persistentPath.Path) {
            throw "$($persistentPath.Name) '$($persistentPath.Path)' is inside the application directory. Move persistent data outside '$ApplicationDirectory' before using in-place updates."
        }
    }
}

function Assert-StrictChildPath([string]$Parent, [string]$Child) {
    $normalizedParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    $normalizedChild = [IO.Path]::GetFullPath($Child).TrimEnd('\', '/')
    $prefix = $normalizedParent + [IO.Path]::DirectorySeparatorChar

    if (-not $normalizedChild.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside '$normalizedParent': $normalizedChild"
    }
}

function Get-ApplicationVersion([string]$Directory) {
    $assemblyPath = Join-Path $Directory "AdbClient.Web.dll"
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "Application assembly not found: $assemblyPath"
    }

    $version = [Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version
    if ($null -eq $version -or $version.Build -lt 0) {
        throw "Could not read a stable application version from $assemblyPath"
    }

    return [Version]::new($version.Major, $version.Minor, $version.Build)
}

function Get-LatestRelease {
    $repository = "krakn-dev/alldebrid-client"
    $headers = @{
        Accept = "application/vnd.github+json"
        "User-Agent" = "AllDebridClient-Updater"
        "X-GitHub-Api-Version" = "2022-11-28"
    }
    $uri = "https://api.github.com/repos/$repository/releases/latest"

    try {
        $release = Invoke-RestMethod -Uri $uri -Headers $headers
    } catch {
        throw "Could not read the latest release from GitHub: $($_.Exception.Message)"
    }

    if ($release.draft -or $release.prerelease -or $release.tag_name -notmatch '^v(\d+\.\d+\.\d+)$') {
        throw "GitHub did not return a stable vMAJOR.MINOR.PATCH release."
    }

    $version = [Version]$Matches[1]
    $assetName = "AllDebridClient-v$version-windows.zip"
    $checksumName = "$assetName.sha256"
    $packageAsset = $release.assets | Where-Object { $_.name -ceq $assetName } | Select-Object -First 1
    $checksumAsset = $release.assets | Where-Object { $_.name -ceq $checksumName } | Select-Object -First 1

    if ($null -eq $packageAsset -or $null -eq $checksumAsset) {
        throw "Release $($release.tag_name) is missing '$assetName' or its checksum."
    }

    foreach ($asset in @($packageAsset, $checksumAsset)) {
        $assetUri = [Uri]$asset.browser_download_url
        if ($assetUri.Scheme -cne "https" -or $assetUri.Host -cne "github.com") {
            throw "Release asset '$($asset.name)' does not use the expected GitHub HTTPS download host."
        }
    }

    return [pscustomobject]@{
        Tag = $release.tag_name
        Version = $version
        Package = $packageAsset
        Checksum = $checksumAsset
    }
}

function Receive-VerifiedPackage($Release, [string]$Directory) {
    $packagePath = Join-Path $Directory $Release.Package.name
    $checksumPath = Join-Path $Directory $Release.Checksum.name
    $headers = @{ "User-Agent" = "AllDebridClient-Updater" }

    Write-Host "==> Downloading $($Release.Package.name)"
    Invoke-WebRequest -Uri $Release.Package.browser_download_url `
                      -Headers $headers `
                      -OutFile $packagePath `
                      -UseBasicParsing
    Invoke-WebRequest -Uri $Release.Checksum.browser_download_url `
                      -Headers $headers `
                      -OutFile $checksumPath `
                      -UseBasicParsing

    $checksumText = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
    $checksumMatch = [regex]::Match($checksumText, '^([0-9a-fA-F]{64})\s+\*?(.+)$')
    if (-not $checksumMatch.Success -or $checksumMatch.Groups[2].Value.Trim() -cne $Release.Package.name) {
        throw "The release checksum file has an invalid format or filename."
    }

    $expectedHash = $checksumMatch.Groups[1].Value.ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $expectedHash) {
        throw "Release checksum verification failed. Expected $expectedHash, got $actualHash."
    }

    if (-not [string]::IsNullOrWhiteSpace($Release.Package.digest)) {
        $apiDigest = $Release.Package.digest.ToString().ToLowerInvariant()
        if ($apiDigest -cne "sha256:$actualHash") {
            throw "The downloaded package does not match the digest recorded by GitHub."
        }
    }

    Write-Host "==> Verified SHA-256 $actualHash"
    return $packagePath
}

function Expand-VerifiedPackage([string]$PackagePath, [string]$Destination, [Version]$ExpectedVersion) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $Destination | Out-Null

    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrWhiteSpace($entry.FullName)) {
                continue
            }

            $entryPath = [IO.Path]::GetFullPath((Join-Path $Destination $entry.FullName))
            Assert-StrictChildPath $Destination $entryPath
        }
    } finally {
        $archive.Dispose()
    }

    [IO.Compression.ZipFile]::ExtractToDirectory($PackagePath, $Destination)

    $requiredFiles = @(
        "AdbClient.Web.exe",
        "AdbClient.Web.dll",
        "appsettings.json",
        "service-install.bat",
        "service-firewall.ps1",
        "service-remove.bat",
        "update.ps1",
        "update.cmd"
    )
    foreach ($requiredFile in $requiredFiles) {
        $requiredPath = Join-Path $Destination $requiredFile
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "The release package is missing required file '$requiredFile'."
        }
    }

    $packageVersion = Get-ApplicationVersion $Destination
    if ($packageVersion -ne $ExpectedVersion) {
        throw "Release package version $packageVersion does not match expected version $ExpectedVersion."
    }
}

function Get-HealthUri([string]$SettingsPath) {
    $settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
    $managedService = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
    $environment = Get-ServiceEnvironmentValues $ServiceName

    $portValue = if ($null -ne $settings.Port) { $settings.Port.ToString() } else { "6500" }
    if ($environment.ContainsKey("Port")) {
        $portValue = $environment["Port"]
    }

    $commandLinePort = Get-CommandLineSetting $managedService.PathName "Port"
    if ($null -ne $commandLinePort) {
        $portValue = $commandLinePort
    }

    $port = 0
    if (-not [int]::TryParse($portValue, [ref]$port) -or $port -lt 1 -or $port -gt 65535) {
        throw "The service's effective Port value is invalid: '$portValue'."
    }

    $basePathValue = if ($null -ne $settings.BasePath) { $settings.BasePath.ToString() } else { "" }
    if ($environment.ContainsKey("BasePath")) {
        $basePathValue = $environment["BasePath"]
    }

    $commandLineBasePath = Get-CommandLineSetting $managedService.PathName "BasePath"
    if ($null -ne $commandLineBasePath) {
        $basePathValue = $commandLineBasePath
    }

    if ([string]::IsNullOrWhiteSpace($basePathValue) -and $environment.ContainsKey("BASE_PATH")) {
        $basePathValue = $environment["BASE_PATH"]
    }

    $basePath = if ([string]::IsNullOrWhiteSpace($basePathValue)) {
        ""
    } else {
        $basePathValue.Trim().Trim('/')
    }

    $hasInvalidSegment = @($basePath.Split('/') | Where-Object { $_ -in @(".", "..") }).Count -gt 0
    if ($basePath -ne "" -and
        ($basePath -notmatch '^[-A-Za-z0-9._~]+(?:/[-A-Za-z0-9._~]+)*$' -or $hasInvalidSegment)) {
        throw "The service's effective BasePath value is invalid: '$basePathValue'."
    }

    return "http://127.0.0.1:$port$(if ($basePath -eq '') { '' } else { "/$basePath" })/health"
}

function Wait-ForHealth([string]$Uri) {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)

    do {
        try {
            $response = Invoke-WebRequest -Uri $Uri -TimeoutSec 5 -UseBasicParsing
            if ($response.StatusCode -eq 200) {
                return
            }
        } catch {
            Start-Sleep -Seconds 2
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "The service did not become healthy at $Uri within 60 seconds."
}

function Install-StagedApplication(
    [string]$StagingDirectory,
    [string]$AppDirectory,
    [string]$BackupsDirectory,
    [Version]$CurrentVersion,
    [Version]$NewVersion,
    [bool]$WasRunning
) {
    $timestamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $suffix = [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $backupDirectory = Join-Path $BackupsDirectory "App-$timestamp-v$CurrentVersion-$suffix"
    $failedDirectory = Join-Path $BackupsDirectory "Failed-$timestamp-v$NewVersion-$suffix"
    $backupCreated = $false

    New-Item -ItemType Directory -Path $BackupsDirectory -Force | Out-Null
    $backupsInfo = Get-Item -LiteralPath $BackupsDirectory -Force
    if (($backupsInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to store rollback data in a reparse point: $BackupsDirectory"
    }

    try {
        if ($WasRunning) {
            Stop-Service -Name $ServiceName
            (Get-Service -Name $ServiceName).WaitForStatus(
                [System.ServiceProcess.ServiceControllerStatus]::Stopped,
                [TimeSpan]::FromSeconds(30))
        }

        Move-Item -LiteralPath $AppDirectory -Destination $backupDirectory
        $backupCreated = $true
        Move-Item -LiteralPath $StagingDirectory -Destination $AppDirectory

        if ($WasRunning) {
            Start-Service -Name $ServiceName
            (Get-Service -Name $ServiceName).WaitForStatus(
                [System.ServiceProcess.ServiceControllerStatus]::Running,
                [TimeSpan]::FromSeconds(30))
            Wait-ForHealth (Get-HealthUri (Join-Path $AppDirectory "appsettings.json"))
        }

        Write-Host "==> Updated AllDebrid Client to $NewVersion"
        Write-Host "==> Previous version retained at $backupDirectory"
        if (-not $WasRunning) {
            Write-Host "==> The service was stopped before the update and remains stopped."
        }
    } catch {
        $updateError = $_.Exception.Message
        $rollbackError = $null

        try {
            Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue

            if ($backupCreated -and (Test-Path -LiteralPath $backupDirectory -PathType Container)) {
                if (Test-Path -LiteralPath $AppDirectory -PathType Container) {
                    Move-Item -LiteralPath $AppDirectory -Destination $failedDirectory
                }

                Move-Item -LiteralPath $backupDirectory -Destination $AppDirectory
            }

            if ($WasRunning) {
                Start-Service -Name $ServiceName
                (Get-Service -Name $ServiceName).WaitForStatus(
                    [System.ServiceProcess.ServiceControllerStatus]::Running,
                    [TimeSpan]::FromSeconds(30))
                Wait-ForHealth (Get-HealthUri (Join-Path $AppDirectory "appsettings.json"))
            }
        } catch {
            $rollbackError = $_.Exception.Message
        }

        if ($null -ne $rollbackError) {
            throw "Update failed: $updateError Rollback also failed: $rollbackError"
        }

        if ($backupCreated) {
            throw "Update failed and the previous installation was restored: $updateError"
        }

        throw "Update failed before the application was replaced: $updateError"
    }
}

$exitCode = 0
$temporaryDirectory = $null
$stagingDirectory = $null

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw "This updater supports Windows installations only. Use the published container image on Docker."
    }

    $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        throw "Windows service '$ServiceName' was not found. Install the service before using this updater."
    }

    $serviceApplicationPath = Get-ServiceApplicationPath $service.PathName
    if ([string]::IsNullOrWhiteSpace($serviceApplicationPath)) {
        throw "Could not identify AdbClient.Web.dll or AdbClient.Web.exe in the service command line."
    }

    $serviceApplicationPath = [IO.Path]::GetFullPath($serviceApplicationPath)
    $serviceDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $serviceApplicationPath)).TrimEnd('\', '/')

    if ([string]::IsNullOrWhiteSpace($ApplicationDirectory)) {
        $ApplicationDirectory = $serviceDirectory
    }

    $ApplicationDirectory = [IO.Path]::GetFullPath($ApplicationDirectory).TrimEnd('\', '/')
    $driveRoot = [IO.Path]::GetPathRoot($ApplicationDirectory).TrimEnd('\', '/')
    if ($ApplicationDirectory -ieq $driveRoot) {
        throw "Refusing to update an application installed at a drive root."
    }

    if ($ApplicationDirectory -ine $serviceDirectory) {
        throw "Service '$ServiceName' runs from '$serviceDirectory', not '$ApplicationDirectory'."
    }

    if (-not (Test-Path -LiteralPath $ApplicationDirectory -PathType Container)) {
        throw "Application directory not found: $ApplicationDirectory"
    }

    $applicationInfo = Get-Item -LiteralPath $ApplicationDirectory -Force
    if (($applicationInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to replace an application directory that is a reparse point."
    }

    $settingsPath = Join-Path $ApplicationDirectory "appsettings.json"
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        throw "Application settings not found: $settingsPath"
    }

    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $serviceEnvironment = Get-ServiceEnvironmentValues $ServiceName
    Assert-PersistentPathsOutsideApplication $settings $ApplicationDirectory $serviceEnvironment $service.PathName

    $requiresElevation = -not $CheckOnly -and -not $ValidateOnly -and -not $WhatIfPreference
    if ($requiresElevation -and -not (Test-IsAdministrator)) {
        if ($Elevated) {
            throw "The elevated updater did not receive administrator privileges."
        }

        $elevatedExitCode = Start-ElevatedUpdater
        $Pause = $false
        exit $elevatedExitCode
    }

    $currentVersion = Get-ApplicationVersion $ApplicationDirectory
    $release = Get-LatestRelease

    Write-Host "Installed version: $currentVersion"
    Write-Host "Latest release:   $($release.Version)"

    if ($release.Version -lt $currentVersion) {
        throw "Refusing to downgrade version $currentVersion to $($release.Version)."
    }

    if ($CheckOnly) {
        if ($release.Version -gt $currentVersion) {
            Write-Host "An update is available."
        } else {
            Write-Host "AllDebrid Client is up to date."
        }
        return
    }

    if ($release.Version -eq $currentVersion -and -not $Force -and -not $ValidateOnly -and -not $WhatIfPreference) {
        Write-Host "AllDebrid Client is already up to date. Use -Force to reinstall this release."
        return
    }

    $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) "AllDebridClient-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
    $packagePath = Receive-VerifiedPackage $release $temporaryDirectory

    $applicationParent = Split-Path -Parent $ApplicationDirectory
    $stagingParent = if ($ValidateOnly -or $WhatIfPreference) {
        $temporaryDirectory
    } else {
        $applicationParent
    }
    $stagingDirectory = Join-Path $stagingParent ".alldebrid-client-update-$([Guid]::NewGuid().ToString('N'))"
    Assert-StrictChildPath $stagingParent $stagingDirectory
    Expand-VerifiedPackage $packagePath $stagingDirectory $release.Version

    Copy-Item -LiteralPath $settingsPath -Destination (Join-Path $stagingDirectory "appsettings.json") -Force
    Write-Host "==> Release package and preserved configuration validated"

    if ($ValidateOnly) {
        Write-Host "Validation completed without changing the installation."
        return
    }

    $action = "replace version $currentVersion with $($release.Version), restart '$ServiceName', and retain a rollback copy"
    if (-not $PSCmdlet.ShouldProcess($ApplicationDirectory, $action)) {
        return
    }

    $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    if ($service.State -notin @("Running", "Stopped")) {
        throw "Service '$ServiceName' is $($service.State). Wait for it to finish changing state, then retry."
    }

    $currentServiceApplicationPath = Get-ServiceApplicationPath $service.PathName
    if ([string]::IsNullOrWhiteSpace($currentServiceApplicationPath)) {
        throw "Service '$ServiceName' no longer has a recognizable application path."
    }

    $currentServicePath = [IO.Path]::GetFullPath($currentServiceApplicationPath)
    if ($currentServicePath -ine $serviceApplicationPath) {
        throw "Service '$ServiceName' changed its application path while the update was being prepared."
    }

    Set-Location ([IO.Path]::GetTempPath())
    $applicationName = Split-Path -Leaf $ApplicationDirectory
    $backupsName = if ($applicationName -ieq "App") { "Backups" } else { "$applicationName-Backups" }
    $backupsDirectory = Join-Path $applicationParent $backupsName
    Assert-StrictChildPath $applicationParent $backupsDirectory
    $wasRunning = $service.State -eq "Running"
    Install-StagedApplication $stagingDirectory `
                              $ApplicationDirectory `
                              $backupsDirectory `
                              $currentVersion `
                              $release.Version `
                              $wasRunning
    $stagingDirectory = $null
} catch {
    $exitCode = 1
    [Console]::Error.WriteLine("ERROR: $($_.Exception.Message)")
} finally {
    if ($null -ne $stagingDirectory -and (Test-Path -LiteralPath $stagingDirectory)) {
        $stagingParent = Split-Path -Parent $stagingDirectory
        Assert-StrictChildPath $stagingParent $stagingDirectory
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }

    if ($null -ne $temporaryDirectory -and (Test-Path -LiteralPath $temporaryDirectory)) {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        Assert-StrictChildPath $tempRoot $temporaryDirectory
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }

    if ($Pause) {
        Read-Host "Press Enter to close"
    }
}

exit $exitCode

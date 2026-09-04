[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "High")]
param(
    [string]$InstallRoot,
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$ServiceName = "AllDebridClient",
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
$projectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\', '/')

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

    foreach ($settingName in @("Port", "BasePath", "BASE_PATH")) {
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

function Get-ServiceHealthUri([string]$SettingsPath, [string]$Name) {
    $settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
    $managedService = Get-CimInstance Win32_Service -Filter "Name='$Name'" -ErrorAction Stop
    $environment = Get-ServiceEnvironmentValues $Name

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

function Assert-ChildPath([string]$Parent, [string]$Child) {
    $prefix = $Parent.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $Child.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside $Parent`: $Child"
    }
}

function Test-SameOrDescendantPath([string]$Parent, [string]$Candidate) {
    $normalizedParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    $normalizedCandidate = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')

    if ($normalizedCandidate.Equals($normalizedParent, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $prefix = $normalizedParent + [System.IO.Path]::DirectorySeparatorChar
    return $normalizedCandidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Resolve-ConfiguredPath([string]$Path, [string]$BasePath) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Get-ManagedServiceState([string]$Name) {
    $managedService = Get-CimInstance Win32_Service -Filter "Name='$Name'" -ErrorAction SilentlyContinue
    if ($null -eq $managedService) {
        throw "Windows service '$Name' was not found."
    }

    return $managedService.State
}

function Wait-ForServiceState([string]$Name, [string]$ExpectedState, [int]$TimeoutSeconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastState = $null

    do {
        $lastState = Get-ManagedServiceState $Name
        if ($lastState -eq $ExpectedState) {
            return
        }

        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Windows service '$Name' did not reach state '$ExpectedState' within $TimeoutSeconds seconds (last state: '$lastState')."
}

function Stop-ManagedService([string]$Name) {
    if ((Get-ManagedServiceState $Name) -eq "Stopped") {
        return
    }

    Stop-Service -Name $Name -ErrorAction Stop
    Wait-ForServiceState $Name "Stopped"
}

function Start-ManagedService([string]$Name) {
    if ((Get-ManagedServiceState $Name) -eq "Running") {
        return
    }

    Start-Service -Name $Name -ErrorAction Stop
    Wait-ForServiceState $Name "Running"
}

function Wait-ForHealth([string]$ServiceName, [string]$Uri) {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $lastError = "No HTTP response."

    do {
        try {
            $response = Invoke-WebRequest -Uri $Uri -TimeoutSec 5 -UseBasicParsing
            if ($response.StatusCode -eq 200) {
                return
            }

            $lastError = "HTTP $($response.StatusCode)."
        } catch {
            $lastError = $_.Exception.Message
        }

        if ((Get-ManagedServiceState $ServiceName) -eq "Stopped") {
            throw "Windows service '$ServiceName' stopped before its health check passed at $Uri. Last error: $lastError"
        }

        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "The service did not become healthy at $Uri within 45 seconds. Last error: $lastError"
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this deployment from an Administrator PowerShell session."
}

$service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
if ($null -eq $service) {
    throw "Windows service '$ServiceName' was not found. Install it before using the in-place deployment command."
}

$serviceApplicationPath = Get-ServiceApplicationPath $service.PathName
if ([string]::IsNullOrWhiteSpace($serviceApplicationPath)) {
    throw "Could not identify AdbClient.Web.dll or AdbClient.Web.exe in the service command line."
}

$serviceApplicationPath = [System.IO.Path]::GetFullPath($serviceApplicationPath)
$serviceAppDirectory = Split-Path -Parent $serviceApplicationPath

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    if ((Split-Path -Leaf $serviceAppDirectory) -ne "App") {
        throw "The service is not using the expected <install-root>\App layout. Pass -InstallRoot explicitly after verifying the installation."
    }

    $InstallRoot = Split-Path -Parent $serviceAppDirectory
}

$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/')
$driveRoot = [System.IO.Path]::GetPathRoot($InstallRoot).TrimEnd('\', '/')
if ($InstallRoot -ieq $driveRoot -or $InstallRoot -ieq $projectRoot) {
    throw "Refusing to deploy to a drive root or the project root: $InstallRoot"
}

$appDirectory = [System.IO.Path]::GetFullPath((Join-Path $InstallRoot "App"))
$backupsDirectory = [System.IO.Path]::GetFullPath((Join-Path $InstallRoot "Backups"))
$stagingDirectory = [System.IO.Path]::GetFullPath((Join-Path $InstallRoot ".staging-$([Guid]::NewGuid().ToString('N'))"))

Assert-ChildPath $InstallRoot $appDirectory
Assert-ChildPath $InstallRoot $backupsDirectory
Assert-ChildPath $InstallRoot $stagingDirectory

if ($serviceAppDirectory -ine $appDirectory) {
    throw "Service '$ServiceName' runs from $serviceAppDirectory, not the expected $appDirectory."
}

if (-not (Test-Path -LiteralPath $appDirectory -PathType Container)) {
    throw "Application directory not found: $appDirectory"
}

$currentSettingsPath = Join-Path $appDirectory "appsettings.json"
if (-not (Test-Path -LiteralPath $currentSettingsPath -PathType Leaf)) {
    throw "Startup configuration not found: $currentSettingsPath"
}

try {
    $currentSettings = Get-Content -LiteralPath $currentSettingsPath -Raw | ConvertFrom-Json
} catch {
    throw "Startup configuration is not valid JSON: $currentSettingsPath"
}

if ([string]::IsNullOrWhiteSpace($currentSettings.DataPath)) {
    throw "The installed startup configuration does not define DataPath: $currentSettingsPath"
}

$dataDirectory = Resolve-ConfiguredPath $currentSettings.DataPath $appDirectory
$persistentPaths = @($dataDirectory)

if (-not [string]::IsNullOrWhiteSpace($currentSettings.Database.Path)) {
    $persistentPaths += Resolve-ConfiguredPath $currentSettings.Database.Path $dataDirectory
}

if (-not [string]::IsNullOrWhiteSpace($currentSettings.Logging.File.Path)) {
    $persistentPaths += Resolve-ConfiguredPath $currentSettings.Logging.File.Path $dataDirectory
}

foreach ($persistentPath in $persistentPaths) {
    if (Test-SameOrDescendantPath $appDirectory $persistentPath) {
        throw "Persistent data path is inside the replaceable application directory. Move it outside $appDirectory before deploying: $persistentPath"
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content -LiteralPath (Join-Path $projectRoot "version.txt") -Raw).Trim()
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Invalid stable semantic version: $Version"
}

$initialServiceState = Get-ManagedServiceState $ServiceName
if ($initialServiceState -eq "Start Pending") {
    Wait-ForServiceState $ServiceName "Running"
    $initialServiceState = "Running"
} elseif ($initialServiceState -eq "Stop Pending") {
    Wait-ForServiceState $ServiceName "Stopped"
    $initialServiceState = "Stopped"
} elseif ($initialServiceState -notin @("Running", "Stopped")) {
    throw "Windows service '$ServiceName' must be running or stopped before deployment (current state: '$initialServiceState')."
}

$wasRunning = $initialServiceState -eq "Running"
$backupDirectory = [System.IO.Path]::GetFullPath((Join-Path $backupsDirectory "App-$((Get-Date).ToString('yyyyMMdd-HHmmss'))-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"))
$failedDirectory = [System.IO.Path]::GetFullPath((Join-Path $backupsDirectory "Failed-$((Get-Date).ToString('yyyyMMdd-HHmmss'))-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"))
Assert-ChildPath $backupsDirectory $backupDirectory
Assert-ChildPath $backupsDirectory $failedDirectory
$backupCreated = $false
$serviceStoppedForDeployment = $false

try {
    Write-Host "==> Building version $Version into staging"
    & (Join-Path $projectRoot "publish.ps1") -InstallPath $stagingDirectory -DataPath $dataDirectory -Version $Version

    Copy-Item -LiteralPath $currentSettingsPath -Destination (Join-Path $stagingDirectory "appsettings.json") -Force

    if (-not $PSCmdlet.ShouldProcess($appDirectory, "Stop $ServiceName, back up the current App directory, deploy version $Version, and restart")) {
        return
    }

    New-Item -ItemType Directory -Path $backupsDirectory -Force | Out-Null

    if ($wasRunning) {
        Stop-ManagedService $ServiceName
        $serviceStoppedForDeployment = $true
    }

    Move-Item -LiteralPath $appDirectory -Destination $backupDirectory
    $backupCreated = $true
    Move-Item -LiteralPath $stagingDirectory -Destination $appDirectory

    if ($wasRunning) {
        Start-ManagedService $ServiceName
        Wait-ForHealth $ServiceName (Get-ServiceHealthUri $currentSettingsPath $ServiceName)
        $serviceStoppedForDeployment = $false
    }

    Write-Host "==> Deployment complete: $appDirectory"
    Write-Host "==> Previous version retained at: $backupDirectory"
} catch {
    $deploymentError = $_
    $rollbackError = $null

    try {
        if ($backupCreated -and (Test-Path -LiteralPath $backupDirectory -PathType Container)) {
            Stop-ManagedService $ServiceName

            if (Test-Path -LiteralPath $appDirectory -PathType Container) {
                Move-Item -LiteralPath $appDirectory -Destination $failedDirectory
            }

            Move-Item -LiteralPath $backupDirectory -Destination $appDirectory
        }

        if ($wasRunning -and ($serviceStoppedForDeployment -or $backupCreated)) {
            Start-ManagedService $ServiceName
            Wait-ForHealth $ServiceName (Get-ServiceHealthUri $currentSettingsPath $ServiceName)
            $serviceStoppedForDeployment = $false
        }
    } catch {
        $rollbackError = $_
    }

    if ($null -ne $rollbackError) {
        throw "Deployment failed: $($deploymentError.Exception.Message) Rollback also failed: $($rollbackError.Exception.Message)"
    }

    throw $deploymentError
} finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Assert-ChildPath $InstallRoot $stagingDirectory
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}

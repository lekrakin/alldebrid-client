[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "High")]
param(
    [string]$InstallRoot,
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$ServiceName = "AllDebridClient",
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [Parameter(DontShow)]
    [switch]$Elevated
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
$projectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\', '/')

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertTo-SingleQuotedLiteral([string]$Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}

function Get-ElevatedDeploymentCommand([string]$ScriptPath, [string]$Root, [string]$Name, [string]$BuildVersion, [string]$OutputPath) {
    $invocation = @(
        '&', (ConvertTo-SingleQuotedLiteral $ScriptPath),
        '-Elevated -Confirm:$false',
        '-InstallRoot', (ConvertTo-SingleQuotedLiteral $Root),
        '-ServiceName', (ConvertTo-SingleQuotedLiteral $Name),
        '-Version', (ConvertTo-SingleQuotedLiteral $BuildVersion)
    ) -join ' '
    $outputLiteral = ConvertTo-SingleQuotedLiteral $OutputPath

    return @"
`$ErrorActionPreference = 'Stop'
& {
    try {
        $invocation
        exit 0
    } catch {
        `$_ | Out-String
        exit 1
    }
} *> $outputLiteral
"@
}

function Start-ElevatedDeployment([string]$ScriptPath, [string]$Root, [string]$Name, [string]$BuildVersion) {
    # Windows cannot redirect an elevated process's streams directly. Relay its output
    # through one temporary file; no helper script or permanent privileged task is needed.
    $outputPath = [IO.Path]::GetTempFileName()
    $reader = $null
    $process = $null
    try {
        $command = Get-ElevatedDeploymentCommand $ScriptPath $Root $Name $BuildVersion $outputPath
        $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $powerShell = Join-Path $PSHOME $(if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh.exe' } else { 'powershell.exe' })
        Write-Host '==> Approve the Windows administrator prompt to deploy. Build output will appear here.'
        try {
            $process = Start-Process -FilePath $powerShell `
                                     -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encodedCommand" `
                                     -WorkingDirectory (Split-Path -Parent $ScriptPath) `
                                     -Verb RunAs -WindowStyle Hidden -PassThru
        } catch {
            throw "Could not start deployment with administrator approval: $($_.Exception.Message)"
        }

        do {
            $hasExited = $process.WaitForExit(250)
            if ($null -eq $reader -and (Get-Item -LiteralPath $outputPath).Length -gt 0) {
                $stream = [IO.File]::Open($outputPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true)
            }
            if ($null -ne $reader) {
                while (-not $reader.EndOfStream) { Write-Host $reader.ReadLine() }
            }
        } while (-not $hasExited)

        if ($process.ExitCode -ne 0) {
            throw "Elevated deployment failed (exit code $($process.ExitCode)). See the output above."
        }
    } finally {
        if ($null -ne $reader) { $reader.Dispose() }
        if ($null -ne $process) { $process.Dispose() }
        Remove-Item -LiteralPath $outputPath -Force
    }
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

function Assert-PersistentPathsOutsideApplication([string[]]$PersistentPaths, [string]$ApplicationDirectory) {
    foreach ($persistentPath in $PersistentPaths) {
        if (Test-SameOrDescendantPath $ApplicationDirectory $persistentPath) {
            throw "Persistent data path is inside the replaceable application directory. Move it outside $ApplicationDirectory before deploying: $persistentPath"
        }
    }
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

$serviceEnvironment = Get-ServiceEnvironmentValues $ServiceName
$configuredDataPath = Get-ConfiguredValue $currentSettings 'DataPath' './data' $serviceEnvironment $service.PathName
if ([string]::IsNullOrWhiteSpace($configuredDataPath)) {
    throw "The service's effective DataPath must not be blank."
}

$dataDirectory = Resolve-ConfiguredPath $configuredDataPath.Trim() $appDirectory
$persistentPaths = @($dataDirectory)

$configuredDatabasePath = Get-ConfiguredValue $currentSettings 'Database:Path' $null $serviceEnvironment $service.PathName
if (-not [string]::IsNullOrWhiteSpace($configuredDatabasePath)) {
    $persistentPaths += Resolve-ConfiguredPath $configuredDatabasePath.Trim() $dataDirectory
}

$configuredLogPath = Get-ConfiguredValue $currentSettings 'Logging:File:Path' $null $serviceEnvironment $service.PathName
if (-not [string]::IsNullOrWhiteSpace($configuredLogPath)) {
    $persistentPaths += Resolve-ConfiguredPath $configuredLogPath.Trim() $dataDirectory
}

Assert-PersistentPathsOutsideApplication $persistentPaths $appDirectory

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
if (-not $PSCmdlet.ShouldProcess($appDirectory, "Build version $Version, stop $ServiceName, back up and replace App, and restore the service's running state")) {
    return
}

if (-not (Test-IsAdministrator)) {
    if ($Elevated) {
        throw "The elevated process did not receive administrator rights. No deployment was performed."
    }
    Start-ElevatedDeployment $PSCommandPath $InstallRoot $ServiceName $Version
    return
}

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

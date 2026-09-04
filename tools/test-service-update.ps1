$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.ServiceProcess
$repositoryDirectory = Split-Path -Parent $PSScriptRoot
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "adc-service-update-$([Guid]::NewGuid().ToString('N'))"))
$fixtureServiceName = 'AdbClientUpdateFixture'
$checks = 0

function Assert-FixturePath([string]$Path) {
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($fixtureRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Operation escaped the fixture: $resolvedPath"
    }
    for ($ancestor = $resolvedPath; $ancestor.Length -ge $fixtureRoot.Length; $ancestor = Split-Path -Parent $ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and
            ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Fixture path contains a reparse point: $ancestor"
        }
    }
}

function Write-FixtureFile([string]$Path, [string]$Content) {
    Assert-FixturePath $Path
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [IO.File]::WriteAllText($Path, $Content)
}

function Get-FixtureSnapshot([string]$Path) {
    Assert-FixturePath $Path
    return (@(Get-ChildItem -LiteralPath $Path -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($Path.Length)
        if ($_.PSIsContainer) { "$relative/" } else { "$relative=$((Get-FileHash -LiteralPath $_.FullName).Hash)" }
    }) -join '|')
}

try {
    foreach ($mode in @('update', 'deploy')) {
        & {
            $relativeScript = if ($mode -eq 'update') { 'server/AdbClient.Web/update.ps1' } else { 'deploy.ps1' }
            $tokens = $null
            $parseErrors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $repositoryDirectory $relativeScript), [ref]$tokens, [ref]$parseErrors)
            if ($parseErrors.Count) { throw "$relativeScript does not parse." }

            # Extract only the transactions and their helpers, never service discovery or elevation.
            $functionNames = @('Install-StagedApplication', 'Assert-ChildPath', 'Assert-StrictChildPath',
                'Get-ManagedServiceState', 'Wait-ForServiceState', 'Stop-ManagedService', 'Start-ManagedService')
            foreach ($definition in $ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst]
            }, $false)) {
                if ($functionNames -contains $definition.Name) { . ([scriptblock]::Create($definition.Extent.Text)) }
            }
            $transaction = @($ast.EndBlock.Statements | Where-Object {
                $_ -is [Management.Automation.Language.TryStatementAst]
            })[-1]
            $deployTransaction = [scriptblock]::Create($transaction.Extent.Text)
            $updateCleanup = [scriptblock]::Create($transaction.Finally.Statements.Extent.Text -join "`n")
            if ($mode -eq 'update') {
                $copyCommands = @($transaction.FindAll({ param($node)
                    $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Copy-Item'
                }, $false))
                if ($copyCommands.Count -ne 1) { throw 'Review the changed updater configuration-copy step.' }
                $copyConfiguration = [scriptblock]::Create($copyCommands[0].Extent.Text)
            }

            function Assert-FixtureService([string]$Name) {
                if ($Name -cne $fixtureServiceName) { throw "Unexpected service: $Name" }
            }
            function Stop-Service([string]$Name, [string]$ErrorAction) {
                Assert-FixtureService $Name
                $fixture.State = 'Stopped'
                $fixture.Stops++
            }
            function Start-Service([string]$Name, [string]$ErrorAction) {
                Assert-FixtureService $Name
                $fixture.State = 'Running'
                $fixture.Starts++
            }
            function Get-Service([string]$Name) {
                Assert-FixtureService $Name
                return $fixture
            }
            function Get-CimInstance([string]$ClassName, [string]$Filter, [string]$ErrorAction) {
                if ($ClassName -cne 'Win32_Service' -or $Filter -cne "Name='$fixtureServiceName'") {
                    throw 'Unexpected service discovery.'
                }
                return $fixture
            }
            function Get-HealthUri([string]$SettingsPath) {
                Assert-FixturePath $SettingsPath
                return 'http://fixture.invalid/health'
            }
            function Get-ServiceHealthUri([string]$SettingsPath, [string]$Name) {
                Assert-FixtureService $Name
                return Get-HealthUri $SettingsPath
            }
            function Wait-ForHealth {
                if ($args[-1] -cne 'http://fixture.invalid/health') { throw 'Unexpected health target.' }
                if ($mode -eq 'deploy') { Assert-FixtureService $args[0] }
                $fixture.HealthChecks++
                if ($case -eq 'health-failure' -and (Get-Content -LiteralPath (Join-Path $appDirectory 'payload.txt') -Raw) -eq 'new') {
                    throw 'Fixture health failure.'
                }
            }
            # Validate every destructive filesystem target, then perform the real operation.
            function Remove-Item([string]$LiteralPath, [switch]$Recurse, [switch]$Force) {
                Assert-FixturePath $LiteralPath
                Microsoft.PowerShell.Management\Remove-Item -LiteralPath $LiteralPath -Recurse:$Recurse -Force:$Force
            }
            function Move-Item([string]$LiteralPath, [string]$Destination) {
                Assert-FixturePath $LiteralPath
                Assert-FixturePath $Destination
                Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
                if ($case -eq 'missing-staging' -and $LiteralPath -eq $appDirectory -and $Destination -like "$backupsDirectory\App-*") {
                    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
                }
            }
            function Invoke-DeployTransaction {
                [CmdletBinding(SupportsShouldProcess)] param()
                . $deployTransaction
            }

            $cases = @('running', 'stopped', 'health-failure', 'missing-staging')
            if ($mode -eq 'deploy') { $cases += 'publisher-failure' }
            foreach ($case in $cases) {
                $InstallRoot = Join-Path $fixtureRoot "$mode-$case"
                $projectRoot = Join-Path $InstallRoot 'Source'
                $appDirectory = Join-Path $InstallRoot 'App'
                $stagingDirectory = Join-Path $InstallRoot 'Staging'
                $backupsDirectory = Join-Path $InstallRoot 'Backups'
                $backupDirectory = Join-Path $backupsDirectory 'App-fixture'
                $failedDirectory = Join-Path $backupsDirectory 'Failed-fixture'
                $dataDirectory = Join-Path $InstallRoot 'Data'
                $currentSettingsPath = Join-Path $appDirectory 'appsettings.json'
                $settingsPath = $currentSettingsPath
                $ServiceName = $fixtureServiceName
                $Version = '1.6.1'
                $wasRunning = $case -ne 'stopped'
                $backupCreated = $false
                $serviceStoppedForDeployment = $false
                $temporaryDirectory = $null
                $Pause = $false
                $fixture = [pscustomobject]@{
                    State = if ($wasRunning) { 'Running' } else { 'Stopped' }
                    Stops = 0; Starts = 0; HealthChecks = 0
                }
                $fixture | Add-Member ScriptMethod WaitForStatus {
                    param($ExpectedState, $Timeout)
                    if ($this.State -ne $ExpectedState.ToString()) { throw 'Unexpected service state.' }
                }
                Write-FixtureFile (Join-Path $appDirectory 'payload.txt') 'old'
                Write-FixtureFile $currentSettingsPath '{"DataPath":"../Data","Port":6500}'
                Write-FixtureFile (Join-Path $dataDirectory 'adbclient.db') 'database sentinel'
                Write-FixtureFile (Join-Path $dataDirectory 'downloads/movie.mkv') 'download sentinel'
                Write-FixtureFile (Join-Path $dataDirectory 'logs/adbclient.log') 'log sentinel'
                Write-FixtureFile (Join-Path $backupsDirectory 'Existing/payload.txt') 'preexisting backup'
                $oldApp = Get-FixtureSnapshot $appDirectory
                $oldData = Get-FixtureSnapshot $dataDirectory
                $oldBackup = Get-FixtureSnapshot (Join-Path $backupsDirectory 'Existing')
                $settingsHash = (Get-FileHash -LiteralPath $currentSettingsPath).Hash
                # Only the external publisher is replaced; deploy's transaction remains unchanged.
                Write-FixtureFile (Join-Path $projectRoot 'publish.ps1') @'
param($InstallPath, $DataPath, $Version)
Write-FixtureFile (Join-Path $InstallPath 'payload.txt') 'new'
Write-FixtureFile (Join-Path $InstallPath 'appsettings.json') '{}'
if ($case -eq 'publisher-failure') { throw 'Fixture publisher failure.' }
'@
                $failure = $null
                try {
                    if ($mode -eq 'update') {
                        & (Join-Path $projectRoot 'publish.ps1') -InstallPath $stagingDirectory
                        . $copyConfiguration
                        try {
                            Install-StagedApplication $stagingDirectory $appDirectory $backupsDirectory ([Version]'1.6.0') ([Version]$Version) $wasRunning 6>$null
                        } finally { . $updateCleanup }
                    } else { Invoke-DeployTransaction -Confirm:$false 6>$null }
                } catch { $failure = $_.Exception.Message }
                $failed = $case -notin @('running', 'stopped')
                if (($null -ne $failure) -ne $failed) { throw "$mode/$case had unexpected result: $failure" }
                if ($failed -and $failure -notmatch 'Fixture health failure|Fixture publisher failure|Staging') {
                    throw "$mode/$case failed for an unexpected reason: $failure"
                }
                if ((Get-FixtureSnapshot $dataDirectory) -cne $oldData -or
                    (Get-FixtureSnapshot (Join-Path $backupsDirectory 'Existing')) -cne $oldBackup -or
                    (Get-FileHash -LiteralPath $currentSettingsPath).Hash -cne $settingsHash) {
                    throw "$mode/$case changed persistent files, configuration, or a preexisting backup."
                }
                if ((Test-Path -LiteralPath $stagingDirectory) -or $fixture.State -ne $(if ($wasRunning) { 'Running' } else { 'Stopped' })) {
                    throw "$mode/$case left staging files or changed the initial service state."
                }
                $savedApps = @(Get-ChildItem -LiteralPath $backupsDirectory -Directory -Filter 'App-*')
                $failedApps = @(Get-ChildItem -LiteralPath $backupsDirectory -Directory -Filter 'Failed-*')
                if ($failed) {
                    if ((Get-FixtureSnapshot $appDirectory) -cne $oldApp -or $savedApps.Count -ne 0) { throw "$mode/$case did not restore the old application." }
                } elseif ($savedApps.Count -ne 1 -or (Get-FixtureSnapshot $savedApps[0].FullName) -cne $oldApp -or
                    (Get-Content -LiteralPath (Join-Path $appDirectory 'payload.txt') -Raw) -cne 'new') {
                    throw "$mode/$case did not install and retain the expected applications."
                }
                if ($failedApps.Count -ne $(if ($case -eq 'health-failure') { 1 } else { 0 })) { throw "$mode/$case retained an unexpected failed application." }
                if ($failedApps.Count -and (Get-Content -LiteralPath (Join-Path $failedApps[0].FullName 'payload.txt') -Raw) -cne 'new') { throw 'Failed application was not preserved.' }
                $expectedCalls = switch ($case) {
                    'running' { '1,1,1' }
                    'health-failure' { '2,2,2' }
                    'missing-staging' { if ($mode -eq 'update') { '2,1,1' } else { '1,1,1' } }
                    default { '0,0,0' }
                }
                if ("$($fixture.Stops),$($fixture.Starts),$($fixture.HealthChecks)" -cne $expectedCalls) {
                    throw "$mode/$case had unexpected stop/start/health calls."
                }
                $script:checks++
            }
        }
    }
} finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Assert-FixturePath (Join-Path $fixtureRoot 'cleanup-check')
        Microsoft.PowerShell.Management\Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
Write-Host "$checks update/deploy transaction scenarios passed on PowerShell $($PSVersionTable.PSVersion). Fixture files removed; no real services, network, builds, or installed data were used."

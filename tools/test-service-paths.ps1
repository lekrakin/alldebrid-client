$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$applicationFixture = 'C:\adb-path-fixture\App'
$externalFixture = 'C:\adb-path-fixture\Data'
$checks = 0

foreach ($relativeScript in @('deploy.ps1', 'server/AdbClient.Web/update.ps1')) {
    & {
        $tokens = $null
        $parseErrors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $projectDirectory $relativeScript), [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count -gt 0) {
            throw "$relativeScript does not parse: $($parseErrors.Message -join '; ')"
        }

        # Load only pure configuration/path functions, never either script's entrypoint.
        $functionNames = @(
            'Get-CommandLineSetting', 'Get-ConfiguredValue', 'Resolve-ConfiguredPath',
            'Resolve-ConfiguredFilePath', 'Test-SameOrDescendant', 'Test-SameOrDescendantPath',
            'Get-PersistentPaths', 'Assert-PersistentPathsOutsideApplication'
        )
        foreach ($definition in $ast.FindAll({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst]
        }, $false)) {
            if ($functionNames -contains $definition.Name) {
                . ([scriptblock]::Create($definition.Extent.Text))
            }
        }

        $isDeploy = $relativeScript -eq 'deploy.ps1'
        if ($isDeploy) {
            # Exercise the actual deploy guard statements, stopping before deployment work.
            $guardStatements = @()
            $collecting = $false
            foreach ($statement in $ast.EndBlock.Statements) {
                if ($statement.Extent.Text.StartsWith('$serviceEnvironment =')) {
                    $collecting = $true
                }
                if ($collecting) {
                    $guardStatements += $statement.Extent.Text
                    if ($statement.Extent.Text.StartsWith('Assert-PersistentPathsOutsideApplication ')) {
                        break
                    }
                }
            }
            if ($guardStatements.Count -ne 10) {
                throw 'The deploy guard changed; review which statements the test executes.'
            }
            $deployGuard = [scriptblock]::Create(($guardStatements -join "`n"))
        }

        $cases = @(
            @{ Name = 'JSON defaults'; Environment = @{}; Command = ''; Reject = $false },
            @{ Name = 'relative database and log'; Environment = @{ Database__Path = 'nested\state.db'; Logging__File__Path = 'logs\app.log' }; Command = ''; Reject = $false },
            @{ Name = 'external environment overrides internal JSON'; InternalJson = $true; Environment = @{ DataPath = $externalFixture }; Command = ''; Reject = $false },
            @{ Name = 'command overrides internal environment'; Environment = @{ DataPath = "$applicationFixture\data" }; Command = "--DataPath=$externalFixture"; Reject = $false },
            @{ Name = 'quoted external command'; Environment = @{}; Command = '--DataPath "C:\adb-path-fixture\Local Data"'; Reject = $false }
        )
        foreach ($key in @('DataPath', 'Database:Path', 'Logging:File:Path')) {
            $insidePath = "$applicationFixture\private.db"
            $environmentValues = @{}
            $environmentValues[$key.Replace(':', '__')] = $insidePath
            $cases += @{ Name = "$key environment inside App"; Environment = $environmentValues; Command = ''; Reject = $true }
            $cases += @{ Name = "$key command inside App"; Environment = @{}; Command = "--$key=$insidePath"; Reject = $true }
        }

        # Only the service-environment boundary is replaced; no registry or service calls run.
        function Get-ServiceEnvironmentValues { return $case.Environment }

        foreach ($case in $cases) {
            foreach ($workingDirectory in @($projectDirectory, [IO.Path]::GetPathRoot($projectDirectory))) {
                $currentSettings = [pscustomobject]@{
                    DataPath = if ($case.InternalJson) { "$applicationFixture\data" } else { $externalFixture }
                    Database = [pscustomobject]@{ Path = 'nested\state.db' }
                    Logging = [pscustomobject]@{ File = [pscustomobject]@{ Path = 'logs\app.log' } }
                }
                $appDirectory = $applicationFixture
                $ServiceName = 'ConfigurationFixture'
                $service = [pscustomobject]@{ PathName = "AdbClient.Web.exe $($case.Command)" }
                $rejected = $false
                Push-Location $workingDirectory
                try {
                    if ($isDeploy) {
                        . $deployGuard
                        $resolvedPaths = $persistentPaths
                    } else {
                        Assert-PersistentPathsOutsideApplication $currentSettings $appDirectory $case.Environment $service.PathName
                        $resolvedPaths = @(Get-PersistentPaths $currentSettings $appDirectory $case.Environment $service.PathName).Path
                    }
                } catch {
                    if (-not $_.Exception.Message.Contains('inside')) { throw }
                    $rejected = $true
                } finally {
                    Pop-Location
                }
                if ($rejected -ne $case.Reject) {
                    throw "$relativeScript failed '$($case.Name)': rejection was $rejected."
                }
                if (-not $rejected) {
                    $expectedRoot = if ($case.Name -eq 'quoted external command') { 'C:\adb-path-fixture\Local Data' } else { $externalFixture }
                    $expectedPaths = @($expectedRoot, "$expectedRoot\nested\state.db", "$expectedRoot\logs\app.log")
                    if (($resolvedPaths -join '|') -cne ($expectedPaths -join '|')) {
                        throw "$relativeScript failed '$($case.Name)': unexpected paths $resolvedPaths."
                    }
                }
                $script:checks++
            }
        }
    }
}
Write-Host "$checks service-path checks passed on PowerShell $($PSVersionTable.PSVersion). No services or files were changed."

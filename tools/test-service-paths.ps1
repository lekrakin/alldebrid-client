$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$applicationFixture = 'C:\adb-path-fixture\App'
$externalFixture = 'C:\adb-path-fixture\Data'
$checks = 0

foreach ($relativeScript in @('deploy.ps1', 'server/AdbClient.Web/update.ps1', 'publish.ps1')) {
    & {
        $tokens = $null
        $parseErrors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $projectDirectory $relativeScript), [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count -gt 0) {
            throw "$relativeScript does not parse: $($parseErrors.Message -join '; ')"
        }

        # Load only pure configuration/path functions, never a script's entrypoint.
        $functionNames = @(
            'Get-CommandLineSetting', 'Get-ConfiguredValue', 'Test-AbsolutePath', 'Resolve-ConfiguredPath',
            'Resolve-ConfiguredFilePath', 'Test-SameOrDescendant', 'Test-SameOrDescendantPath',
            'Get-PersistentPaths', 'Assert-PersistentPathsOutsideApplication',
            'Assert-ExistingPersistentPathsAbsolute'
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
        $isPublish = $relativeScript -eq 'publish.ps1'
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
            if ($guardStatements.Count -eq 0 -or
                -not $guardStatements[-1].StartsWith('Assert-PersistentPathsOutsideApplication ')) {
                throw 'The deploy guard changed; review which statements the test executes.'
            }
            $deployGuard = [scriptblock]::Create(($guardStatements -join "`n"))
        }

        # The entrypoint must reject ambiguous data before elevation or work that can mutate files.
        $entrypointCommands = @($ast.FindAll({
            param($node)
            $node -is [Management.Automation.Language.CommandAst]
        }, $true) | Where-Object {
            $ancestor = $_.Parent
            while ($null -ne $ancestor -and $ancestor -isnot [Management.Automation.Language.FunctionDefinitionAst]) {
                $ancestor = $ancestor.Parent
            }
            $null -eq $ancestor
        })
        $guardName = if ($isPublish) { 'Assert-ExistingPersistentPathsAbsolute' } else { 'Assert-PersistentPathsOutsideApplication' }
        $guardCommand = @($entrypointCommands | Where-Object { $_.GetCommandName() -eq $guardName })
        if ($guardCommand.Count -ne 1) { throw "$relativeScript must call its preflight guard exactly once." }
        foreach ($command in $entrypointCommands) {
            if ($command.GetCommandName() -in @('Start-ElevatedDeployment', 'Start-ElevatedUpdater', 'npm', 'dotnet',
                    'Receive-VerifiedPackage', 'New-Item', 'Stop-ManagedService', 'Install-StagedApplication') -and
                $command.Extent.StartOffset -lt $guardCommand[0].Extent.StartOffset) {
                throw "$relativeScript performs $($command.GetCommandName()) before validating persistent paths."
            }
        }
        $script:checks++

        $cases = @(
            @{ Name = 'omitted file overrides use DataPath defaults'; Json = @{}; Environment = @{}; Command = '' },
            @{ Name = 'blank file overrides use DataPath defaults'; Json = @{ 'Database:Path' = ''; 'Logging:File:Path' = ' ' }; Environment = @{}; Command = '' },
            @{ Name = 'absolute JSON overrides'; Json = @{ 'Database:Path' = 'C:\adb-path-fixture\Database\state.db'; 'Logging:File:Path' = 'C:\adb-path-fixture\Logs\app.log' }; Environment = @{}; Command = ''; ExpectedDatabase = 'C:\adb-path-fixture\Database\state.db'; ExpectedLog = 'C:\adb-path-fixture\Logs\app.log' },
            @{ Name = 'absolute UNC data'; Json = @{ DataPath = '\\server.example\share\Data' }; Environment = @{}; Command = ''; ExpectedData = '\\server.example\share\Data' },
            @{ Name = 'missing DataPath keeps ambiguous legacy default'; OmitDataPath = $true; Json = @{}; Environment = @{}; Command = ''; Error = 'relative or not fully qualified' }
        )
        if (-not $isPublish) {
            $cases += @(
                @{ Name = 'environment overrides relative JSON'; Json = @{ DataPath = '..\data' }; Environment = @{ DataPath = $externalFixture }; Command = '' },
                @{ Name = 'command overrides relative environment'; Json = @{}; Environment = @{ DataPath = 'data' }; Command = "--DataPath=$externalFixture" },
                @{ Name = 'quoted external command'; Json = @{}; Environment = @{}; Command = '--DataPath "C:\adb-path-fixture\Local Data"'; ExpectedData = 'C:\adb-path-fixture\Local Data' },
                @{ Name = 'absolute environment file overrides'; Json = @{}; Environment = @{ Database__Path = 'C:\adb-path-fixture\Database\state.db'; Logging__File__Path = 'C:\adb-path-fixture\Logs\app.log' }; Command = ''; ExpectedDatabase = 'C:\adb-path-fixture\Database\state.db'; ExpectedLog = 'C:\adb-path-fixture\Logs\app.log' },
                @{ Name = 'absolute command overrides relative files'; Json = @{}; Environment = @{ Database__Path = 'state.db'; Logging__File__Path = 'app.log' }; Command = '--Database:Path=C:\adb-path-fixture\Database\state.db --Logging:File:Path=C:\adb-path-fixture\Logs\app.log'; ExpectedDatabase = 'C:\adb-path-fixture\Database\state.db'; ExpectedLog = 'C:\adb-path-fixture\Logs\app.log' }
            )
        }

        foreach ($key in @('DataPath', 'Database:Path', 'Logging:File:Path')) {
            foreach ($relativePath in @('nested\state', '..\state', 'C:state', '\state', '/state', 'C:', '\\server')) {
                $json = @{}
                $json[$key] = $relativePath
                $cases += @{ Name = "$key relative JSON: $relativePath"; Json = $json; Environment = @{}; Command = ''; Error = 'relative or not fully qualified' }
                if (-not $isPublish) {
                    $environmentValues = @{}
                    $environmentValues[$key.Replace(':', '__')] = $relativePath
                    $cases += @{ Name = "$key relative environment: $relativePath"; Json = @{}; Environment = $environmentValues; Command = ''; Error = 'relative or not fully qualified' }
                    $cases += @{ Name = "$key relative command: $relativePath"; Json = @{}; Environment = @{}; Command = "--$key=$relativePath"; Error = 'relative or not fully qualified' }
                }
            }
            if (-not $isPublish) {
                $insidePath = "$applicationFixture\private.db"
                $json = @{}
                $json[$key] = $insidePath
                $cases += @{ Name = "$key JSON inside App"; Json = $json; Environment = @{}; Command = ''; Error = 'inside' }
                $environmentValues = @{}
                $environmentValues[$key.Replace(':', '__')] = $insidePath
                $cases += @{ Name = "$key environment inside App"; Json = @{}; Environment = $environmentValues; Command = ''; Error = 'inside' }
                $cases += @{ Name = "$key command inside App"; Json = @{}; Environment = @{}; Command = "--$key=$insidePath"; Error = 'inside' }
            }
        }

        # Only the service-environment boundary is replaced; no registry or service calls run.
        function Get-ServiceEnvironmentValues { return $case.Environment }

        foreach ($case in $cases) {
            foreach ($workingDirectory in @($projectDirectory, [IO.Path]::GetPathRoot($projectDirectory))) {
                $currentSettings = [pscustomobject]@{
                    DataPath = $externalFixture
                    Database = [pscustomobject]@{ Path = $null }
                    Logging = [pscustomobject]@{ File = [pscustomobject]@{ Path = $null } }
                }
                if ($case.OmitDataPath) { $currentSettings.PSObject.Properties.Remove('DataPath') }
                foreach ($key in $case.Json.Keys) {
                    switch ($key) {
                        'DataPath' { $currentSettings.DataPath = $case.Json[$key] }
                        'Database:Path' { $currentSettings.Database.Path = $case.Json[$key] }
                        'Logging:File:Path' { $currentSettings.Logging.File.Path = $case.Json[$key] }
                    }
                }
                $appDirectory = $applicationFixture
                $ServiceName = 'ConfigurationFixture'
                $service = [pscustomobject]@{ PathName = "AdbClient.Web.exe $($case.Command)" }
                $rejected = $false
                Push-Location $workingDirectory
                try {
                    if ($isPublish) {
                        Assert-ExistingPersistentPathsAbsolute $currentSettings
                    } elseif ($isDeploy) {
                        . $deployGuard
                        $resolvedPaths = $persistentPaths
                    } else {
                        Assert-PersistentPathsOutsideApplication $currentSettings $appDirectory $case.Environment $service.PathName
                        $resolvedPaths = @(Get-PersistentPaths $currentSettings $appDirectory $case.Environment $service.PathName).Path
                    }
                } catch {
                    if (-not $case.Error -or -not $_.Exception.Message.Contains($case.Error)) { throw }
                    $rejected = $true
                } finally {
                    Pop-Location
                }
                if ($rejected -ne [bool]$case.Error) {
                    throw "$relativeScript failed '$($case.Name)': rejection was $rejected."
                }
                if (-not $rejected -and -not $isPublish) {
                    $expectedRoot = if ($case.ExpectedData) { $case.ExpectedData } else { $externalFixture }
                    $expectedDatabase = if ($case.ExpectedDatabase) { $case.ExpectedDatabase } else { "$expectedRoot\adbclient.db" }
                    $expectedLog = if ($case.ExpectedLog) { $case.ExpectedLog } else { "$expectedRoot\adbclient.log" }
                    $expectedPaths = @($expectedRoot, $expectedDatabase, $expectedLog)
                    if (($resolvedPaths -join '|') -cne ($expectedPaths -join '|')) {
                        throw "$relativeScript failed '$($case.Name)': unexpected paths $resolvedPaths."
                    }
                }
                $script:checks++
            }
        }

        if ($isPublish) {
            # A fresh publish can choose a relative data directory; it exports the resolved absolute path.
            $resolved = Resolve-ConfiguredPath 'data' $applicationFixture
            if ($resolved -cne "$applicationFixture\data") { throw 'Fresh publish failed to resolve its chosen data directory.' }
            $settingsAssignment = @($ast.FindAll({
                param($node)
                $node -is [Management.Automation.Language.AssignmentStatementAst] -and
                $node.Extent.Text -eq '$settings.DataPath = $DataPath'
            }, $true))
            if ($settingsAssignment.Count -ne 1) { throw 'Publish must export the absolute data path into the staged settings.' }
            $script:checks++
        }
    }
}
Write-Host "$checks persistent-path checks passed on PowerShell $($PSVersionTable.PSVersion). No services or files were changed."

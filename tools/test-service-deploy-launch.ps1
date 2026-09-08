$ErrorActionPreference = 'Stop'
$repositoryDirectory = Split-Path -Parent $PSScriptRoot
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "adc-deploy-launch-$([Guid]::NewGuid().ToString('N'))"))
$checks = 0
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repositoryDirectory 'deploy.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'deploy.ps1 does not parse.' }

# Load the launch helpers and approval gate, never the installation/service entrypoint.
foreach ($definition in $ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst]
}, $false)) {
    if ($definition.Name -in @('ConvertTo-SingleQuotedLiteral', 'Get-ElevatedDeploymentCommand', 'Start-ElevatedDeployment')) {
        . ([scriptblock]::Create($definition.Extent.Text))
    }
}
$gates = @($ast.EndBlock.Statements | Where-Object {
    $_.Extent.Text.StartsWith('if (-not $PSCmdlet.ShouldProcess(') -or
    $_.Extent.Text.StartsWith('if (-not (Test-IsAdministrator))')
})
$transaction = @($ast.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.TryStatementAst]
})[-1]
if ($gates.Count -ne 2 -or $gates[1].Extent.EndOffset -gt $transaction.Extent.StartOffset) {
    throw 'Review the deployment gates: approval and elevation must precede the build transaction.'
}
$dispatch = [scriptblock]::Create(($gates.Extent.Text -join "`n") + "`n`$fixture.ReachedTransaction = `$true")

try {
    New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
    $fixtureScript = Join-Path $fixtureRoot "deploy 'fixture'.ps1"
    [IO.File]::WriteAllText($fixtureScript, @'
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param([string]$InstallRoot, [string]$ServiceName, [string]$Version, [switch]$Elevated)
Write-Host 'Fixture build output'
Start-Sleep -Milliseconds 350
Write-Warning 'Fixture warning'
@{ Root = $InstallRoot; Name = $ServiceName; Version = $Version; Elevated = [bool]$Elevated; Confirm = [bool]$PSBoundParameters['Confirm'] } | ConvertTo-Json -Compress
if ($Version -eq '9.9.9') { throw 'Fixture deployment failure.' }
if (-not $PSCmdlet.ShouldProcess('fixture', 'verify unattended child confirmation')) { throw 'Unexpected declined confirmation.' }
'@)

    # Inspect the requested UAC launch, but run only the harmless fixture WITHOUT elevation.
    function Start-Process {
        param($FilePath, $ArgumentList, $WorkingDirectory, $Verb, $WindowStyle, [switch]$PassThru)
        if ($Verb -ne 'RunAs' -or $WindowStyle -ne 'Hidden' -or -not $PassThru -or
            $ArgumentList -notmatch '^-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand (\S+)$') {
            throw 'Unexpected elevated process arguments.'
        }
        $command = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($Matches[1]))
        if (-not $command.Contains((ConvertTo-SingleQuotedLiteral $fixtureScript))) {
            throw 'Refusing to launch anything except the harmless fixture.'
        }
        $commandAst = [Management.Automation.Language.Parser]::ParseInput($command, [ref]$null, [ref]$null)
        $script:outputPath = $commandAst.EndBlock.Statements[-1].PipelineElements[0].Redirections[0].Location.Value
        if ($cancel) { throw [ComponentModel.Win32Exception]::new(1223) }
        return Microsoft.PowerShell.Management\Start-Process -FilePath $FilePath -ArgumentList $ArgumentList `
            -WorkingDirectory $WorkingDirectory -WindowStyle Hidden -PassThru
    }

    foreach ($case in @('success', 'child-failure', 'cancelled-uac')) {
        $cancel = $case -eq 'cancelled-uac'
        $outputPath = $null
        $version = if ($case -eq 'child-failure') { '9.9.9' } else { '1.6.1' }
        $root = "C:\Fixture's folder; `$null [test]\$([char]0x00e9)\App"
        $name = 'Fixture.Service-1'
        $failure = $null
        $reported = [Collections.Generic.List[string]]::new()
        try {
            Start-ElevatedDeployment $fixtureScript $root $name $version 6>&1 | ForEach-Object { $reported.Add($_.ToString()) }
        } catch { $failure = $_.Exception.Message }
        if ($null -eq $outputPath -or (Test-Path -LiteralPath $outputPath)) { throw "$case left temporary output behind." }
        if ($cancel) {
            if ($failure -notmatch 'Could not start deployment with administrator approval:') { throw 'Cancellation was not reported.' }
        } else {
            $output = $reported -join "`n"
            $jsonLine = @($reported | Where-Object { $_.StartsWith('{') })
            if ($jsonLine.Count -ne 1) { throw "$case did not relay fixture output: $output Failure: $failure" }
            $received = $jsonLine[0] | ConvertFrom-Json
            if ($received.Root -cne $root -or $received.Name -cne $name -or $received.Version -cne $version -or
                -not $received.Elevated -or $received.Confirm) { throw 'Arguments changed across the process boundary.' }
            if ($output -notmatch 'Fixture build output' -or $output -notmatch 'Fixture warning') { throw 'Output streams were lost.' }
            if ($case -eq 'child-failure') {
                if ($failure -notmatch 'exit code 1' -or $output -notmatch 'Fixture deployment failure') { throw 'Child failure did not reach the caller.' }
            } elseif ($null -ne $failure) { throw $failure }
        }
        $checks++
    }

    & {
        function Test-IsAdministrator { return $fixture.Admin }
        function Start-ElevatedDeployment { $fixture.Launches++ }
        function Invoke-FixtureDispatch {
            [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
            param([switch]$Elevated)
            $appDirectory = 'C:\Fixture\App'
            $Version = '1.6.1'
            $ServiceName = 'Fixture.Service'
            . $dispatch
        }
        foreach ($case in @('what-if', 'administrator', 'needs-elevation', 'failed-elevation')) {
            $fixture = [pscustomobject]@{ Admin = $case -eq 'administrator'; Launches = 0; ReachedTransaction = $false }
            $failure = $null
            try {
                Invoke-FixtureDispatch -Confirm:$false -WhatIf:($case -eq 'what-if') -Elevated:($case -eq 'failed-elevation')
            } catch { $failure = $_.Exception.Message }
            if ($fixture.Launches -ne $(if ($case -eq 'needs-elevation') { 1 } else { 0 }) -or
                $fixture.ReachedTransaction -ne ($case -eq 'administrator')) { throw "Unexpected dispatch for $case." }
            if ($case -eq 'failed-elevation') {
                if ($failure -notmatch 'did not receive administrator rights') { throw 'Failed elevation was not stopped.' }
            } elseif ($null -ne $failure) { throw $failure }
            $script:checks++
        }
    }
} finally {
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    if ((Split-Path -Parent $fixtureRoot) -ine $expectedParent -or
        (Split-Path -Leaf $fixtureRoot) -notmatch '^adc-deploy-launch-[a-f0-9]{32}$') {
        throw "Refusing to remove an unexpected fixture path: $fixtureRoot"
    }
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
Write-Host "$checks deployment-launch checks passed on PowerShell $($PSVersionTable.PSVersion). No UAC prompts, services, builds, or installed data were used; temporary files removed."

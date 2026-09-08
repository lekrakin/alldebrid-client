[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("Ensure", "Remove")]
    [string]$Action,
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ProgramPath
)

$ErrorActionPreference = "Stop"
$displayName = "AllDebridClient"
$groupName = "AllDebrid Client"
$description = "Managed by the AllDebrid Client Windows service installer."
$normalizedProgramPath = [IO.Path]::GetFullPath($ProgramPath)

if ($Action -eq "Ensure" -and -not (Test-Path -LiteralPath $normalizedProgramPath -PathType Leaf)) {
    throw "Application executable not found: '$normalizedProgramPath'."
}

function Test-ManagedRule($Rule) {
    if ($Rule.Direction -ne "Inbound" -or $Rule.Action -ne "Allow") {
        return $false
    }

    foreach ($filter in @(Get-NetFirewallApplicationFilter `
            -AssociatedNetFirewallRule $Rule `
            -ErrorAction Stop)) {
        if ([string]::IsNullOrWhiteSpace($filter.Program) -or $filter.Program -eq "Any") {
            continue
        }

        try {
            $ruleProgramPath = [IO.Path]::GetFullPath(
                [Environment]::ExpandEnvironmentVariables($filter.Program.Trim('"')))
        } catch {
            continue
        }

        if ($ruleProgramPath.Equals($normalizedProgramPath,
                                    [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

$namedRules = @(Get-NetFirewallRule `
    -PolicyStore PersistentStore `
    -DisplayName $displayName `
    -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -ieq $displayName })
$managedRules = @($namedRules | Where-Object { Test-ManagedRule $_ })

if ($Action -eq "Ensure") {
    if ($managedRules.Count -gt 1) {
        throw "Multiple managed firewall rules target '$normalizedProgramPath'. Remove the duplicates before installing the service."
    }

    if ($namedRules.Count -ne $managedRules.Count) {
        throw "A different firewall rule already uses the display name '$displayName'. It was left unchanged."
    }

    if ($managedRules.Count -eq 1) {
        Set-NetFirewallRule `
            -InputObject $managedRules[0] `
            -Description $description `
            -Direction Inbound `
            -Action Allow `
            -Program $normalizedProgramPath `
            -Enabled True `
            -Profile Any `
            -RemoteAddress Any | Out-Null

        Write-Host "Firewall rule '$displayName' is configured for this application."
        exit 0
    }

    New-NetFirewallRule `
        -PolicyStore PersistentStore `
        -DisplayName $displayName `
        -Group $groupName `
        -Description $description `
        -Direction Inbound `
        -Action Allow `
        -Program $normalizedProgramPath `
        -Enabled True `
        -Profile Any `
        -RemoteAddress Any | Out-Null
    Write-Host "Created firewall rule '$displayName' for this application."
    exit 10
}

if ($managedRules.Count -eq 0) {
    if ($namedRules.Count -eq 0) {
        Write-Host "The managed firewall rule is already absent."
    } else {
        Write-Host "Similarly named firewall rules do not target this application and were left unchanged."
    }

    exit 0
}

foreach ($rule in $managedRules) {
    Remove-NetFirewallRule -InputObject $rule -ErrorAction Stop
}

Write-Host "Removed the managed firewall rule for this application."

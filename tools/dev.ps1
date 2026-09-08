param(
    [ValidateSet("menu", "info", "deps", "rebuild", "frontend", "backend", "verify", "run", "publish", "docker")]
    [string]$Command = "menu",
    [string]$InstallPath,
    [switch]$SkipNpmCi,
    [switch]$SkipCache
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

$root = Split-Path -Parent $PSScriptRoot
$client = Join-Path $root "client"
$server = Join-Path $root "server"
$webProject = Join-Path $server "AdbClient.Web\AdbClient.Web.csproj"
$devData = Join-Path $root "data\dev"

if ([string]::IsNullOrWhiteSpace($InstallPath)) {
    $InstallPath = Join-Path $root "publish"
}

function Write-Header($Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Get-PortOwner($Port) {
    if ($null -eq (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue)) {
        return $null
    }

    $connection = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $connection) {
        return $null
    }

    $process = Get-Process -Id $connection.OwningProcess -ErrorAction SilentlyContinue
    if ($process) {
        return "$($process.ProcessName) (PID $($connection.OwningProcess))"
    }

    return "PID $($connection.OwningProcess)"
}

function Assert-PortAvailable($Port, $Purpose) {
    $owner = Get-PortOwner $Port
    if ($owner) {
        throw "Port $Port is already in use by $owner. Stop it before starting $Purpose."
    }
}

function Resolve-ToolPath($Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command -ne $null) {
        return $command.Source
    }

    if ($Name -ne "docker") {
        return $null
    }

    $candidates = @(
        (Join-Path $env:ProgramFiles "Docker\Docker\resources\bin\docker.exe")
    )

    $dockerProcesses = Get-Process -Name "Docker Desktop", "com.docker.backend" -ErrorAction SilentlyContinue |
        Where-Object { $_.Path }

    foreach ($dockerProcess in $dockerProcesses) {
        $processDirectory = Split-Path -Parent $dockerProcess.Path
        $parentDirectory = Split-Path -Parent $processDirectory
        $candidates += Join-Path $processDirectory "bin\docker.exe"
        $candidates += Join-Path $processDirectory "resources\bin\docker.exe"
        $candidates += Join-Path $parentDirectory "resources\bin\docker.exe"
    }

    return $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function Get-ToolStatus($Name) {
    $toolPath = Resolve-ToolPath $Name
    if ($null -eq $toolPath) {
        return "missing"
    }

    if ($Name -eq "dotnet") {
        $sdks = @(& $toolPath --list-sdks)
        if ($sdks.Count -eq 0) {
            return "$toolPath (SDK missing; runtimes only)"
        }
    }

    return $toolPath
}

function Test-RequiredCommand($Name) {
    $toolPath = Resolve-ToolPath $Name
    if ($null -eq $toolPath) {
        throw "Required command '$Name' was not found on PATH. Install it or open a shell where it is available."
    }

    if ($Name -eq "dotnet" -and @(& $toolPath --list-sdks).Count -eq 0) {
        throw "The .NET runtime is installed, but no .NET SDK is available. Install the .NET 10 SDK to build this project."
    }

    return $toolPath
}

function Invoke-ProjectCommand($Title, $WorkingDirectory, [string[]]$CommandLine) {
    Write-Header $Title
    $toolPath = Test-RequiredCommand $CommandLine[0]

    Push-Location $WorkingDirectory
    try {
        & $toolPath $CommandLine[1..($CommandLine.Length - 1)]
        if ($LASTEXITCODE -ne 0) {
            throw "Command '$($CommandLine -join ' ')' failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Show-Info {
    Write-Header "AllDebrid Client dev info"
    Write-Host "Root:       $root"
    Write-Host "Frontend:   $client"
    Write-Host "Backend:    $webProject"
    Write-Host "Web UI:     http://127.0.0.1:6500"
    Write-Host "Angular:    http://127.0.0.1:4200"
    Write-Host "Docker:     http://127.0.0.1:6500"
    Write-Host "Dev data:   $devData"
    Write-Host "Publish:    $InstallPath"
    Write-Host ""
    Write-Host "Prerequisites:"
    Write-Host "  dotnet:    $(Get-ToolStatus "dotnet")"
    Write-Host "  npm:       $(Get-ToolStatus "npm")"
    Write-Host "  docker:    $(Get-ToolStatus "docker")"
    Write-Host ""
    Write-Host "Listeners:"
    Write-Host "  port 6500: $(if (Get-PortOwner 6500) { Get-PortOwner 6500 } else { "available" })"
    Write-Host "  port 4200: $(if (Get-PortOwner 4200) { Get-PortOwner 4200 } else { "available" })"
    Write-Host ""
    Write-Host "Direct commands:"
    Write-Host "  .\dev.ps1 deps       Install frontend packages and restore backend"
    Write-Host "  .\dev.ps1 rebuild    Build frontend and backend"
    Write-Host "  .\dev.ps1 verify     Restore, build, test, lint, and format-check"
    Write-Host "  .\dev.ps1 run        Verify everything, then run backend locally"
    Write-Host "  .\dev.ps1 frontend   Run Angular dev server"
    Write-Host "  .\dev.ps1 backend    Run ASP.NET Core backend"
    Write-Host "  .\dev.ps1 docker     Rebuild and start Docker dev container"
    Write-Host "  .\dev.ps1 publish    Create a local Windows publish output"
}

function Show-ActionError($ErrorRecord) {
    Write-Host ""
    Write-Host "Action did not complete." -ForegroundColor Red
    Write-Host $ErrorRecord.Exception.Message -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Tip: run '.\dev.ps1 info' to see prerequisite status and local URLs."
}

function Install-Deps {
    if (-not $SkipNpmCi) {
        Invoke-ProjectCommand "Install frontend dependencies" $client @("npm", "ci")
    }

    Invoke-ProjectCommand "Restore backend dependencies" $root @("dotnet", "restore", "server")
}

function Invoke-Rebuild {
    if (-not $SkipNpmCi) {
        Invoke-ProjectCommand "Install frontend dependencies" $client @("npm", "ci")
    }

    Invoke-ProjectCommand "Build frontend" $client @("npm", "run", "build")
    Invoke-ProjectCommand "Restore backend dependencies" $root @("dotnet", "restore", "server")
    Invoke-ProjectCommand "Build backend" $root @("dotnet", "build", "--no-restore", "server")
}

function Invoke-Verify {
    Invoke-Rebuild
    Invoke-ProjectCommand "Test backend" $root @("dotnet", "test", "--no-build", "server")
    Invoke-ProjectCommand "Lint frontend" $client @("npm", "run", "lint")
    Invoke-ProjectCommand "Check frontend formatting" $client @("npm", "run", "format:check")
}

function Invoke-RunIfVerified {
    Invoke-Verify
    Write-Header "Verified. Starting backend"
    Write-Host "Backend URL: http://127.0.0.1:6500"
    Write-Host "Open another terminal and run '.\dev.ps1 frontend' for the Angular dev server."
    Start-Backend
}

function Start-Frontend {
    Assert-PortAvailable 4200 "the Angular dev server"
    Invoke-ProjectCommand "Run Angular dev server on http://127.0.0.1:4200" $client @("npm", "start")
}

function Start-Backend {
    Assert-PortAvailable 6500 "the ASP.NET Core backend"
    Invoke-ProjectCommand "Run backend on http://127.0.0.1:6500 with isolated dev data" $root @(
        "dotnet",
        "run",
        "--project",
        $webProject,
        "--",
        "--DataPath",
        $devData
    )
}

function Publish-Local {
    Write-Header "Publish local Windows install"
    & (Join-Path $root "publish.ps1") -InstallPath $InstallPath
}

function Start-DockerDev {
    Write-Header "Rebuild and start Docker dev container"
    $docker = Test-RequiredCommand "docker"
    $composeFile = Join-Path $root "tools\docker-compose.yml"
    $buildArguments = @("compose", "--file", $composeFile, "build")

    if ($SkipCache) {
        $buildArguments += "--no-cache"
    }

    & $docker @buildArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Docker Compose build failed with exit code $LASTEXITCODE."
    }

    & $docker compose --file $composeFile up --detach
    if ($LASTEXITCODE -ne 0) {
        throw "Docker Compose startup failed with exit code $LASTEXITCODE."
    }
}

function Show-Menu {
    while ($true) {
        Write-Host ""
        Write-Host "AllDebrid Client Dev Menu" -ForegroundColor Green
        Write-Host "1. MAIN: build, verify, then run backend if checks pass" -ForegroundColor Cyan
        Write-Host "2. Info and local URLs"
        Write-Host "3. Install/restore dependencies"
        Write-Host "4. Rebuild frontend and backend"
        Write-Host "5. Verify project only"
        Write-Host "6. Run frontend dev server only"
        Write-Host "7. Run backend dev server only"
        Write-Host "8. Create local Windows publish output"
        Write-Host "9. Rebuild/start Docker dev container"
        Write-Host "Q. Quit"
        $choice = Read-Host "Select [1]"
        if ([string]::IsNullOrWhiteSpace($choice)) {
            $choice = "1"
        }

        try {
            switch ($choice.ToLowerInvariant()) {
                "1" { Invoke-RunIfVerified }
                "2" { Show-Info }
                "3" { Install-Deps }
                "4" { Invoke-Rebuild }
                "5" { Invoke-Verify }
                "6" { Start-Frontend }
                "7" { Start-Backend }
                "8" { Publish-Local }
                "9" { Start-DockerDev }
                "q" { return }
                default { Write-Host "Unknown choice: $choice" -ForegroundColor Yellow }
            }
        }
        catch {
            Show-ActionError $_
            Read-Host "Press Enter to return to the menu"
        }
    }
}

switch ($Command) {
    "menu" { Show-Menu }
    "info" { Show-Info }
    "deps" { Install-Deps }
    "rebuild" { Invoke-Rebuild }
    "frontend" { Start-Frontend }
    "backend" { Start-Backend }
    "verify" { Invoke-Verify }
    "run" { Invoke-RunIfVerified }
    "publish" { Publish-Local }
    "docker" { Start-DockerDev }
}

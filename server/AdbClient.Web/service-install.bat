@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "serviceName=AllDebridClient"
set "executable=%~dp0AdbClient.Web.exe"
set "firewallScript=%~dp0service-firewall.ps1"
set "firewallCreated=0"

net.exe session >nul 2>&1
if errorlevel 1 goto :administratorRequired

if not exist "%executable%" (
    echo ERROR: Application executable not found: "%executable%"
    exit /b 1
)

if not exist "%firewallScript%" (
    echo ERROR: Firewall management script not found: "%firewallScript%"
    exit /b 1
)

sc.exe query "%serviceName%" >nul 2>&1
if not errorlevel 1 (
    echo ERROR: Windows service "%serviceName%" is already installed.
    exit /b 1
)

echo Checking firewall rule...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%firewallScript%" -Action Ensure -ProgramPath "%executable%"
set "firewallResult=!errorlevel!"
if "!firewallResult!"=="10" (
    set "firewallCreated=1"
) else if not "!firewallResult!"=="0" (
    echo ERROR: Could not configure the managed firewall rule.
    exit /b 1
)

echo Installing Windows service...
sc.exe create "%serviceName%" binPath= "\"%executable%\"" start= auto >nul
if errorlevel 1 goto :installFailed

sc.exe start "%serviceName%" >nul
if errorlevel 1 goto :startFailed

call :waitForState 4 30
if errorlevel 1 goto :startFailed

echo Windows service "%serviceName%" is installed and running.
exit /b 0

:startFailed
echo ERROR: Windows service "%serviceName%" did not start. Removing the incomplete installation.
sc.exe stop "%serviceName%" >nul 2>&1
call :waitForState 1 15 >nul 2>&1
sc.exe delete "%serviceName%" >nul 2>&1
call :waitForDeletion 15 >nul 2>&1

:installFailed
if "!firewallCreated!"=="1" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%firewallScript%" -Action Remove -ProgramPath "%executable%" >nul 2>&1
exit /b 1

:administratorRequired
echo ERROR: Administrator privileges are required.
echo Right-click this script and select "Run as administrator".
exit /b 1

:waitForState
set "targetState=%~1"
set "attempts=%~2"
for /L %%I in (1,1,%attempts%) do (
    sc.exe query "%serviceName%" 2>nul | findstr.exe /R /C:":[ ]*%targetState%[ ]" >nul
    if not errorlevel 1 exit /b 0
    timeout.exe /t 1 /nobreak >nul
)
exit /b 1

:waitForDeletion
set "attempts=%~1"
for /L %%I in (1,1,%attempts%) do (
    sc.exe query "%serviceName%" >nul 2>&1
    if errorlevel 1 exit /b 0
    timeout.exe /t 1 /nobreak >nul
)
exit /b 1

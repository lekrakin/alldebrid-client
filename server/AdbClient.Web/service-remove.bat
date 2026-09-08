@echo off
setlocal EnableExtensions

set "serviceName=AllDebridClient"
set "executable=%~dp0AdbClient.Web.exe"
set "firewallScript=%~dp0service-firewall.ps1"

net.exe session >nul 2>&1
if errorlevel 1 goto :administratorRequired

if not exist "%firewallScript%" (
    echo ERROR: Firewall management script not found: "%firewallScript%"
    exit /b 1
)

sc.exe query "%serviceName%" >nul 2>&1
if errorlevel 1 goto :removeFirewall

echo Stopping Windows service...
sc.exe stop "%serviceName%" >nul 2>&1
call :waitForState 1 30
if errorlevel 1 (
    echo ERROR: Windows service "%serviceName%" did not stop. It was not removed.
    exit /b 1
)

echo Removing Windows service...
sc.exe delete "%serviceName%" >nul
if errorlevel 1 (
    echo ERROR: Windows service "%serviceName%" could not be removed.
    exit /b 1
)

call :waitForDeletion 30
if errorlevel 1 (
    echo ERROR: Windows service "%serviceName%" is still pending deletion. Close service-management tools and run this script again.
    exit /b 1
)

:removeFirewall
echo Checking firewall rule...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%firewallScript%" -Action Remove -ProgramPath "%executable%"
if errorlevel 1 (
    echo ERROR: The managed firewall rule could not be removed.
    exit /b 1
)

echo Windows service "%serviceName%" and its managed firewall rule are removed.
exit /b 0

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

@echo off
rem Builds bin\GHubKeyboard.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem Run with "release" to also create dist\GHubKeyboard-v<version>.zip for GitHub Releases.
setlocal
cd /d "%~dp0"

set VERSION=1.0.0
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo .NET Framework 4.x C# compiler not found.
    exit /b 1
)

if not exist bin mkdir bin
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ ^
    /r:System.Web.Extensions.dll /r:System.Core.dll ^
    /win32manifest:src\app.manifest ^
    /out:bin\GHubKeyboard.exe src\GHubKeyboard.cs
if errorlevel 1 exit /b 1
echo Built bin\GHubKeyboard.exe

if /i not "%1"=="release" exit /b 0

if not exist dist mkdir dist
set ZIP=dist\GHubKeyboard-v%VERSION%.zip
if exist "%ZIP%" del "%ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path 'bin\GHubKeyboard.exe','README.md','LICENSE' -DestinationPath '%ZIP%'"
if errorlevel 1 exit /b 1
echo Created %ZIP%

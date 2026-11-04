@echo off
rem ============================================================================
rem  CoRFAGen - builds the add-in for Revit 2024-2027 (Release) and
rem  packs it into Setup\CoRFAGen_<ver>.msi  (WiX Toolset v5).
rem  Requirements: .NET SDK 10 (installed with Visual Studio 2026), internet for NuGet.
rem  Just double-click this file. Log: Setup\build.log
rem ============================================================================
setlocal
cd /d "%~dp0"
set "VER=1.7.2"
if not exist "Setup" mkdir "Setup"
set "LOG=%~dp0Setup\build.log"
echo CoRFAGen MSI build %date% %time% > "%LOG%"
where dotnet >> "%LOG%" 2>&1
dotnet --list-sdks >> "%LOG%" 2>&1

set FAILED=
for %%V in (24 25 26 27) do (
  echo. >> "%LOG%"
  echo ===== Release R%%V ===== >> "%LOG%"
  echo Building Revit 20%%V ...
  rem --no-incremental: an incremental build can keep old icons, it does not always notice a replaced PNG
  dotnet build "CoRFAGen\CoRFAGen.csproj" -c "Release R%%V" -p:SkipDeploy=true --no-incremental -nologo -v:minimal >> "%LOG%" 2>&1
  if errorlevel 1 (
    echo FAILED: Release R%%V >> "%LOG%"
    set FAILED=1
  )
)
if defined FAILED goto fail

echo. >> "%LOG%"
echo ===== MSI ===== >> "%LOG%"
echo Building MSI ...
dotnet build "Installer\CoRFAGen.Installer.wixproj" -c Release -p:ProductVersion=%VER% -nologo -v:minimal >> "%LOG%" 2>&1
if errorlevel 1 goto fail

for /r "Installer\bin" %%F in (*.msi) do copy /y "%%F" "Setup\" >> "%LOG%"
echo SUCCESS: Setup\CoRFAGen_%VER%.msi >> "%LOG%"
echo.
echo SUCCESS: %~dp0Setup\CoRFAGen_%VER%.msi
timeout /t 10
exit /b 0

:fail
echo BUILD FAILED >> "%LOG%"
echo.
echo BUILD FAILED - see %LOG%
timeout /t 30
exit /b 1

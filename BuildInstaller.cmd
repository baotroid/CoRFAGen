@echo off
rem ============================================================================
rem  BeGenSt PDF&Pic2RFA - builds the add-in for Revit 2024-2027 (Release) and
rem  packs it into Setup\BeGenSt_PDFPic2RFA_<ver>.msi  (WiX Toolset v5).
rem  Requirements: .NET SDK 10 (installed with Visual Studio 2026), internet for NuGet.
rem  Just double-click this file. Log: Setup\build.log
rem ============================================================================
setlocal
cd /d "%~dp0"
set "VER=1.4.4"
if not exist "Setup" mkdir "Setup"
set "LOG=%~dp0Setup\build.log"
echo BeGenSt MSI build %date% %time% > "%LOG%"
where dotnet >> "%LOG%" 2>&1
dotnet --list-sdks >> "%LOG%" 2>&1

set FAILED=
for %%V in (24 25 26 27) do (
  echo. >> "%LOG%"
  echo ===== Release R%%V ===== >> "%LOG%"
  echo Building Revit 20%%V ...
  dotnet build "BeGenSt\BeGenSt.csproj" -c "Release R%%V" -p:SkipDeploy=true -nologo -v:minimal >> "%LOG%" 2>&1
  if errorlevel 1 (
    echo FAILED: Release R%%V >> "%LOG%"
    set FAILED=1
  )
)
if defined FAILED goto fail

echo. >> "%LOG%"
echo ===== MSI ===== >> "%LOG%"
echo Building MSI ...
dotnet build "Installer\BeGenSt.Installer.wixproj" -c Release -p:ProductVersion=%VER% -nologo -v:minimal >> "%LOG%" 2>&1
if errorlevel 1 goto fail

for /r "Installer\bin" %%F in (*.msi) do copy /y "%%F" "Setup\" >> "%LOG%"
echo SUCCESS: Setup\BeGenSt_PDFPic2RFA_%VER%.msi >> "%LOG%"
echo.
echo SUCCESS: %~dp0Setup\BeGenSt_PDFPic2RFA_%VER%.msi
timeout /t 10
exit /b 0

:fail
echo BUILD FAILED >> "%LOG%"
echo.
echo BUILD FAILED - see %LOG%
timeout /t 30
exit /b 1

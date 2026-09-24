@echo off
rem Builds FreeRdpBridge.dll (x64 Release).
rem Usage: build.cmd [FreeRDP install dir]   (default: ..\..\..\FreeRDP\build\install)
setlocal
pushd "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer"
for /f "usebackq delims=" %%i in (`.\vswhere.exe -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
popd
if not defined VSDIR (
  echo Visual Studio with C++ tools not found
  exit /b 1
)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d "%~dp0"
set "EXTRA="
if not "%~1"=="" set "EXTRA=-DFREERDP_INSTALL_DIR=%~1"
cmake -G Ninja -B build -S . -DCMAKE_BUILD_TYPE=Release %EXTRA% || exit /b 1
cmake --build build || exit /b 1
echo Built: %~dp0build\FreeRdpBridge.dll

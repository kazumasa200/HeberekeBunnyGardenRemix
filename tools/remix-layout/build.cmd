@echo off
rem Prints sizes/offsets/StructType values of include/remix_c.h. Remix/RemixApi.cs (Layout.Check, StructType) must match.
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
for /f "usebackq delims=" %%i in (`vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d "%~dp0"
cl /nologo /utf-8 /W3 layout.c /Fo:"%TEMP%\remix-layout.obj" /link /OUT:"%TEMP%\remix-layout.exe" >nul || exit /b 1
"%TEMP%\remix-layout.exe"

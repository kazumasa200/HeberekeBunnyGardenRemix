@echo off
rem Builds d3d11.dll and dxgi.dll: pure export forwarders to the Remix Plus runtime (d3d9.dll next to the game's exe).
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
for /f "usebackq delims=" %%i in (`vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d "%~dp0"
if not exist out mkdir out
cl /nologo /c /O1 /GS- /Fo:out\ d3d11_forward.cpp dxgi_forward.cpp || exit /b 1
link /nologo /DLL /NOENTRY /NODEFAULTLIB /MACHINE:X64 /OUT:out\d3d11.dll out\d3d11_forward.obj || exit /b 1
link /nologo /DLL /NOENTRY /NODEFAULTLIB /MACHINE:X64 /OUT:out\dxgi.dll out\dxgi_forward.obj || exit /b 1
del out\*.obj out\*.exp out\*.lib 2>nul
echo built out\d3d11.dll out\dxgi.dll

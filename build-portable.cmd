@echo off
cd /d "%~dp0"
echo.
echo === Building portable NoahDisk.exe ===
echo (first run needs internet: it downloads the .NET win-x64 runtime)
echo.
dotnet publish gui -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist-gui-portable
echo.
if exist "dist-gui-portable\NoahDisk.exe" echo DONE: %~dp0dist-gui-portable\NoahDisk.exe
if not exist "dist-gui-portable\NoahDisk.exe" echo BUILD FAILED - check internet and that .NET 9 SDK is installed.
echo.
pause

@echo off
setlocal
cd /d "%~dp0"
dotnet --list-sdks
dotnet publish "src\DTLocalStudio\DTLocalStudio.csproj" -c Release -r win-x64 --self-contained true -o "publish\win-x64"
if errorlevel 1 exit /b 1
echo Build completed. Run publish\win-x64\DTLocalStudio.exe to test startup.

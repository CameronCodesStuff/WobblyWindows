@echo off
REM Builds a single-file WobblyWindows.exe into .\dist
REM Requires the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0

dotnet publish -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true ^
  -o dist

echo.
echo Done. Run dist\WobblyWindows.exe
pause

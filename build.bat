@echo off
setlocal
set "PROJECT=%~dp0UI\ExplorerPlusPlus.csproj"

dotnet build "%PROJECT%" %*
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo Build failed with exit code %RESULT%.
if not "%RESULT%"=="0" pause
exit /b %RESULT%

@echo off
setlocal
set "PROJECT=%~dp0UI\ExplorerPlusPlus.csproj"

dotnet clean "%PROJECT%"
if errorlevel 1 (
    echo Clean failed.
    pause
    exit /b 1
)

dotnet build "%PROJECT%" %*
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo Rebuild failed with exit code %RESULT%.
if not "%RESULT%"=="0" pause
exit /b %RESULT%

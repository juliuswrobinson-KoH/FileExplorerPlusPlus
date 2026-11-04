@echo off
setlocal
set "PROJECT=%~dp0UI\ExplorerPlusPlus.csproj"

dotnet run --project "%PROJECT%" -- %*
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" (
    echo Run failed with exit code %RESULT%.
    pause
)
exit /b %RESULT%

@echo off
setlocal

set OUTPUT=%~dp0Build\Debug

echo [Shikigami] Cleaning Debug build...
if exist "%OUTPUT%" rmdir /s /q "%OUTPUT%"

echo [Shikigami] Building Server (Debug)...
dotnet publish src\Shikigami.Server\Shikigami.Server.csproj -c Debug -o "%OUTPUT%\Server" --no-self-contained
if errorlevel 1 goto :fail

echo [Shikigami] Building Runner (Debug)...
dotnet publish src\Shikigami.Runner\Shikigami.Runner.csproj -c Debug -o "%OUTPUT%\Runner" --no-self-contained
if errorlevel 1 goto :fail

echo.
echo [Shikigami] Debug build complete:
echo   Server: %OUTPUT%\Server\Shikigami.Server.exe
echo   Runner: %OUTPUT%\Runner\Shikigami.Runner.exe
goto :end

:fail
echo.
echo [Shikigami] BUILD FAILED
exit /b 1

:end
endlocal

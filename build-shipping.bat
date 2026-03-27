@echo off
setlocal

set OUTPUT=%~dp0Build\Shipping

echo [Shikigami] Cleaning Shipping build...
if exist "%OUTPUT%" rmdir /s /q "%OUTPUT%"

echo [Shikigami] Building Server (Release)...
dotnet publish src\Shikigami.Server\Shikigami.Server.csproj -c Release -o "%OUTPUT%\Server" --no-self-contained
if errorlevel 1 goto :fail

echo [Shikigami] Building Runner (Release)...
dotnet publish src\Shikigami.Runner\Shikigami.Runner.csproj -c Release -o "%OUTPUT%\Runner" --no-self-contained
if errorlevel 1 goto :fail

echo.
echo [Shikigami] Shipping build complete:
echo   Server: %OUTPUT%\Server\Shikigami.Server.exe
echo   Runner: %OUTPUT%\Runner\Shikigami.Runner.exe
goto :end

:fail
echo.
echo [Shikigami] BUILD FAILED
exit /b 1

:end
endlocal

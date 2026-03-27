@echo off
setlocal

set MCP_ROOT=%USERPROFILE%\.claude\MCPs\ShikigamiMCP
set BUILD=%~dp0Build\Shipping

:: Check build exists
if not exist "%BUILD%\Server\Shikigami.Server.exe" (
    echo [Shikigami] Shipping build not found. Run build-shipping.bat first.
    exit /b 1
)
if not exist "%BUILD%\Runner\Shikigami.Runner.exe" (
    echo [Shikigami] Shipping build not found. Run build-shipping.bat first.
    exit /b 1
)

:: Create target directories
echo [Shikigami] Installing to %MCP_ROOT%...
if exist "%MCP_ROOT%\Server" rmdir /s /q "%MCP_ROOT%\Server"
if exist "%MCP_ROOT%\Runner" rmdir /s /q "%MCP_ROOT%\Runner"
mkdir "%MCP_ROOT%\Server" 2>nul
mkdir "%MCP_ROOT%\Runner" 2>nul

:: Copy files
echo [Shikigami] Copying Server...
xcopy "%BUILD%\Server\*" "%MCP_ROOT%\Server\" /s /e /q /y >nul

echo [Shikigami] Copying Runner...
xcopy "%BUILD%\Runner\*" "%MCP_ROOT%\Runner\" /s /e /q /y >nul

echo.
echo [Shikigami] Installation complete:
echo   Server: %MCP_ROOT%\Server\Shikigami.Server.exe
echo   Runner: %MCP_ROOT%\Runner\Shikigami.Runner.exe
echo.
echo   First time? Register once with:
echo   claude mcp add ShikigamiMCP -- "%MCP_ROOT%\Server\Shikigami.Server.exe"

endlocal

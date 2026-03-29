@echo off
setlocal

set MCP_ROOT=%USERPROFILE%\.claude\MCPs\ShikigamiMCP
set BUILD=%~dp0Build\Shipping

if not exist "%BUILD%\Runner\Shikigami.Runner.exe" (
    echo [Shikigami] Shipping build not found. Run build-shipping.bat first.
    exit /b 1
)

:: Create target directories
echo [Shikigami] Installing to %MCP_ROOT%...
if exist "%MCP_ROOT%\Runner" rmdir /s /q "%MCP_ROOT%\Runner"
mkdir "%MCP_ROOT%\Runner" 2>nul

:: Copy files (robocopy /MIR = mirror, reliable with all file types)
echo [Shikigami] Copying Runner...
robocopy "%BUILD%\Runner" "%MCP_ROOT%\Runner" /MIR /NJH /NJS /NP /NFL /NDL >nul

echo.
echo [Shikigami] Installation complete:
echo   Runner: %MCP_ROOT%\Runner\Shikigami.Runner.exe
echo.

endlocal

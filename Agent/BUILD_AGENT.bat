@echo off
setlocal
cd /d "%~dp0"
echo.
echo Iniciando BUILD_AGENT.ps1 ...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BUILD_AGENT.ps1"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
    echo.
    echo O script terminou com erro (codigo %RC%).
    echo Veja o arquivo build.log na pasta Agent para detalhes.
    echo.
    pause
)
exit /b %RC%

@echo off
cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -File "%~dp0BUILD_AGENT.ps1"

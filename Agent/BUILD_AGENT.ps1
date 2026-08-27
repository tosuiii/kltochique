$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "=== EmpresaMonitor - Gerador do Agent ===" -ForegroundColor Cyan
Write-Host ""

$server = Read-Host "Cole a URL do Netlify (ex: https://meusite.netlify.app)"
$agentKey = Read-Host "Digite a AGENT_KEY configurada no Netlify"

if ([string]::IsNullOrWhiteSpace($server) -or [string]::IsNullOrWhiteSpace($agentKey)) {
    Write-Host "URL ou AGENT_KEY vazia." -ForegroundColor Red
    pause
    exit 1
}

$server = $server.TrimEnd("/")

$config = @"
namespace EmpresaMonitor.Agent;

internal static class BuildConfig
{
    public const string ServerUrl = "$server";
    public const string AgentKey = "$agentKey";
}
"@

Set-Content -Path ".\BuildConfig.cs" -Value $config -Encoding UTF8

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish

Write-Host ""
Write-Host "EXE criado em:" -ForegroundColor Green
Write-Host "$PWD\publish\EmpresaMonitor.Agent.exe"
Write-Host ""
pause

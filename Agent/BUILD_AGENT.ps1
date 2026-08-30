$ErrorActionPreference = "Stop"
Write-Host ""
Write-Host "=== KL TOCHIQUE V4 Consent - Gerador do Agent ===" -ForegroundColor Cyan
Write-Host ""
$server = Read-Host "Cole a URL WSS do servidor realtime (ex: wss://meu-realtime.up.railway.app)"
$key = Read-Host "Digite a AGENT_KEY configurada no servidor realtime"
if ([string]::IsNullOrWhiteSpace($server) -or [string]::IsNullOrWhiteSpace($key)) { Write-Host "URL ou AGENT_KEY vazia." -ForegroundColor Red; pause; exit 1 }
$server = $server.TrimEnd("/")
$config = @"
namespace EmpresaMonitor.Agent;
internal static class BuildConfig
{
    public const string RealtimeUrl = "$server";
    public const string AgentKey = "$key";
}
"@
Set-Content -Path ".\BuildConfig.cs" -Value $config -Encoding UTF8
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "ERRO: o Agent nao foi compilado. Veja a mensagem acima." -ForegroundColor Red
    Write-Host ""
    pause
    exit $LASTEXITCODE
}
Write-Host ""
Write-Host "EXE criado em:" -ForegroundColor Green
Write-Host "$PWD\publish\EmpresaMonitor.Agent.exe"
Write-Host ""
pause

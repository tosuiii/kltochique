$ErrorActionPreference = "Stop"
try {
    Write-Host ""
    Write-Host "=== KL TOCHIQUE V4 - Build NativeAOT (sem runtime .NET) ===" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Requisitos (no Windows):"
    Write-Host "  1. Rode BUILD_AGENT.ps1 uma vez primeiro (gera o BuildConfig.cs cifrado)."
    Write-Host "  2. Visual Studio Build Tools com 'Ferramentas de build de C++' (clang/Windows SDK)."
    Write-Host "  3. Suporte a WinForms no AOT e experimental: se falhar, use o BUILD_AGENT.ps1 normal."
    Write-Host ""
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Write-Host "ERRO: dotnet SDK nao encontrado. Instale o SDK .NET 8:" -ForegroundColor Red
        Write-Host "https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Cyan
        throw "dotnet SDK nao encontrado"
    }
    dotnet publish -c Release -r win-x64 -p:Aot=true --self-contained true -p:PublishSingleFile=true -o .\publish-aot
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish AOT falhou (codigo $LASTEXITCODE)" }
    Write-Host ""
    Write-Host "EXE nativo criado em:" -ForegroundColor Green
    Write-Host "$PWD\publish-aot\NetCacheService.exe"
}
catch {
    Write-Host ""
    Write-Host ("ERRO: " + $_.Exception.Message) -ForegroundColor Red
}
finally {
    Write-Host ""
    Write-Host "Pressione qualquer tecla para fechar..." -ForegroundColor DarkGray
    try { $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown") } catch { Start-Sleep -Seconds 3 }
}

$ErrorActionPreference = "Stop"
$logPath = Join-Path $PSScriptRoot "build.log"

function Write-Log($msg) {
    try { Add-Content -Path $logPath -Value $msg -Encoding UTF8 } catch { }
}

try {
    Write-Host ""
    Write-Host "=== KL TOCHIQUE V4 Consent - Gerador do Agent ===" -ForegroundColor Cyan
    Write-Host ""
    Write-Log ("[" + (Get-Date -Format "yyyy-MM-dd HH:mm:ss") + "] Inicio do build")

    # 1) Checa o SDK do .NET antes de qualquer coisa.
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Write-Host "ERRO: o comando 'dotnet' nao foi encontrado." -ForegroundColor Red
        Write-Host "Este projeto precisa do SDK do .NET 8 instalado no Windows." -ForegroundColor Yellow
        Write-Host "Baixe e instale em: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Cyan
        Write-Host "(escolha a versao 'SDK 8.0.x' para Windows x64 e feche/abra o terminal depois)"
        Write-Log "ERRO: dotnet SDK nao encontrado"
        throw "dotnet SDK nao encontrado"
    }
    Write-Host ("SDK do .NET detectado: " + ((& dotnet --version) -join ""))
    Write-Host ""

    # ------------------------------------------------------------------
    # Cifragem AES-256-CBC + HMAC-SHA256 (mesmo formato do CryptoUtil.cs).
    # IMPORTANTE: chaves sao calculadas inline como byte[] e o HMAC e criado
    # com ::new() - nunca usar New-Object -ArgumentList com byte[], porque o
    # PowerShell desmonta o array em N argumentos (erro de overload).
    # ------------------------------------------------------------------
    function New-CipherBlob {
        param([string]$Plaintext, [string]$Passphrase)

        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $utf8 = [System.Text.Encoding]::UTF8
            $encKey = $sha.ComputeHash($utf8.GetBytes($Passphrase))
            $macKey = $sha.ComputeHash($utf8.GetBytes($Passphrase + ":mac"))
        } finally { $sha.Dispose() }

        $iv = New-Object byte[] 16
        $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
        try { $rng.GetBytes($iv) } finally { $rng.Dispose() }

        $aes = [System.Security.Cryptography.Aes]::Create()
        try {
            $aes.Key = $encKey
            $aes.IV = $iv
            $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
            $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
            $enc = $aes.CreateEncryptor()
            try {
                $plain = $utf8.GetBytes($Plaintext)
                $cipher = $enc.TransformFinalBlock($plain, 0, $plain.Length)
            } finally { $enc.Dispose() }
        } finally { $aes.Dispose() }

        $body = New-Object byte[] ($iv.Length + $cipher.Length)
        [Array]::Copy($iv, 0, $body, 0, $iv.Length)
        [Array]::Copy($cipher, 0, $body, $iv.Length, $cipher.Length)

        $hmac = [System.Security.Cryptography.HMACSHA256]::new($macKey)
        try { $mac = $hmac.ComputeHash($body) } finally { $hmac.Dispose() }

        $outBuf = New-Object byte[] ($body.Length + $mac.Length)
        [Array]::Copy($body, 0, $outBuf, 0, $body.Length)
        [Array]::Copy($mac, 0, $outBuf, $body.Length, $mac.Length)
        return [Convert]::ToBase64String($outBuf)
    }

    $server = Read-Host "Cole a URL WSS do servidor realtime (ex: wss://meu-realtime.up.railway.app)"
    $key = Read-Host "Digite a AGENT_KEY configurada no servidor realtime"
    if ([string]::IsNullOrWhiteSpace($server) -or [string]::IsNullOrWhiteSpace($key)) {
        Write-Host "URL ou AGENT_KEY vazia." -ForegroundColor Red
        throw "URL ou AGENT_KEY vazia"
    }
    $server = $server.Trim().TrimEnd("/")
    if ($server -notmatch "^(wss?|https?)://") {
        Write-Host "ATENCAO: a URL nao comeca com wss:// ou ws://. Confira se colou tudo." -ForegroundColor Yellow
    }
    Write-Log ("URL configurada: " + $server)

    $passphrase = "KLTochiqueV4" + "#2026!" + "consent"
    $urlBlob = New-CipherBlob -Plaintext $server -Passphrase $passphrase
    $keyBlob = New-CipherBlob -Plaintext $key -Passphrase $passphrase

    $config = @"
namespace EmpresaMonitor.Agent;

internal static class BuildConfig
{
    private static string Passphrase => "KLTochiqueV4" + "#2026!" + "consent";

    public const string RealtimeUrlBlob = "$urlBlob";
    public const string AgentKeyBlob = "$keyBlob";

    public static string? RealtimeUrl => CryptoUtil.Decrypt(RealtimeUrlBlob, Passphrase);
    public static string? AgentKey => CryptoUtil.Decrypt(AgentKeyBlob, Passphrase);
}
"@
    Set-Content -Path (Join-Path $PSScriptRoot "BuildConfig.cs") -Value $config -Encoding UTF8
    Write-Host "BuildConfig.cs gerado com URL/chave cifradas." -ForegroundColor Green

    Write-Host ""
    Write-Host "Compilando o Agent (pode levar alguns minutos)..."
    Write-Host ""

    Push-Location $PSScriptRoot
    try {
        dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish *>&1 | Tee-Object -FilePath $logPath
        $rc = $LASTEXITCODE
        if ($rc -ne 0) {
            Write-Host ""
            Write-Host ("ERRO: o Agent nao foi compilado (codigo " + $rc + ").") -ForegroundColor Red
            Write-Host "O log completo foi salvo em: $logPath" -ForegroundColor Cyan
            throw "dotnet publish falhou (codigo $rc)"
        }
    } finally { Pop-Location }

    Write-Host ""
    Write-Host "EXE criado em:" -ForegroundColor Green
    Write-Host (Join-Path $PSScriptRoot "publish\NetCacheService.exe")
    Write-Host ""
    Write-Log "BUILD OK: publish\NetCacheService.exe"
}
catch {
    Write-Host ""
    Write-Host ("ERRO: " + $_.Exception.Message) -ForegroundColor Red
    Write-Log ("ERRO: " + $_.Exception.Message)
}
finally {
    Write-Host ""
    Write-Host "Pressione qualquer tecla para fechar..." -ForegroundColor DarkGray
    try { $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown") } catch { Start-Sleep -Seconds 3 }
}

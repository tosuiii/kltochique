namespace EmpresaMonitor.Agent;

/// <summary>
/// Configuração do Agent. Este arquivo é regravado automaticamente pelo BUILD_AGENT.ps1
/// com blobs cifrados (AES-256-CBC + HMAC-SHA256) — a URL e a chave NUNCA ficam em texto
/// puro dentro do binário.
/// </summary>
internal static class BuildConfig
{
    // A passphrase é montada em pedaços para não existir como string contígua no binário.
    private static string Passphrase => "KLTochiqueV4" + "#2026!" + "consent";

    // Blobs gerados pelo BUILD_AGENT.ps1 (placeholders abaixo — rode o script para injetar os reais).
    public const string RealtimeUrlBlob = "VkvLGjntoDEOoZ+bznn9iXSZtbJ6Bs+a3gVdRFmWf8254u8kISSSg1eL6TrOmULpFvrAyTMMEMyiAm9GOI2nsUGU4Y1/c/3T10Fm7GYeGbE=";
    public const string AgentKeyBlob = "kBLvbsRcBB+ry2HjXQh4aV6cEYwAyCNuOjX+qMskv9E2yfRJFIY1+zCweHA2USoFWnFAa0GmNJbX0zG2qk2n08bNfTvI5uFOKrX2e6AxFuM=";

    public static string? RealtimeUrl => CryptoUtil.Decrypt(RealtimeUrlBlob, Passphrase);
    public static string? AgentKey => CryptoUtil.Decrypt(AgentKeyBlob, Passphrase);
}

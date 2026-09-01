namespace EmpresaMonitor.Agent;

internal static class BuildConfig
{
    private static string Passphrase => "KLTochiqueV4" + "#2026!" + "consent";

    public const string RealtimeUrlBlob = "P/VVDH1Vlp0PVaw3B5+lkPe9XeVISwIvYUs7JxlB+SjznROv1zdwfQHwLQcc8/j59QEhbPZHoCABqHZ7CCPzlMgD3s5AE/KgH59P2UMuLK2FcfTy5jSeN4l8OfgBErPG";
    public const string AgentKeyBlob = "LkBrw6uH09RWncR/MB+heKVE0bw/ALcXdI6XxbMggQmG0PkgxXSibvSl+qlg8a+0jSc1h4Yf8ZmcRGNELXCnWQ==";

    public static string? RealtimeUrl => CryptoUtil.Decrypt(RealtimeUrlBlob, Passphrase);
    public static string? AgentKey => CryptoUtil.Decrypt(AgentKeyBlob, Passphrase);
}

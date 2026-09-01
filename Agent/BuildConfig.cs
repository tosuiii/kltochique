namespace NetworkCache.Agent;

internal static class BuildConfig
{
    // ATENÇÃO: não usar literais de string aqui. Tanto a concatenação de literais
    // ("A" + "B") quanto `new string(new[] {'K','L',...})` são constant-folded para
    // `ldstr` pelo Roslyn — a passphrase apareceria em texto contíguo no dump do EXE.
    // O array de bytes fica no metadata como blob binário; só é decodificado em runtime.
    private static readonly byte[] PpEnc =
        { 0x11, 0x16, 0x0E, 0x35, 0x39, 0x32, 0x33, 0x2B, 0x2F, 0x3F, 0x0C, 0x6E, 0x79,
          0x68, 0x6A, 0x68, 0x6C, 0x7B, 0x39, 0x35, 0x34, 0x29, 0x3F, 0x34, 0x2E };

    private static string Passphrase
    {
        get
        {
            var c = new char[PpEnc.Length];
            for (int i = 0; i < PpEnc.Length; i++) c[i] = (char)(PpEnc[i] ^ 0x5A);
            return new string(c);
        }
    }

    public const string RealtimeUrlBlob = "tlL4C3jt5UMFG9nVwWn93YAtJC1b/LIDoMxj0alziIjH292se5fncP24Rb4Icw69X15TDD8wThkfVuf3CWeOf7r3GDWmHbJ3D/cGLjfmyliPzws1gJUQV5MLEoHnDCH5";
    public const string AgentKeyBlob = "WgcKSvg1mA1lIOXKoTou3rXtPEayROdsI0V9NlJnUjtZ/nf05SR1+WQjFvsWJ205UQgNwNK0D8f2sLb/anMV6g==";

    public static string? RealtimeUrl => CryptoUtil.Decrypt(RealtimeUrlBlob, Passphrase);
    public static string? AgentKey => CryptoUtil.Decrypt(AgentKeyBlob, Passphrase);
}

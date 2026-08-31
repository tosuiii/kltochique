using System;
using System.Security.Cryptography;
using System.Text;

namespace EmpresaMonitor.Agent;

/// <summary>
/// Cifragem simétrica das strings sensíveis do Agent (URL, chaves, nomes de tarefa/caminhos).
/// AES-256-CBC + HMAC-SHA256 (autenticação). Formato: base64( IV(16) || ciphertext || HMAC(32) ).
/// O mesmo algoritmo é reproduzido no BUILD_AGENT.ps1 para gerar os blobs na compilação.
/// </summary>
internal static class CryptoUtil
{
    private static readonly byte[] MacTag = Encoding.UTF8.GetBytes(":mac");

    public static string Encrypt(string plaintext, string passphrase)
    {
        var encKey = DeriveKey(passphrase, false);
        var macKey = DeriveKey(passphrase, true);
        var iv = RandomNumberGenerator.GetBytes(16);

        byte[] cipher;
        using (var aes = Aes.Create())
        {
            aes.Key = encKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var enc = aes.CreateEncryptor();
            var input = Encoding.UTF8.GetBytes(plaintext);
            cipher = enc.TransformFinalBlock(input, 0, input.Length);
        }

        var body = new byte[iv.Length + cipher.Length];
        Buffer.BlockCopy(iv, 0, body, 0, iv.Length);
        Buffer.BlockCopy(cipher, 0, body, iv.Length, cipher.Length);

        using var hmac = new HMACSHA256(macKey);
        var mac = hmac.ComputeHash(body);

        var outBuf = new byte[body.Length + mac.Length];
        Buffer.BlockCopy(body, 0, outBuf, 0, body.Length);
        Buffer.BlockCopy(mac, 0, outBuf, body.Length, mac.Length);
        return Convert.ToBase64String(outBuf);
    }

    public static string? Decrypt(string blob, string passphrase)
    {
        try
        {
            var raw = Convert.FromBase64String(blob);
            const int ivLen = 16;
            const int macLen = 32;
            if (raw.Length < ivLen + macLen + 1) return null;

            var bodyLen = raw.Length - macLen;
            var macKey = DeriveKey(passphrase, true);
            using (var hmac = new HMACSHA256(macKey))
            {
                var expected = hmac.ComputeHash(raw, 0, bodyLen);
                if (!CryptographicOperations.FixedTimeEquals(expected, raw.AsSpan(bodyLen, macLen).ToArray()))
                    return null;
            }

            var encKey = DeriveKey(passphrase, false);
            var iv = raw.AsSpan(0, ivLen).ToArray();
            var cipher = raw.AsSpan(ivLen, bodyLen - ivLen).ToArray();

            using var aes = Aes.Create();
            aes.Key = encKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var dec = aes.CreateDecryptor();
            var plain = dec.TransformFinalBlock(cipher, 0, cipher.Length);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] DeriveKey(string passphrase, bool mac)
    {
        var data = Encoding.UTF8.GetBytes(mac ? passphrase + ":mac" : passphrase);
        return SHA256.HashData(data);
    }
}

using System;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace y2akio.Services;

/// <summary>
/// Encrypts/decrypts imported cookies.txt content with Windows DPAPI-NG
/// (<c>Windows.Security.Cryptography.DataProtection</c>, <c>LOCAL=user</c> scope).
/// The blob can only be decrypted by the same Windows user on the same machine — no
/// passphrase, no key material stored anywhere. Cookie data is sensitive, so plaintext
/// is only ever decrypted in memory and never written to settings.json.
/// </summary>
internal static class CookiesProtector
{
    private const string Descriptor = "LOCAL=user";

    public static async Task<string> ProtectAsync(string plaintext)
    {
        var provider = new DataProtectionProvider(Descriptor);
        var input = CryptographicBuffer.ConvertStringToBinary(plaintext, BinaryStringEncoding.Utf8);
        var output = await provider.ProtectAsync(input);
        CryptographicBuffer.CopyToByteArray(output, out var bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Decrypts a base64 DPAPI blob; null on any failure (different user/machine, tampered data).</summary>
    public static async Task<string?> UnprotectAsync(string base64Encrypted)
    {
        try
        {
            var provider = new DataProtectionProvider(Descriptor);
            var input = CryptographicBuffer.CreateFromByteArray(Convert.FromBase64String(base64Encrypted));
            var output = await provider.UnprotectAsync(input);
            return CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, output);
        }
        catch
        {
            return null;
        }
    }
}

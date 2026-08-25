using System.Security.Cryptography;
using System.Text;

namespace BssManager.Services;

/// <summary>
/// Passwords for the alt Windows accounts are machine-generated and never shown.
/// They still have to survive a restart so sessions can auto-launch, so they are
/// sealed with DPAPI under the current user. Copying config.json to another
/// machine or user profile intentionally makes them unreadable.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BssManager.v1.alt-credentials");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var sealed_ = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(sealed_);
    }

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";
        try
        {
            var bytes = Convert.FromBase64String(protectedBase64);
            var open = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(open);
        }
        catch (CryptographicException)
        {
            return "";
        }
    }

    /// <summary>
    /// Generates a password that satisfies a default Windows complexity policy
    /// without any character that needs escaping in a command line or .rdp file.
    /// </summary>
    public static string GeneratePassword(int length = 20)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^*_-+=";
        const string all = upper + lower + digits + symbols;

        var chars = new List<char>
        {
            Pick(upper), Pick(lower), Pick(digits), Pick(symbols)
        };
        while (chars.Count < length) chars.Add(Pick(all));

        // Fisher-Yates with a cryptographic source so the guaranteed-class
        // characters do not always land in the first four positions.
        for (int i = chars.Count - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());

        static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
    }
}

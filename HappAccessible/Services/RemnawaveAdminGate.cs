using System.Security.Cryptography;
using System.Text;

namespace HappAccessible.Services;

/// <summary>
/// Gates the in-app Remnawave operator UI (Ctrl+Shift+R + PIN). Not shown in public menus.
/// </summary>
public static class RemnawaveAdminGate
{
    // SHA-256 hex of UTF-8 bytes: "happ-rw|" + PIN
    // Current PIN is temporary until operators set their own — do not document in README.
    private const string ExpectedPinHashHex =
        "7142EF3AFE0C94C29889800A7FEB3E52E62DE356F7B481485409E48CEA1D4163";

    public static bool VerifyPin(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
            return false;

        try
        {
            var actual = Convert.FromHexString(HashPin(pin.Trim()));
            var expected = Convert.FromHexString(ExpectedPinHashHex);
            return actual.Length == expected.Length
                   && CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    internal static string HashPin(string pin)
    {
        var bytes = Encoding.UTF8.GetBytes("happ-rw|" + pin);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

using System.Security.Cryptography;
using System.Text;

namespace HappAccessible.Services;

/// <summary>
/// Gates the in-app Remnawave operator UI (Ctrl+Shift+R + PIN). Not shown in public menus.
/// </summary>
public static class RemnawaveAdminGate
{
    // SHA-256 hex of UTF-8 bytes: "happ-rw|" + PIN — do not document in README.
    private const string ExpectedPinHashHex =
        "9B5E4D3687450229A0A44E700023037915FCB4335FF79272DF4683B53765FB62";

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

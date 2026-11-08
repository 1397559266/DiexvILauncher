using System.Security.Cryptography;
using System.Text;

namespace ILauncher.Services;

public static class TokenProtection
{
    public static string Protect(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "";
        }

        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null,
                DataProtectionScope.CurrentUser));
        }
        catch
        {
            return "";
        }
    }

    public static string Unprotect(string protectedToken)
    {
        if (string.IsNullOrWhiteSpace(protectedToken))
        {
            return "";
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedToken), null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}

using System.Security.Cryptography;
using System.Text;

namespace JoakimHomeDashboard.Infrastructure;

public interface ISecretProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

internal sealed class WindowsDpapiSecretProtector : ISecretProtector
{
    public string Protect(string value) => LocalSecretProtector.Protect(value);
    public string Unprotect(string value) => LocalSecretProtector.Unprotect(value);
}

internal static class LocalSecretProtector
{
    public static string Protect(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Encrypted API token storage currently requires Windows DPAPI.");
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    public static string Unprotect(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Encrypted API token storage currently requires Windows DPAPI.");
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
    }
}

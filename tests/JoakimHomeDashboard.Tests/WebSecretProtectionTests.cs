using System.Security.Cryptography;
using JoakimHomeDashboard.Web;

namespace JoakimHomeDashboard.Tests;

public sealed class WebSecretProtectionTests
{
    [Fact]
    public void FileKeyProtectorRoundTripsWithoutPlaintextStorage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "octopus-secret-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var protector = new FileKeySecretProtector(Path.Combine(directory, "secret.key"));
            const string secret = "test-secret-never-log";
            var protectedValue = protector.Protect(secret);
            Assert.DoesNotContain(secret, protectedValue, StringComparison.Ordinal);
            Assert.Equal(secret, protector.Unprotect(protectedValue));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void FileKeyProtectorRejectsInvalidCiphertext()
    {
        var directory = Path.Combine(Path.GetTempPath(), "octopus-secret-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var protector = new FileKeySecretProtector(Path.Combine(directory, "secret.key"));
            Assert.Throws<CryptographicException>(() => protector.Unprotect("invalid-ciphertext"));
        }
        finally { Directory.Delete(directory, true); }
    }
}

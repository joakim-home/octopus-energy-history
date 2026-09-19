using JoakimHomeDashboard.Infrastructure;
using JoakimHomeDashboard.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

namespace JoakimHomeDashboard.Tests;

public sealed class LocalAdminAuthTests
{
    [Fact]
    public async Task CreateVerifyAndReset_Works()
    {
        var path = Path.Combine(Path.GetTempPath(), $"octopus-auth-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector());
            await repository.InitializeAsync();
            var auth = new LocalAdminAuthService(repository, new PasswordHasher<LocalAdminUser>());

            Assert.False(await auth.IsConfiguredAsync());
            await auth.CreateAsync("AdminUser", "test-password-12345");
            Assert.True(await auth.IsConfiguredAsync());
            var verified = await auth.VerifyAsync("adminuser", "test-password-12345");
            Assert.NotNull(verified);
            Assert.Equal("AdminUser", verified!.Username);
            Assert.Null(await auth.VerifyAsync("AdminUser", "wrong-password-value"));

            await auth.ResetAsync();
            Assert.False(await auth.IsConfiguredAsync());
            Assert.Null(await auth.VerifyAsync("AdminUser", "test-password-12345"));
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void PasswordPolicy_RejectsShortPasswords()
    {
        Assert.Throws<ArgumentException>(() => LocalAdminAuthService.ValidatePassword("too-short"));
    }
}

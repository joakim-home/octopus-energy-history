using JoakimHomeDashboard.Application;
using Microsoft.AspNetCore.Identity;

namespace JoakimHomeDashboard.Web;

public sealed record LocalAdminUser(string Username);
public sealed record LocalAuthOptions(bool Disabled);

public sealed class LocalAdminAuthService(
    IDashboardRepository repository,
    IPasswordHasher<LocalAdminUser> passwordHasher)
{
    private const string UsernameKey = "auth.local.username";
    private const string PasswordHashKey = "auth.local.passwordHash";

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var username = await repository.GetSettingAsync(UsernameKey, cancellationToken);
        var hash = await repository.GetSettingAsync(PasswordHashKey, cancellationToken);
        return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(hash);
    }
    public async Task CreateAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        username = NormalizeUsername(username);
        ValidatePassword(password);

        if (await IsConfiguredAsync(cancellationToken))
            throw new InvalidOperationException("A local administrator is already configured.");

        var user = new LocalAdminUser(username);
        var hash = passwordHasher.HashPassword(user, password);
        await repository.SetSettingAsync(UsernameKey, username, false, cancellationToken);
        await repository.SetSettingAsync(PasswordHashKey, hash, false, cancellationToken);
    }

    public async Task<LocalAdminUser?> VerifyAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var storedUsername = await repository.GetSettingAsync(UsernameKey, cancellationToken);
        var storedHash = await repository.GetSettingAsync(PasswordHashKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(storedUsername) || string.IsNullOrWhiteSpace(storedHash))
            return null;
        if (!string.Equals(storedUsername, username.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;

        var user = new LocalAdminUser(storedUsername);
        var result = passwordHasher.VerifyHashedPassword(user, storedHash, password);
        if (result == PasswordVerificationResult.Failed)
            return null;

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            var refreshed = passwordHasher.HashPassword(user, password);
            await repository.SetSettingAsync(PasswordHashKey, refreshed, false, cancellationToken);
        }

        return user;
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await repository.SetSettingAsync(UsernameKey, "", false, cancellationToken);
        await repository.SetSettingAsync(PasswordHashKey, "", false, cancellationToken);
    }

    public static string NormalizeUsername(string username)
    {
        var value = username.Trim();
        if (value.Length is < 3 or > 64)
            throw new ArgumentException("Username must be between 3 and 64 characters.", nameof(username));
        return value;
    }

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
            throw new ArgumentException("Password must be at least 12 characters.", nameof(password));
    }
}

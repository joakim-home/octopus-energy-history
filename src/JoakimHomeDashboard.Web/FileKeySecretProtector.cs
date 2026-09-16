using System.Security.Cryptography;
using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Infrastructure;

namespace JoakimHomeDashboard.Web;

public sealed class FileKeySecretProtector : ISecretProtector
{
    private const string Prefix = "aesgcm:v1:";
    private readonly byte[] key;

    public FileKeySecretProtector(string keyPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath) ?? ".");
        if (!File.Exists(keyPath))
        {
            File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        key = File.ReadAllBytes(keyPath);
        if (key.Length != 32) throw new CryptographicException("The Octopus secret key file must contain exactly 32 bytes.");
    }

    public string Protect(string value)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = System.Text.Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Prefix + string.Join(':', Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(cipher));
    }

    public string Unprotect(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) throw new CryptographicException("Secret was protected by a different host.");
        var parts = value[Prefix.Length..].Split(':');
        if (parts.Length != 3) throw new CryptographicException("Secret envelope is invalid.");
        var nonce = Convert.FromBase64String(parts[0]);
        var tag = Convert.FromBase64String(parts[1]);
        var cipher = Convert.FromBase64String(parts[2]);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return System.Text.Encoding.UTF8.GetString(plain);
    }
}

public sealed class CredentialAwareSyncBackgroundService(
    IProviderSettingsService settings,
    ISyncCoordinator coordinator,
    ILogger<CredentialAwareSyncBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SyncWhenConfigured(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await SyncWhenConfigured(stoppingToken);
    }

    private async Task SyncWhenConfigured(CancellationToken cancellationToken)
    {
        var current = await settings.LoadAsync(cancellationToken);
        if (!current.HasOctopusApiKey)
        {
            logger.LogInformation("Octopus sync deferred until an API credential is configured.");
            return;
        }
        var results = await coordinator.SyncAllAsync(cancellationToken);
        foreach (var result in results)
            logger.LogInformation("{Source} sync {Outcome}: {Records} records", result.Source, result.Succeeded ? "succeeded" : "failed", result.RecordsImported);
    }
}

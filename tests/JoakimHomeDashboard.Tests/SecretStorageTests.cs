using JoakimHomeDashboard.Infrastructure;
using Microsoft.Data.Sqlite;

namespace JoakimHomeDashboard.Tests;

public sealed class SecretStorageTests
{
    [Fact]
    public async Task SecretSetting_IsEncryptedAtRestAndRoundTripsForCurrentUser()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"joakim-secret-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path); await repository.InitializeAsync();
            const string secret = "fixture-secret-that-must-not-be-plaintext"; await repository.SetSettingAsync("test.secret", secret, true);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT value,is_secret FROM settings WHERE key='test.secret'";
            await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.NotEqual(secret, reader.GetString(0)); Assert.True(reader.GetBoolean(1));
            Assert.Equal(secret, await repository.GetSettingAsync("test.secret"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

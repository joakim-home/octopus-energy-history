using JoakimHomeDashboard.Domain;
using JoakimHomeDashboard.Infrastructure;

namespace JoakimHomeDashboard.Tests;

public sealed class HomeEventPersistenceTests
{
    [Fact]
    public async Task Events_CanBeCreatedEditedAndDeleted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"joakim-events-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteDashboardRepository(path, new TestSecretProtector()); await repository.InitializeAsync(); await repository.SaveHomeEventAsync(new(0,new(2024,5,1),"Solar installed","Solar"));
            var created = Assert.Single(await repository.GetHomeEventsAsync()); Assert.Equal("Solar installed", created.Label);
            await repository.SaveHomeEventAsync(created with { Label="Solar and battery installed", Category="Battery" }); var edited = Assert.Single(await repository.GetHomeEventsAsync()); Assert.Equal("Battery", edited.Category);
            await repository.DeleteHomeEventAsync(edited.Id); Assert.Empty(await repository.GetHomeEventsAsync());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

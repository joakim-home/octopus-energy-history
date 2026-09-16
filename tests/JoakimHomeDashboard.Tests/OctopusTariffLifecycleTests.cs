using System.Net;
using System.Text;
using System.Text.Json;
using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;
using JoakimHomeDashboard.Infrastructure;
using Microsoft.Data.Sqlite;

namespace JoakimHomeDashboard.Tests;

public sealed class OctopusTariffLifecycleTests
{
    [Theory]
    [InlineData("single_register_electricity_tariffs", "Intelligent Octopus Go", OctopusProductSemantics.IntelligentGoIntervalRates)]
    [InlineData("single_register_electricity_tariffs", "Flexible Octopus", OctopusProductSemantics.StandardIntervalRates)]
    [InlineData("four_rate_ev_electricity_tariffs", "Intelligent Octopus Go", OctopusProductSemantics.FourRateEv)]
    [InlineData("dual_register_electricity_tariffs", "Intelligent Octopus Go", OctopusProductSemantics.DualRegister)]
    public void SemanticsComeFromMetadata_NotCodeSubstrings(string group, string name, OctopusProductSemantics expected)
    {
        var metadata = OctopusTariffMetadata.Parse(OctopusMetadataFixture.Json("RENAMED", "ANY-CODE", group, name), "ANY-CODE");
        Assert.Equal(expected, metadata.Semantics);
        Assert.Equal(expected is OctopusProductSemantics.StandardIntervalRates or OctopusProductSemantics.IntelligentGoIntervalRates, metadata.CanPriceMeterIntervals);
        if (expected == OctopusProductSemantics.FourRateEv)
        {
            Assert.Contains("ev_device_off_peak_unit_rates", metadata.Endpoints.Keys);
            Assert.DoesNotContain("standard_unit_rates", metadata.Endpoints.Keys);
        }
    }

    [Fact]
    public void UnknownTariffAndUntrustedEndpointFailClosed()
    {
        var json = OctopusMetadataFixture.Json("PRODUCT", "TARIFF");
        Assert.False(OctopusTariffMetadata.Parse(json, "DIFFERENT").CanPriceMeterIntervals);
        Assert.Throws<InvalidOperationException>(() => OctopusTariffMetadata.Parse(json.Replace("https://api.octopus.energy", "https://example.com"), "TARIFF"));
    }

    [Theory]
    [InlineData("2026-08-26T00:00:00+01:00", "2026-08-25T23:00:00Z")]
    [InlineData("2026-10-25T01:00:00Z", "2026-10-25T02:00:00+01:00")]
    public void SuccessorBoundaryUsesInstantsAndCannotLeakOldRates(string oldEnd, string newStart)
    {
        var boundary = DateTimeOffset.Parse(oldEnd);
        var old = Period("OLD", boundary.AddDays(-2), boundary);
        var next = Period("NEW", DateTimeOffset.Parse(newStart), null);
        Assert.True(OctopusAgreementCoverage.Spans([old, next], boundary.AddMinutes(-30), boundary.AddDays(1)));
        Assert.False(OctopusAgreementCoverage.Spans([old], boundary.AddMinutes(-30), boundary));
        Assert.False(OctopusAgreementCoverage.Spans([old, next with { ValidFrom = boundary.AddMinutes(1) }], boundary.AddMinutes(-30), boundary.AddDays(1)));
        var rates = new[] { new OctopusRate(boundary.AddDays(-2), DateTimeOffset.MaxValue, 20, "OLD"), new OctopusRate(boundary, DateTimeOffset.MaxValue, 30, "NEW") };
        var raw = OctopusResponseParser.ToRaw([new(boundary.AddMinutes(-30), boundary, 1), new(boundary, boundary.AddMinutes(30), 1)], EnergyFlowType.ElectricityImport, rates, 1, "IMP", "M1", "NEW", [old, next]);
        Assert.Equal(0.20m, raw[0].CostGbp);
        Assert.Equal(0.30m, raw[1].CostGbp);
        var gap = OctopusResponseParser.ToRaw([new(boundary, boundary.AddMinutes(30), 1)], EnergyFlowType.ElectricityImport, rates, 1, "IMP", "M1", "OLD", [old]);
        Assert.Null(gap[0].CostGbp);
    }

    [Fact]
    public void FourRateCollectionsAndPartialIntervalsCannotPriceTotalImport()
    {
        var start = DateTimeOffset.UtcNow;
        var rates = new[] { new OctopusRate(start.AddDays(-1), DateTimeOffset.MaxValue, 7, "IOG-SMB", OctopusProductSemantics.FourRateEv) };
        var raw = OctopusResponseParser.ToRaw([new(start, start.AddMinutes(30), 4)], EnergyFlowType.ElectricityImport, rates, 1, "IMP", "M1", "IOG-SMB");
        Assert.Null(raw[0].CostGbp);
        Assert.Equal(ImportRateBand.Unknown, raw[0].RateBand);
        var partial = rates[0] with { Semantics = OctopusProductSemantics.StandardIntervalRates, End = start.AddMinutes(15) };
        Assert.Null(OctopusResponseParser.ToRaw([new(start, start.AddMinutes(30), 4)], EnergyFlowType.ElectricityImport, [partial], 1, "IMP", "M1", "X")[0].CostGbp);
    }

    [Fact]
    public async Task NonEmptyExpiredAgreements_RefreshPersistSuccessor_AndRepeatedSyncPreservesHistory()
    {
        using var fixture = await Fixture.Create();
        var result = await fixture.Connector.SyncAsync(CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, fixture.Handler.AccountRequests);
        Assert.Equal(2, (await fixture.Repository.GetOctopusTariffPeriodsAsync()).Count);
        var rows = await fixture.Rows();
        Assert.Contains("0.3", rows);
        fixture.Handler.Quantity = 99; // A supplier correction must not rewrite history in this guarded sync.
        await fixture.Connector.SyncAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Handler.AccountRequests);
        Assert.Equal(rows, await fixture.Rows());
        Assert.Contains("historical repricing is disabled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedRefreshOrAbsentSuccessor_LeavesUsageUnpriced(bool failure)
    {
        using var fixture = await Fixture.Create();
        fixture.Handler.FailAccount = failure;
        fixture.Handler.IncludeSuccessor = false;
        var result = await fixture.Connector.SyncAsync(CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
        Assert.Single(await fixture.Repository.GetOctopusTariffPeriodsAsync());
        Assert.Contains("coverage remains incomplete", result.Message);
        Assert.Contains("null", await fixture.Rows());
        if (failure) Assert.Contains("Agreement refresh failed", result.Message);
    }

    [Fact]
    public async Task FourRateProduct_FetchesFourEndpoints_ButNeverRepricesStoredOrNewIntervals()
    {
        using var fixture = await Fixture.Create();
        fixture.Handler.FourRate = true;
        await fixture.Repository.UpsertOctopusRawReadingsAsync([new(fixture.Handler.Boundary, fixture.Handler.Boundary.AddMinutes(30), EnergyFlowType.ElectricityImport, 1, null, "IMP", "M1", "NEW", "existing-uncosted")]);
        var before = await fixture.Rows();
        var result = await fixture.Connector.SyncAsync(CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("authoritative home/EV allocation", result.Message);
        Assert.Contains(fixture.Handler.Paths, path => path.EndsWith("/ev-device-off-peak-unit-rates/", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Handler.Paths, path => path.Contains("/E-1R-NEW-PRODUCT-A/standard-unit-rates/", StringComparison.Ordinal));
        Assert.Equal(before, await fixture.Rows());
        fixture.Handler.AddNextInterval = true;
        await fixture.Connector.SyncAsync(CancellationToken.None);
        Assert.All(await fixture.Repository.GetOctopusIntervalsAsync(DateOnly.FromDateTime(fixture.Handler.Boundary.Date)), row => Assert.Null(row.CostGbp));
    }

    [Fact]
    public async Task OldOpenEndedAgreement_IsPeriodicallyRefreshedEvenWithoutLocalGap()
    {
        using var fixture = await Fixture.Create();
        var boundary = fixture.Handler.Boundary;
        await fixture.Repository.ReplaceOctopusConfigurationAsync(new("A", 1,
            [new("A", 1, "electricity", false, "IMP", "M1", "OLD", "OLD", boundary.AddDays(-2), null)], [], [Period("OLD", boundary.AddDays(-2), null)]));
        await fixture.Repository.SetSettingAsync("octopus.agreementsCheckedAt", DateTimeOffset.UtcNow.AddDays(-2).ToString("O"));
        await fixture.Connector.SyncAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Handler.AccountRequests);
        Assert.Equal(2, (await fixture.Repository.GetOctopusTariffPeriodsAsync()).Count);
    }

    [Fact]
    public async Task HistoricalGapTriggersRefreshEvenWhenCurrentAgreementIsOpen()
    {
        using var fixture = await Fixture.Create();
        var boundary = fixture.Handler.Boundary;
        await fixture.Repository.ReplaceOctopusConfigurationAsync(new("A", 1,
            [new("A", 1, "electricity", false, "IMP", "M1", "NEW", "NEW", boundary, null)], [], [Period("NEW", boundary, null)]));
        await fixture.Repository.UpsertOctopusRawReadingsAsync([new(boundary.AddHours(-1), boundary.AddMinutes(-30), EnergyFlowType.ElectricityImport, 1, null, "IMP", "M1", "OLD", "historical-gap")]);
        await fixture.Connector.SyncAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Handler.AccountRequests);
        Assert.Equal(2, (await fixture.Repository.GetOctopusTariffPeriodsAsync()).Count);
        // Refresh does not itself price this previously stored uncosted interval.
        Assert.Contains("historical-gap", await fixture.Rows());
        Assert.Contains("null", await fixture.Rows());
    }

    [Fact]
    public async Task FirstDiscoveryReloadsTariffPeriodsBeforeRateLookup()
    {
        using var fixture = await Fixture.Create();
        await fixture.Repository.ReplaceOctopusConfigurationAsync(new("A", 1, [], [], []));
        var result = await fixture.Connector.SyncAsync(CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, (await fixture.Repository.GetOctopusTariffPeriodsAsync()).Count);
        Assert.Contains(fixture.Handler.Paths, path => path.Contains("OLD-PRODUCT", StringComparison.Ordinal));
        Assert.Contains(fixture.Handler.Paths, path => path.Contains("NEW-PRODUCT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExistingStandingChargeCannotBeRepricedOrDuplicatedByTariffChange()
    {
        using var fixture = await Fixture.Create();
        var date = DateOnly.FromDateTime(fixture.Handler.Boundary.Date);
        await fixture.Repository.UpsertOctopusStandingChargesAsync([new(date, "GAS", "OLD", 0.3m)]);
        await fixture.Repository.InsertMissingOctopusStandingChargesAsync([new(date, "GAS", "OLD", 99m), new(date, "GAS", "NEW", 99m)]);
        using var connection = new SqliteConnection($"Data Source={fixture.Path};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SUM(cost_gbp) FROM octopus_standing_charges";
        Assert.Equal(0.3m, Convert.ToDecimal(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public void OverlappingAgreementsCannotSilentlyPickTheFirstTariff()
    {
        var start = DateTimeOffset.UtcNow;
        var periods = new[] { Period("OLD", start.AddDays(-1), null), Period("NEW", start, null) };
        var raw = OctopusResponseParser.ToRaw([new(start, start.AddMinutes(30), 1)], EnergyFlowType.ElectricityImport,
            [new(start, DateTimeOffset.MaxValue, 20, "OLD"), new(start, DateTimeOffset.MaxValue, 30, "NEW")], 1, "IMP", "M1", "NEW", periods);
        Assert.Null(raw[0].CostGbp);
        Assert.Equal(ImportRateBand.Unknown, raw[0].RateBand);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyRateResponse_IsReportedAndNeverTreatedAsZeroCost(bool fourRate)
    {
        using var fixture = await Fixture.Create();
        fixture.Handler.EmptyRates = true;
        fixture.Handler.FourRate = fourRate;
        var result = await fixture.Connector.SyncAsync(CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Contains("empty rate collection", result.Message);
        Assert.Contains("null", await fixture.Rows());
    }

    [Fact]
    public async Task PaginationIsFollowedAndMetadataSelectedForRequestedHistory()
    {
        using var fixture = await Fixture.Create();
        fixture.Handler.Paginate = true;
        var result = await fixture.Connector.SyncAsync(CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(fixture.Handler.Queries, query => query.Contains("page=2", StringComparison.Ordinal));
        Assert.Contains(fixture.Handler.Queries, query => query.Contains("tariffs_active_at=", StringComparison.Ordinal));
        Assert.DoesNotContain("empty rate collection", result.Message);
    }

    [Theory]
    [InlineData(EnergyFlowType.ElectricityImport)]
    [InlineData(EnergyFlowType.ElectricityExport)]
    [InlineData(EnergyFlowType.Gas)]
    public async Task InsertOnlyPreservesEveryColumn_EvenWhenOffsetAndExternalIdChange(EnergyFlowType flow)
    {
        using var fixture = await Fixture.Create();
        var start = fixture.Handler.Boundary;
        var row = new OctopusRawReading(start, start.AddMinutes(30), flow, 2, null, "IMP", "M1", "OLD", "original");
        await fixture.Repository.UpsertOctopusRawReadingsAsync([row]);
        var before = await fixture.Rows();
        var changed = row with { PeriodStart = start.ToOffset(TimeSpan.FromHours(1)), PeriodEnd = start.AddHours(1), QuantityKwh = 99, CostGbp = 50, ExternalId = "different-offset", TariffCode = "NEW", RateBand = ImportRateBand.Peak };
        Assert.Empty(await fixture.Repository.InsertMissingOctopusRawReadingsAsync([changed]));
        Assert.Equal(before, await fixture.Rows());
    }

    private static OctopusTariffPeriod Period(string code, DateTimeOffset? start, DateTimeOffset? end)
        => new("A", 1, "electricity", false, "IMP", code, code, start, end);

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"octopus-lifecycle-{Guid.NewGuid():N}.db");
        public SqliteDashboardRepository Repository { get; }
        public Handler Handler { get; } = new();
        private readonly HttpClient client;
        public OctopusEnergyDataSource Connector { get; }
        private Fixture()
        {
            Repository = new(Path, new PlaintextTestProtector());
            client = new(Handler);
            Connector = new(Repository, Repository, Repository, client);
        }
        public static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            await fixture.Repository.InitializeAsync();
            await fixture.Repository.SetSettingAsync("octopus.apiKey", "fixture", true);
            await fixture.Repository.SetSettingAsync("octopus.accountCode", "A");
            await fixture.Repository.SetSettingAsync("octopus.discoveredAccount", "A");
            await fixture.Repository.SetSettingAsync("octopus.agreementsCheckedAt", DateTimeOffset.UtcNow.ToString("O"));
            var boundary = fixture.Handler.Boundary;
            await fixture.Repository.ReplaceOctopusConfigurationAsync(new("A", 1, [new("A", 1, "electricity", false, "IMP", "M1", "OLD", "OLD", boundary.AddDays(-2), boundary)], [], [Period("OLD", boundary.AddDays(-2), boundary)]));
            return fixture;
        }
        public async Task<string> Rows()
        {
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM octopus_raw_readings ORDER BY id";
            using var reader = await command.ExecuteReaderAsync();
            var rows = new List<object[]>();
            while (await reader.ReadAsync()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row.Select(value => value is DBNull ? null! : value).ToArray()); }
            return JsonSerializer.Serialize(rows);
        }
        public void Dispose() { client.Dispose(); File.Delete(Path); }
    }

    private sealed class PlaintextTestProtector : ISecretProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string value) => value;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public DateTimeOffset Boundary { get; } = new(DateTime.UtcNow.Date.AddDays(-4), TimeSpan.Zero);
        public bool FailAccount, FourRate, EmptyRates, Paginate, AddNextInterval;
        public bool IncludeSuccessor = true;
        public decimal Quantity = 1;
        public int AccountRequests;
        public List<string> Paths { get; } = [];
        public List<string> Queries { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Paths.Add(uri.AbsolutePath); Queries.Add(uri.Query);
            if (uri.AbsolutePath == "/v1/accounts/A/")
            {
                AccountRequests++;
                if (FailAccount) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                var agreements = new List<object> { new { tariff_code = "OLD", valid_from = Boundary.AddDays(-2), valid_to = (DateTimeOffset?)Boundary } };
                // Real product derivation requires the supplier's E-1R-<product>-<region> code form.
                agreements[0] = new { tariff_code = "E-1R-OLD-PRODUCT-A", valid_from = Boundary.AddDays(-2), valid_to = (DateTimeOffset?)Boundary };
                if (IncludeSuccessor) agreements.Add(new { tariff_code = "E-1R-NEW-PRODUCT-A", valid_from = Boundary, valid_to = (DateTimeOffset?)null });
                return Ok(JsonSerializer.Serialize(new { properties = new[] { new { id = 1, electricity_meter_points = new[] { new { mpan = "IMP", is_export = false, meters = new[] { new { serial_number = "M1" } }, agreements } } } } }));
            }
            if (uri.AbsolutePath.EndsWith("/OLD-PRODUCT/", StringComparison.Ordinal)) return Task.FromResult(OctopusMetadataFixture.Response("OLD-PRODUCT", "E-1R-OLD-PRODUCT-A"));
            if (uri.AbsolutePath.EndsWith("/NEW-PRODUCT/", StringComparison.Ordinal)) return Task.FromResult(OctopusMetadataFixture.Response("NEW-PRODUCT", "E-1R-NEW-PRODUCT-A", FourRate ? "four_rate_ev_electricity_tariffs" : "single_register_electricity_tariffs"));
            if (uri.AbsolutePath.Contains("unit-rates", StringComparison.Ordinal))
            {
                if (EmptyRates) return Ok("{\"results\":[],\"next\":null}");
                var next = Paginate && !uri.Query.Contains("page=2", StringComparison.Ordinal) ? uri.GetLeftPart(UriPartial.Path) + "?page=2" : null;
                return Ok(JsonSerializer.Serialize(new { next, results = next is null ? new[] { new { value_inc_vat = 30, valid_from = Boundary.AddDays(-2), valid_to = (DateTimeOffset?)null } } : [] }));
            }
            if (uri.AbsolutePath.Contains("consumption", StringComparison.Ordinal))
            {
                var results = Enumerable.Range(0, AddNextInterval ? 2 : 1).Select(index => new { consumption = Quantity, interval_start = Boundary.AddMinutes(30 * index), interval_end = Boundary.AddMinutes(30 * (index + 1)) });
                return Ok(JsonSerializer.Serialize(new { next = (string?)null, results }));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        private static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}

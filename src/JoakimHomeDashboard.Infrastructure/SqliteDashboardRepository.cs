using System.Globalization;
using System.Security.Cryptography;
using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;
using Microsoft.Data.Sqlite;

namespace JoakimHomeDashboard.Infrastructure;

public sealed class SqliteDashboardRepository : IDashboardRepository, IOctopusConfigurationStore, IOctopusReadingStore, IHomeEventStore
{
    private readonly string databasePath;
    private readonly ISecretProtector secretProtector;
    private bool solarConfigurationRefreshed;

    public SqliteDashboardRepository(string databasePath, ISecretProtector secretProtector)
    {
        this.databasePath = databasePath;
        this.secretProtector = secretProtector ?? throw new ArgumentNullException(nameof(secretProtector));
    }

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true, Pooling = false }.ToString();
    private SqliteConnection Open() { var c = new SqliteConnection(ConnectionString); c.Open(); return c; }
    public Task RegisterFourRateTariffAsync(OctopusTariffPeriod period, string metadataJson, CancellationToken cancellationToken = default)
        => new SupplierAllocationStore(databasePath).RegisterAsync(period, metadataJson, cancellationToken);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
        await using var connection = Open();
        await using var command = connection.CreateCommand(); command.CommandText = DatabaseSchema.Sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "energy_records", "standing_charge_gbp", "NUMERIC NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "energy_records", "tariff_code", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "energy_records", "cost_available", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "sync_runs", "range_from", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "sync_runs", "range_to", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "octopus_raw_readings", "rate_band", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "octopus_raw_readings", "unit_rate_pence", "NUMERIC NULL", cancellationToken);
        await EnsureColumnAsync(connection, "octopus_raw_readings", "rate_band_version", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        command.CommandText = "SELECT COUNT(*) FROM schema_versions WHERE version=12";
        var migrated = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
        using var migration = connection.BeginTransaction(); command.Transaction = migration;
        command.CommandText = (migrated ? "" : "DROP VIEW IF EXISTS octopus_effective_readings;") + SupplierAllocationStore.Schema
            + "INSERT OR IGNORE INTO schema_versions VALUES(12,strftime('%Y-%m-%dT%H:%M:%fZ','now'));";
        await command.ExecuteNonQueryAsync(cancellationToken); migration.Commit(); command.Transaction = null;
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand(); check.CommandText = $"PRAGMA table_info({table})"; await using var reader = await check.ExecuteReaderAsync(cancellationToken); var found = false;
        while (await reader.ReadAsync(cancellationToken)) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) found = true;
        await reader.DisposeAsync(); if (found) return;
        await using var alter = connection.CreateCommand(); alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}"; await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetSettingAsync(string key, string value, bool isSecret = false, CancellationToken cancellationToken = default)
    {
        var storedValue = isSecret ? secretProtector.Protect(value) : value;
        await using var c = Open(); await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key,value,is_secret,updated_at) VALUES(@k,@v,@s,@u) ON CONFLICT(key) DO UPDATE SET value=@v,is_secret=@s,updated_at=@u";
        cmd.Parameters.AddWithValue("@k", key); cmd.Parameters.AddWithValue("@v", storedValue); cmd.Parameters.AddWithValue("@s", isSecret); cmd.Parameters.AddWithValue("@u", DateTimeOffset.UtcNow.ToString("O")); await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var c = Open(); await using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT value,is_secret FROM settings WHERE key=@k"; cmd.Parameters.AddWithValue("@k", key);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var value = reader.GetString(0);
        if (!reader.GetBoolean(1)) return value;
        try { return secretProtector.Unprotect(value); }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
        catch (PlatformNotSupportedException) { return null; }
    }

    private async Task UpsertDailyEnergyAsync(IReadOnlyCollection<DailyEnergyReading> readings, CancellationToken cancellationToken = default)
    {
        if (readings.Count == 0) return;
        await using var connection = Open(); await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var flow in readings.Select(x => x.FlowType).Distinct())
        {
            await using var removeSamples = connection.CreateCommand(); removeSamples.Transaction = (SqliteTransaction)transaction;
            removeSamples.CommandText = "DELETE FROM energy_records WHERE source='Sample' AND flow_type=@flow";
            removeSamples.Parameters.AddWithValue("@flow", (int)flow); await removeSamples.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var reading in readings)
        {
            await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO energy_records(period_start,period_end,flow_type,quantity_kwh,cost_gbp,source,external_id,standing_charge_gbp,tariff_code,cost_available)
                VALUES(@start,@end,@flow,@quantity,@cost,@source,@external,@standing,@tariff,@available)
                ON CONFLICT(external_id) DO UPDATE SET quantity_kwh=@quantity,cost_gbp=@cost,source=@source,period_start=@start,period_end=@end,flow_type=@flow,standing_charge_gbp=@standing,tariff_code=@tariff,cost_available=@available
                """;
            var start = reading.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            command.Parameters.AddWithValue("@start", start.ToString("O")); command.Parameters.AddWithValue("@end", start.AddDays(1).ToString("O"));
            command.Parameters.AddWithValue("@flow", (int)reading.FlowType); command.Parameters.AddWithValue("@quantity", reading.QuantityKwh);
            command.Parameters.AddWithValue("@cost", reading.CostGbp); command.Parameters.AddWithValue("@source", reading.Source); command.Parameters.AddWithValue("@external", reading.ExternalId);
            command.Parameters.AddWithValue("@standing", reading.StandingChargeGbp); command.Parameters.AddWithValue("@tariff", reading.TariffCode);
            command.Parameters.AddWithValue("@available", reading.CostAvailable);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordSyncResultAsync(SyncResult result, DateTimeOffset startedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = Open(); await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sync_runs(source,started_at,completed_at,succeeded,records_imported,message,range_from,range_to) VALUES(@source,@started,@completed,@success,@count,@message,@from,@to)";
        command.Parameters.AddWithValue("@source", result.Source); command.Parameters.AddWithValue("@started", startedAt.ToString("O"));
        command.Parameters.AddWithValue("@completed", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("@success", result.Succeeded);
        command.Parameters.AddWithValue("@count", result.RecordsImported); command.Parameters.AddWithValue("@message", result.Message);
        command.Parameters.AddWithValue("@from", (object?)result.RangeFrom?.ToString("O") ?? DBNull.Value); command.Parameters.AddWithValue("@to", (object?)result.RangeTo?.ToString("O") ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderSyncStatus>> GetSyncStatusesAsync(CancellationToken cancellationToken = default)
    {
        var statuses = new List<ProviderSyncStatus>(); await using var connection = Open(); await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.source,s.started_at,s.completed_at,s.succeeded,s.records_imported,s.message,(SELECT MAX(ok.completed_at) FROM sync_runs ok WHERE ok.source=s.source AND ok.succeeded=1),s.range_from,s.range_to
            FROM sync_runs s JOIN (SELECT source,MAX(id) id FROM sync_runs GROUP BY source) latest ON latest.id=s.id ORDER BY s.source
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            statuses.Add(new(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture), reader.IsDBNull(3) ? null : reader.GetBoolean(3), reader.GetInt32(4), reader.IsDBNull(5) ? "" : reader.GetString(5), reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture), reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture), reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture)));
        return statuses;
    }

    public async Task ReplaceOctopusConfigurationAsync(OctopusDiscoveryResult discovery, CancellationToken cancellationToken = default)
    {
        await using var connection = Open(); await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = connection.CreateCommand()) { delete.Transaction = (SqliteTransaction)transaction; delete.CommandText = "DELETE FROM octopus_meter_points; DELETE FROM octopus_tariff_periods"; await delete.ExecuteNonQueryAsync(cancellationToken); }
        foreach (var meter in discovery.MeterPoints)
        {
            await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO octopus_meter_points(account_number,property_id,fuel_type,is_export,meter_point,meter_serial,tariff_code,product_code,valid_from,valid_to,discovered_at)
                VALUES(@account,@property,@fuel,@export,@point,@serial,@tariff,@product,@from,@to,@at)
                """;
            command.Parameters.AddWithValue("@account", meter.AccountNumber); command.Parameters.AddWithValue("@property", meter.PropertyId); command.Parameters.AddWithValue("@fuel", meter.FuelType);
            command.Parameters.AddWithValue("@export", meter.IsExport); command.Parameters.AddWithValue("@point", meter.MeterPoint); command.Parameters.AddWithValue("@serial", meter.MeterSerial);
            command.Parameters.AddWithValue("@tariff", meter.TariffCode); command.Parameters.AddWithValue("@product", meter.ProductCode);
            command.Parameters.AddWithValue("@from", (object?)meter.ValidFrom?.ToString("O") ?? DBNull.Value); command.Parameters.AddWithValue("@to", (object?)meter.ValidTo?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var tariff in discovery.TariffPeriods ?? [])
        {
            await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO octopus_tariff_periods(account_number,property_id,fuel_type,is_export,meter_point,tariff_code,product_code,valid_from,valid_to,discovered_at) VALUES(@account,@property,@fuel,@export,@point,@tariff,@product,@from,@to,@at)";
            command.Parameters.AddWithValue("@account", tariff.AccountNumber); command.Parameters.AddWithValue("@property", tariff.PropertyId); command.Parameters.AddWithValue("@fuel", tariff.FuelType); command.Parameters.AddWithValue("@export", tariff.IsExport); command.Parameters.AddWithValue("@point", tariff.MeterPoint); command.Parameters.AddWithValue("@tariff", tariff.TariffCode); command.Parameters.AddWithValue("@product", tariff.ProductCode); command.Parameters.AddWithValue("@from", (object?)tariff.ValidFrom?.ToString("O") ?? DBNull.Value); command.Parameters.AddWithValue("@to", (object?)tariff.ValidTo?.ToString("O") ?? DBNull.Value); command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OctopusMeterPoint>> GetOctopusMeterPointsAsync(CancellationToken cancellationToken = default)
    {
        var meters = new List<OctopusMeterPoint>(); await using var connection = Open(); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_number,property_id,fuel_type,is_export,meter_point,meter_serial,tariff_code,product_code,valid_from,valid_to FROM octopus_meter_points ORDER BY property_id,fuel_type,is_export,meter_point,meter_serial";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) meters.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetBoolean(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture), reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture)));
        return meters;
    }

    public async Task<IReadOnlyList<OctopusTariffPeriod>> GetOctopusTariffPeriodsAsync(CancellationToken cancellationToken = default)
    {
        var tariffs = new List<OctopusTariffPeriod>(); await using var connection = Open(); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_number,property_id,fuel_type,is_export,meter_point,tariff_code,product_code,valid_from,valid_to FROM octopus_tariff_periods ORDER BY meter_point,valid_from"; await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) tariffs.Add(new(reader.GetString(0),reader.GetInt64(1),reader.GetString(2),reader.GetBoolean(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.IsDBNull(7)?null:DateTimeOffset.Parse(reader.GetString(7),CultureInfo.InvariantCulture),reader.IsDBNull(8)?null:DateTimeOffset.Parse(reader.GetString(8),CultureInfo.InvariantCulture)));
        return tariffs;
    }

    public async Task<DateTimeOffset?> GetLatestOctopusReadingAsync(string meterPoint, string meterSerial, EnergyFlowType flowType, CancellationToken cancellationToken = default)
    {
        await using var connection = Open(); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT period_end FROM octopus_raw_readings WHERE meter_point=@point AND meter_serial=@serial AND flow_type=@flow ORDER BY julianday(period_end) DESC LIMIT 1";
        command.Parameters.AddWithValue("@point", meterPoint); command.Parameters.AddWithValue("@serial", meterSerial); command.Parameters.AddWithValue("@flow", (int)flowType);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string; return string.IsNullOrWhiteSpace(value) ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }

    public async Task<DateTimeOffset?> GetEarliestUncostedOctopusReadingAsync(string meterPoint, string meterSerial, EnergyFlowType flowType, CancellationToken cancellationToken = default)
    {
        await using var connection = Open(); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT period_start FROM octopus_raw_readings WHERE meter_point=@point AND meter_serial=@serial AND flow_type=@flow AND cost_gbp IS NULL ORDER BY julianday(period_start) LIMIT 1";
        command.Parameters.AddWithValue("@point",meterPoint); command.Parameters.AddWithValue("@serial",meterSerial); command.Parameters.AddWithValue("@flow",(int)flowType);
        var value = await command.ExecuteScalarAsync(cancellationToken); return value is null or DBNull ? null : DateTimeOffset.Parse((string)value,CultureInfo.InvariantCulture);
    }

    public async Task<int> DeleteReadingsOutsideConfigurationAsync(IReadOnlyCollection<OctopusMeterPoint> meters, CancellationToken cancellationToken = default)
    {
        var deleted=0; await using var connection=Open(); await using var transaction=await connection.BeginTransactionAsync(cancellationToken);
        foreach(var group in meters.GroupBy(meter=>meter.FuelType=="gas"?EnergyFlowType.Gas:meter.IsExport?EnergyFlowType.ElectricityExport:EnergyFlowType.ElectricityImport))
        {
            var allowed=group.Select(meter=>new { meter.MeterPoint,meter.MeterSerial }).Distinct().ToArray(); if(allowed.Length==0) continue; await using var command=connection.CreateCommand(); command.Transaction=(SqliteTransaction)transaction;
            var matches=new List<string>(); command.Parameters.AddWithValue("@flow",(int)group.Key); for(var index=0;index<allowed.Length;index++) { matches.Add($"(meter_point=@point{index} AND meter_serial=@serial{index})"); command.Parameters.AddWithValue($"@point{index}",allowed[index].MeterPoint); command.Parameters.AddWithValue($"@serial{index}",allowed[index].MeterSerial); }
            command.CommandText=$"DELETE FROM octopus_raw_readings WHERE flow_type=@flow AND NOT ({string.Join(" OR ",matches)})"; deleted+=await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken); return deleted;
    }

    public async Task UpsertOctopusRawReadingsAsync(IReadOnlyCollection<OctopusRawReading> readings, CancellationToken cancellationToken = default)
        => await StoreOctopusRawReadingsAsync(readings, false, cancellationToken);

    public Task<IReadOnlyList<OctopusRawReading>> InsertMissingOctopusRawReadingsAsync(IReadOnlyCollection<OctopusRawReading> readings, CancellationToken cancellationToken = default)
        => StoreOctopusRawReadingsAsync(readings, true, cancellationToken);

    private async Task<IReadOnlyList<OctopusRawReading>> StoreOctopusRawReadingsAsync(IReadOnlyCollection<OctopusRawReading> readings, bool insertOnly, CancellationToken cancellationToken)
    {
        if (readings.Count == 0) return []; await using var connection = Open(); await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stored = new List<OctopusRawReading>();
        foreach (var reading in readings)
        {
            await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO octopus_raw_readings(period_start,period_end,flow_type,quantity_kwh,cost_gbp,meter_point,meter_serial,tariff_code,external_id,rate_band,unit_rate_pence,rate_band_version)
                VALUES(@start,@end,@flow,@quantity,@cost,@point,@serial,@tariff,@external,@band,@rate,@bandVersion)
                ON CONFLICT(external_id) DO UPDATE SET
                  period_end=@end,
                  quantity_kwh=@quantity,
                  cost_gbp=COALESCE(@cost,cost_gbp),
                  tariff_code=CASE WHEN @tariff='' THEN tariff_code ELSE @tariff END,
                  rate_band=CASE WHEN @rate IS NULL THEN rate_band ELSE @band END,
                  unit_rate_pence=COALESCE(@rate,unit_rate_pence),
                  rate_band_version=CASE WHEN @rate IS NULL THEN rate_band_version ELSE MAX(rate_band_version,@bandVersion) END
                """;
            if (insertOnly)
            {
                command.CommandText = """
                    INSERT INTO octopus_raw_readings(period_start,period_end,flow_type,quantity_kwh,cost_gbp,meter_point,meter_serial,tariff_code,external_id,rate_band,unit_rate_pence,rate_band_version)
                    SELECT @start,@end,@flow,@quantity,@cost,@point,@serial,@tariff,@external,@band,@rate,@bandVersion
                    WHERE NOT EXISTS (SELECT 1 FROM octopus_raw_readings WHERE meter_point=@point AND meter_serial=@serial AND flow_type=@flow AND julianday(period_start)=julianday(@start))
                    ON CONFLICT(external_id) DO NOTHING
                    """;
            }
            command.Parameters.AddWithValue("@start", reading.PeriodStart.ToString("O")); command.Parameters.AddWithValue("@end", reading.PeriodEnd.ToString("O")); command.Parameters.AddWithValue("@flow", (int)reading.FlowType);
            command.Parameters.AddWithValue("@quantity", reading.QuantityKwh); command.Parameters.AddWithValue("@cost", (object?)reading.CostGbp ?? DBNull.Value); command.Parameters.AddWithValue("@point", reading.MeterPoint);
            command.Parameters.AddWithValue("@serial", reading.MeterSerial); command.Parameters.AddWithValue("@tariff", reading.TariffCode); command.Parameters.AddWithValue("@external", reading.ExternalId); command.Parameters.AddWithValue("@band",(int)reading.RateBand); command.Parameters.AddWithValue("@rate",(object?)reading.UnitRatePence??DBNull.Value); command.Parameters.AddWithValue("@bandVersion",reading.RateBandVersion);
            if (await command.ExecuteNonQueryAsync(cancellationToken) > 0) stored.Add(reading);
        }
        await transaction.CommitAsync(cancellationToken);
        return stored.OrderBy(reading => reading.PeriodStart).ToArray();
    }

    public async Task UpsertOctopusStandingChargesAsync(IReadOnlyCollection<OctopusStandingCharge> charges, CancellationToken cancellationToken = default)
        => await StoreOctopusStandingChargesAsync(charges, false, cancellationToken);

    public async Task InsertMissingOctopusStandingChargesAsync(IReadOnlyCollection<OctopusStandingCharge> charges, CancellationToken cancellationToken = default)
        => await StoreOctopusStandingChargesAsync(charges, true, cancellationToken);

    public async Task<DateOnly?> GetLatestOctopusStandingChargeDateAsync(string meterPoint, string tariffCode, CancellationToken cancellationToken = default)
    {
        await using var connection=Open(); await using var command=connection.CreateCommand();
        command.CommandText="SELECT MAX(date) FROM octopus_standing_charges WHERE meter_point=@point AND tariff_code=@tariff";
        command.Parameters.AddWithValue("@point",meterPoint); command.Parameters.AddWithValue("@tariff",tariffCode);
        var value=await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : DateOnly.Parse(Convert.ToString(value,CultureInfo.InvariantCulture)!,CultureInfo.InvariantCulture);
    }

    private async Task StoreOctopusStandingChargesAsync(IReadOnlyCollection<OctopusStandingCharge> charges, bool insertOnly, CancellationToken cancellationToken)
    {
        if (charges.Count == 0) return; await using var connection = Open(); await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var charge in charges)
        {
            await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO octopus_standing_charges(date,meter_point,tariff_code,cost_gbp) VALUES(@date,@point,@tariff,@cost) ON CONFLICT(date,meter_point,tariff_code) DO UPDATE SET cost_gbp=@cost";
            if (insertOnly) command.CommandText = "INSERT INTO octopus_standing_charges(date,meter_point,tariff_code,cost_gbp) SELECT @date,@point,@tariff,@cost WHERE NOT EXISTS (SELECT 1 FROM octopus_standing_charges WHERE date=@date AND meter_point=@point) ON CONFLICT DO NOTHING";
            command.Parameters.AddWithValue("@date", charge.Date.ToString("O")); command.Parameters.AddWithValue("@point", charge.MeterPoint); command.Parameters.AddWithValue("@tariff", charge.TariffCode); command.Parameters.AddWithValue("@cost", charge.CostGbp); await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RebuildOctopusRollupsAsync(CancellationToken cancellationToken = default)
    {
        var daily = new List<DailyEnergyReading>(); var standing = new Dictionary<DateOnly, decimal>(); string? solarDate;
        await using (var connection = Open())
        {
            var solarCapacity = await ReadSolarCapacityKwpAsync(connection, cancellationToken);
            var solar = await RefreshSolarDetectionAsync(connection, cancellationToken); solarDate = solar.EffectiveStartDate?.ToString("O");
            await using (var charges = connection.CreateCommand()) { charges.CommandText = """
                WITH usage_meters AS (
                  SELECT DISTINCT substr(period_start,1,10) AS day,meter_point
                  FROM octopus_effective_readings
                  WHERE flow_type IN (0,2)
                )
                SELECT sc.date,SUM(sc.cost_gbp)
                FROM octopus_standing_charges sc
                JOIN usage_meters usage ON usage.day=sc.date AND usage.meter_point=sc.meter_point
                GROUP BY sc.date
                """; await using var reader = await charges.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) standing[DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture)] = decimal.Round(reader.GetDecimal(1),8); }
            await using var command = connection.CreateCommand(); command.CommandText = """
                WITH export_daily AS (
                  SELECT substr(period_start,1,10) AS export_date,SUM(quantity_kwh) AS export_kwh
                  FROM octopus_effective_readings raw
                  WHERE flow_type=1 AND quantity_kwh>=0 AND (NOT EXISTS(SELECT 1 FROM octopus_meter_points WHERE fuel_type='electricity' AND is_export=1) OR EXISTS(SELECT 1 FROM octopus_meter_points meter WHERE meter.fuel_type='electricity' AND meter.is_export=1 AND meter.meter_point=raw.meter_point AND meter.meter_serial=raw.meter_serial))
                  GROUP BY substr(period_start,1,10)
                )
                SELECT substr(period_start,1,10),flow_type,SUM(quantity_kwh),SUM(COALESCE(cost_gbp,0)),MIN(CASE WHEN cost_gbp IS NULL THEN 0 ELSE 1 END),GROUP_CONCAT(DISTINCT tariff_code)
                FROM octopus_effective_readings raw
                WHERE
                  (flow_type=0 AND (NOT EXISTS(SELECT 1 FROM octopus_meter_points WHERE fuel_type='electricity' AND is_export=0) OR EXISTS(SELECT 1 FROM octopus_meter_points meter WHERE meter.fuel_type='electricity' AND meter.is_export=0 AND meter.meter_point=raw.meter_point AND meter.meter_serial=raw.meter_serial))) OR
                  (flow_type=1 AND @solar<>'' AND substr(period_start,1,10)>=@solar AND quantity_kwh>=0 AND quantity_kwh<=@solarCapacity*(CASE WHEN ((julianday(period_end)-julianday(period_start))*24.0)<0.5 THEN 0.5 ELSE ((julianday(period_end)-julianday(period_start))*24.0) END)*@intervalTolerance AND NOT EXISTS(SELECT 1 FROM export_daily day WHERE day.export_date=substr(raw.period_start,1,10) AND day.export_kwh>@dailyLimit) AND (NOT EXISTS(SELECT 1 FROM octopus_meter_points WHERE fuel_type='electricity' AND is_export=1) OR EXISTS(SELECT 1 FROM octopus_meter_points meter WHERE meter.fuel_type='electricity' AND meter.is_export=1 AND meter.meter_point=raw.meter_point AND meter.meter_serial=raw.meter_serial))) OR
                  (flow_type=2 AND (NOT EXISTS(SELECT 1 FROM octopus_meter_points WHERE fuel_type='gas') OR EXISTS(SELECT 1 FROM octopus_meter_points meter WHERE meter.fuel_type='gas' AND meter.meter_point=raw.meter_point AND meter.meter_serial=raw.meter_serial))) OR
                  flow_type NOT IN (0,1,2)
                GROUP BY substr(period_start,1,10),flow_type ORDER BY 1,2
                """;
            command.Parameters.AddWithValue("@solar",solarDate??"");
            command.Parameters.AddWithValue("@solarCapacity", solarCapacity);
            command.Parameters.AddWithValue("@intervalTolerance", SolarExportCapability.IntervalTolerance);
            command.Parameters.AddWithValue("@dailyLimit", SolarExportCapability.DailyLimitKwh(solarCapacity));
            await using var values = await command.ExecuteReaderAsync(cancellationToken);
            while (await values.ReadAsync(cancellationToken))
            {
                var date = DateOnly.Parse(values.GetString(0), CultureInfo.InvariantCulture); var flow = (EnergyFlowType)values.GetInt32(1);
                daily.Add(new(date, flow, values.GetDecimal(2), values.GetDecimal(3), "Octopus", $"octopus:{flow}:{date:yyyy-MM-dd}", 0, values.IsDBNull(5) ? "" : values.GetString(5), values.GetBoolean(4)));
            }
            foreach (var charge in standing.OrderBy(x => x.Key))
                daily.Add(new(charge.Key, EnergyFlowType.SiteConsumption, 0, 0, "Octopus", $"octopus:standing:{charge.Key:yyyy-MM-dd}", charge.Value, "standing-charge", true));
        }
        await using (var cleanup = Open()) { await using var command = cleanup.CreateCommand(); command.CommandText = "DELETE FROM energy_records WHERE source='Octopus'"; await command.ExecuteNonQueryAsync(cancellationToken); }
        await UpsertDailyEnergyAsync(daily, cancellationToken);
        await using var target = Open(); await using var transaction = await target.BeginTransactionAsync(cancellationToken);
        await using (var delete = target.CreateCommand()) { delete.Transaction = (SqliteTransaction)transaction; delete.CommandText = "DELETE FROM energy_monthly_rollups WHERE source='Octopus'"; await delete.ExecuteNonQueryAsync(cancellationToken); }
        await using (var insert = target.CreateCommand()) { insert.Transaction = (SqliteTransaction)transaction; insert.CommandText = """
            INSERT INTO energy_monthly_rollups(period_start,flow_type,quantity_kwh,cost_gbp,cost_available,source)
            SELECT substr(period_start,1,7)||'-01',flow_type,SUM(quantity_kwh),SUM(cost_gbp),MIN(cost_available),'Octopus'
            FROM energy_records WHERE source='Octopus' GROUP BY substr(period_start,1,7),flow_type
            """; await insert.ExecuteNonQueryAsync(cancellationToken); }
        await transaction.CommitAsync(cancellationToken);
        solarConfigurationRefreshed = true;
    }

    public async Task<SolarAnalysisConfiguration> GetSolarAnalysisConfigurationAsync(CancellationToken cancellationToken = default)
    {
        if (!solarConfigurationRefreshed) await RebuildOctopusRollupsAsync(cancellationToken);
        await using var connection = Open();
        return await ReadSolarConfigurationAsync(connection, cancellationToken);
    }

    public async Task SetSolarManualOverrideAsync(DateOnly? date, CancellationToken cancellationToken = default)
    {
        await using (var connection = Open())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE solar_analysis_configuration SET manual_override_date=@date WHERE id=1";
            command.Parameters.AddWithValue("@date", (object?)date?.ToString("O") ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await RebuildOctopusRollupsAsync(cancellationToken);
    }

    private static async Task<SolarAnalysisConfiguration> RefreshSolarDetectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var observations = new List<DailyExportObservation>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT substr(period_start,1,10),SUM(quantity_kwh)
                FROM octopus_raw_readings raw
                WHERE flow_type=1 AND (NOT EXISTS(SELECT 1 FROM octopus_meter_points WHERE fuel_type='electricity' AND is_export=1)
                  OR EXISTS(SELECT 1 FROM octopus_meter_points meter WHERE meter.fuel_type='electricity' AND meter.is_export=1 AND meter.meter_point=raw.meter_point AND meter.meter_serial=raw.meter_serial))
                GROUP BY substr(period_start,1,10) ORDER BY 1
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) observations.Add(new(DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetDecimal(1)));
        }
        var detection = SolarStartDetector.Detect(observations);
        await using (var save = connection.CreateCommand())
        {
            save.CommandText = "UPDATE solar_analysis_configuration SET detected_start_date=@date,detection_confidence=@confidence,detection_method=@method,detected_at=@at WHERE id=1";
            save.Parameters.AddWithValue("@date", (object?)detection.StartDate?.ToString("O") ?? DBNull.Value);
            save.Parameters.AddWithValue("@confidence", (int)detection.Confidence);
            save.Parameters.AddWithValue("@method", detection.Method);
            save.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            await save.ExecuteNonQueryAsync(cancellationToken);
        }
        return await ReadSolarConfigurationAsync(connection, cancellationToken);
    }

    private static async Task<SolarAnalysisConfiguration> ReadSolarConfigurationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT detected_start_date,detection_confidence,detection_method,manual_override_date,detected_at FROM solar_analysis_configuration WHERE id=1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new(null, SolarDetectionConfidence.None, "No sustained export detected", null, null);
        return new(
            reader.IsDBNull(0) ? null : DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
            (SolarDetectionConfidence)reader.GetInt32(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : DateOnly.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture));
    }

    public async Task<EnergyDashboardSnapshot> GetEnergyDashboardAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = Open(); var today = DateOnly.FromDateTime(DateTime.Now); var month = new DateOnly(today.Year, today.Month, 1); var year = new DateOnly(today.Year, 1, 1);
        async Task<decimal> Sum(EnergyFlowType flow, DateOnly from, DateOnly? to = null) { await using var command = connection.CreateCommand(); command.CommandText = "SELECT COALESCE(SUM(quantity_kwh),0) FROM energy_records WHERE source='Octopus' AND flow_type=@flow AND period_start>=@from" + (to is null ? "" : " AND period_start<@to"); command.Parameters.AddWithValue("@flow", (int)flow); command.Parameters.AddWithValue("@from", from.ToString("O")); if (to is not null) command.Parameters.AddWithValue("@to", to.Value.ToString("O")); return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken) ?? 0, CultureInfo.InvariantCulture); }
        async Task<(decimal Cost,bool Exact)> FlowCost(EnergyFlowType flow, DateOnly from, DateOnly? to) { await using var command = connection.CreateCommand(); command.CommandText = "SELECT COALESCE(SUM(cost_gbp),0),COUNT(*),COALESCE(MIN(cost_available),0) FROM energy_records WHERE source='Octopus' AND flow_type=@flow AND period_start>=@from" + (to is null ? "" : " AND period_start<@to"); command.Parameters.AddWithValue("@flow",(int)flow); command.Parameters.AddWithValue("@from",from.ToString("O")); if(to is not null) command.Parameters.AddWithValue("@to",to.Value.ToString("O")); await using var reader=await command.ExecuteReaderAsync(cancellationToken); await reader.ReadAsync(cancellationToken); return (reader.GetDecimal(0),reader.GetInt32(1)>0&&reader.GetBoolean(2)); }
        async Task<(decimal Cost,bool Exact)> StandingCost(DateOnly from, DateOnly? to)
        {
            await using var command=connection.CreateCommand();
            command.CommandText="""
                WITH usage_meters AS (
                  SELECT DISTINCT substr(period_start,1,10) AS day,meter_point
                  FROM octopus_effective_readings
                  WHERE flow_type IN (0,2) AND period_start>=@from
                  AND (@to IS NULL OR period_start<@to)
                ),
                expected AS (
                  SELECT day,COUNT(*) AS expected_count FROM usage_meters GROUP BY day
                ),
                actual AS (
                  SELECT sc.date AS day,COUNT(DISTINCT sc.meter_point) AS actual_count,SUM(sc.cost_gbp) AS cost
                  FROM octopus_standing_charges sc
                  JOIN usage_meters usage ON usage.day=sc.date AND usage.meter_point=sc.meter_point
                  GROUP BY sc.date
                )
                SELECT COALESCE(SUM(actual.cost),0),COUNT(expected.day),
                       COALESCE(MIN(CASE WHEN COALESCE(actual.actual_count,0)>=expected.expected_count THEN 1 ELSE 0 END),0)
                FROM expected LEFT JOIN actual ON actual.day=expected.day
                """;
            command.Parameters.AddWithValue("@from",from.ToString("O"));
            command.Parameters.AddWithValue("@to",to is null?DBNull.Value:to.Value.ToString("O"));
            await using var reader=await command.ExecuteReaderAsync(cancellationToken); await reader.ReadAsync(cancellationToken);
            return(decimal.Round(reader.GetDecimal(0),8),reader.GetInt32(1)>0&&reader.GetBoolean(2));
        }
        async Task<EnergyCostPeriod> Costs(DateOnly from, DateOnly? to) { var import=await FlowCost(EnergyFlowType.ElectricityImport,from,to); var export=await FlowCost(EnergyFlowType.ElectricityExport,from,to); var gas=await FlowCost(EnergyFlowType.Gas,from,to); var standing=await StandingCost(from,to); return new(import.Cost,-export.Cost,gas.Cost,import.Exact,export.Exact,gas.Exact,standing.Cost,standing.Exact); }
        var daily = new List<EnergyPeriodPoint>(); await using (var command = connection.CreateCommand()) { command.CommandText = "SELECT substr(period_start,1,10),SUM(CASE WHEN flow_type=0 THEN quantity_kwh ELSE 0 END),SUM(CASE WHEN flow_type=1 THEN quantity_kwh ELSE 0 END),SUM(CASE WHEN flow_type=2 THEN quantity_kwh ELSE 0 END),SUM(CASE WHEN flow_type=0 THEN cost_gbp ELSE 0 END),-SUM(CASE WHEN flow_type=1 THEN cost_gbp ELSE 0 END),SUM(CASE WHEN flow_type=2 THEN cost_gbp ELSE 0 END),CASE WHEN SUM(CASE WHEN flow_type=0 THEN 1 ELSE 0 END)>0 AND MIN(CASE WHEN flow_type=0 THEN cost_available ELSE 1 END)=1 THEN 1 ELSE 0 END,CASE WHEN SUM(CASE WHEN flow_type=1 THEN 1 ELSE 0 END)>0 AND MIN(CASE WHEN flow_type=1 THEN cost_available ELSE 1 END)=1 THEN 1 ELSE 0 END,CASE WHEN SUM(CASE WHEN flow_type=2 THEN 1 ELSE 0 END)>0 AND MIN(CASE WHEN flow_type=2 THEN cost_available ELSE 1 END)=1 THEN 1 ELSE 0 END FROM energy_records WHERE source='Octopus' AND period_start>=@from GROUP BY substr(period_start,1,10) ORDER BY 1"; command.Parameters.AddWithValue("@from", today.AddYears(-2).ToString("O")); await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) daily.Add(new(DateOnly.Parse(reader.GetString(0),CultureInfo.InvariantCulture),reader.GetDecimal(1),reader.GetDecimal(2),reader.GetDecimal(3),reader.GetDecimal(4),reader.GetDecimal(5),reader.GetDecimal(6),reader.GetBoolean(7),reader.GetBoolean(8),reader.GetBoolean(9))); }
        var monthly = new List<EnergyPeriodPoint>(); await using (var command = connection.CreateCommand()) { command.CommandText = "SELECT period_start,SUM(CASE WHEN flow_type=0 THEN quantity_kwh ELSE 0 END),SUM(CASE WHEN flow_type=1 THEN quantity_kwh ELSE 0 END),SUM(CASE WHEN flow_type=2 THEN quantity_kwh ELSE 0 END),SUM(CASE WHEN flow_type=0 THEN cost_gbp ELSE 0 END),-SUM(CASE WHEN flow_type=1 THEN cost_gbp ELSE 0 END),SUM(CASE WHEN flow_type=2 THEN cost_gbp ELSE 0 END),CASE WHEN SUM(CASE WHEN flow_type=0 THEN 1 ELSE 0 END)>0 AND MIN(CASE WHEN flow_type=0 THEN cost_available ELSE 1 END)=1 THEN 1 ELSE 0 END,CASE WHEN SUM(CASE WHEN flow_type=1 THEN 1 ELSE 0 END)>0 AND MIN(CASE WHEN flow_type=1 THEN cost_available ELSE 1 END)=1 THEN 1 ELSE 0 END,CASE WHEN SUM(CASE WHEN flow_type=2 THEN 1 ELSE 0 END)>0 AND MIN(CASE WHEN flow_type=2 THEN cost_available ELSE 1 END)=1 THEN 1 ELSE 0 END FROM energy_monthly_rollups WHERE source='Octopus' GROUP BY period_start ORDER BY period_start"; await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) monthly.Add(new(DateOnly.Parse(reader.GetString(0),CultureInfo.InvariantCulture),reader.GetDecimal(1),reader.GetDecimal(2),reader.GetDecimal(3),reader.GetDecimal(4),reader.GetDecimal(5),reader.GetDecimal(6),reader.GetBoolean(7),reader.GetBoolean(8),reader.GetBoolean(9))); }
        await ApplyImportBands(daily,false,today.AddYears(-2)); await ApplyImportBands(monthly,true,today.AddYears(-2));
        await ApplyStandingCharges(daily,false,today.AddYears(-2)); await ApplyStandingCharges(monthly,true,today.AddYears(-2));

        async Task ApplyStandingCharges(List<EnergyPeriodPoint> points,bool byMonth,DateOnly from)
        {
            var values=new Dictionary<DateOnly,(decimal Cost,bool Exact)>(); await using var command=connection.CreateCommand();
            var bucket=byMonth?"substr(expected.day,1,7)":"expected.day";
            command.CommandText=$"""
                WITH usage_meters AS (
                  SELECT DISTINCT substr(period_start,1,10) AS day,meter_point
                  FROM octopus_effective_readings
                  WHERE flow_type IN (0,2) AND period_start>=@from
                ),
                expected AS (
                  SELECT day,COUNT(*) AS expected_count FROM usage_meters GROUP BY day
                ),
                actual AS (
                  SELECT sc.date AS day,COUNT(DISTINCT sc.meter_point) AS actual_count,SUM(sc.cost_gbp) AS cost
                  FROM octopus_standing_charges sc
                  JOIN usage_meters usage ON usage.day=sc.date AND usage.meter_point=sc.meter_point
                  GROUP BY sc.date
                )
                SELECT {bucket},COALESCE(SUM(actual.cost),0),
                       MIN(CASE WHEN COALESCE(actual.actual_count,0)>=expected.expected_count THEN 1 ELSE 0 END)
                FROM expected LEFT JOIN actual ON actual.day=expected.day
                GROUP BY 1
                """;
            command.Parameters.AddWithValue("@from",from.ToString("O")); await using var reader=await command.ExecuteReaderAsync(cancellationToken);
            while(await reader.ReadAsync(cancellationToken)) { var key=DateOnly.Parse(byMonth?$"{reader.GetString(0)}-01":reader.GetString(0),CultureInfo.InvariantCulture); values[key]=(decimal.Round(reader.GetDecimal(1),8),reader.GetBoolean(2)); }
            for(var index=0;index<points.Count;index++) points[index]=values.TryGetValue(points[index].Period,out var value)
                ? points[index] with { StandingChargeGbp=value.Cost,StandingChargeExact=value.Exact }
                : points[index] with { StandingChargeExact=false };
        }

        async Task ApplyImportBands(List<EnergyPeriodPoint> points,bool byMonth,DateOnly from)
        {
            var values=new Dictionary<DateOnly,(decimal PeakKwh,decimal OffKwh,decimal UnknownKwh,decimal PeakCost,decimal OffCost,bool PeakExact,bool OffExact)>(); await using var command=connection.CreateCommand();
            command.CommandText=$"SELECT substr(period_start,1,{(byMonth?7:10)}),rate_band,SUM(quantity_kwh),SUM(COALESCE(cost_gbp,0)),COUNT(*),MIN(CASE WHEN cost_gbp IS NULL THEN 0 ELSE 1 END) FROM octopus_import_band_readings raw WHERE flow_type=0 AND period_start>=@from AND (NOT EXISTS(SELECT 1 FROM octopus_meter_points WHERE fuel_type='electricity' AND is_export=0) OR EXISTS(SELECT 1 FROM octopus_meter_points meter WHERE meter.fuel_type='electricity' AND meter.is_export=0 AND meter.meter_point=raw.meter_point AND meter.meter_serial=raw.meter_serial)) GROUP BY 1,rate_band"; command.Parameters.AddWithValue("@from",from.ToString("O")); await using var reader=await command.ExecuteReaderAsync(cancellationToken);
            while(await reader.ReadAsync(cancellationToken)) { var key=DateOnly.Parse(byMonth?$"{reader.GetString(0)}-01":reader.GetString(0),CultureInfo.InvariantCulture); values.TryGetValue(key,out var value); var band=(ImportRateBand)reader.GetInt32(1); var quantity=reader.GetDecimal(2); var cost=reader.GetDecimal(3); var exact=reader.GetInt32(4)>0&&reader.GetBoolean(5); values[key]=band switch { ImportRateBand.Peak=>value with { PeakKwh=quantity,PeakCost=cost,PeakExact=exact }, ImportRateBand.OffPeak=>value with { OffKwh=quantity,OffCost=cost,OffExact=exact }, _=>value with { UnknownKwh=quantity } }; }
            for(var index=0;index<points.Count;index++) if(values.TryGetValue(points[index].Period,out var value)) points[index]=points[index] with { PeakImportKwh=value.PeakKwh,OffPeakImportKwh=value.OffKwh,UnknownImportKwh=value.UnknownKwh,PeakImportCost=value.PeakCost,OffPeakImportCost=value.OffCost,PeakCostExact=value.PeakExact,OffPeakCostExact=value.OffExact };
        }

        async Task<ImportRateBreakdown> ImportBreakdown(DateOnly from)
        {
            await using var command=connection.CreateCommand(); command.CommandText="SELECT COALESCE(SUM(CASE WHEN rate_band=1 THEN quantity_kwh ELSE 0 END),0),COALESCE(SUM(CASE WHEN rate_band=2 THEN quantity_kwh ELSE 0 END),0),COALESCE(SUM(CASE WHEN rate_band=0 THEN quantity_kwh ELSE 0 END),0),COALESCE(SUM(CASE WHEN rate_band=1 THEN COALESCE(cost_gbp,0) ELSE 0 END),0),COALESCE(SUM(CASE WHEN rate_band=2 THEN COALESCE(cost_gbp,0) ELSE 0 END),0),COALESCE(SUM(CASE WHEN rate_band=1 THEN 1 ELSE 0 END),0),COALESCE(MIN(CASE WHEN rate_band=1 THEN CASE WHEN cost_gbp IS NULL THEN 0 ELSE 1 END ELSE 1 END),0),COALESCE(SUM(CASE WHEN rate_band=2 THEN 1 ELSE 0 END),0),COALESCE(MIN(CASE WHEN rate_band=2 THEN CASE WHEN cost_gbp IS NULL THEN 0 ELSE 1 END ELSE 1 END),0) FROM octopus_import_band_readings raw WHERE flow_type=0 AND period_start>=@from AND (NOT EXISTS(SELECT 1 FROM octopus_meter_points WHERE fuel_type='electricity' AND is_export=0) OR EXISTS(SELECT 1 FROM octopus_meter_points meter WHERE meter.fuel_type='electricity' AND meter.is_export=0 AND meter.meter_point=raw.meter_point AND meter.meter_serial=raw.meter_serial))"; command.Parameters.AddWithValue("@from",from.ToString("O")); await using var reader=await command.ExecuteReaderAsync(cancellationToken); await reader.ReadAsync(cancellationToken); return new(reader.GetDecimal(0),reader.GetDecimal(1),reader.GetDecimal(2),reader.GetDecimal(3),reader.GetDecimal(4),reader.GetInt32(5)>0&&reader.GetBoolean(6),reader.GetInt32(7)>0&&reader.GetBoolean(8));
        }
        await using var range = connection.CreateCommand(); range.CommandText = "SELECT MIN(period_start),MAX(period_end) FROM octopus_raw_readings"; await using var rangeReader = await range.ExecuteReaderAsync(cancellationToken); await rangeReader.ReadAsync(cancellationToken); DateTimeOffset? dataFrom = rangeReader.IsDBNull(0) ? null : DateTimeOffset.Parse(rangeReader.GetString(0),CultureInfo.InvariantCulture); DateTimeOffset? dataTo = rangeReader.IsDBNull(1) ? null : DateTimeOffset.Parse(rangeReader.GetString(1),CultureInfo.InvariantCulture); await rangeReader.DisposeAsync();
        var statuses = await GetSyncStatusesAsync(cancellationToken); var status = statuses.FirstOrDefault(x => x.Source == "Octopus Energy"); var meters = await GetOctopusMeterPointsAsync(cancellationToken); var tariffs = await GetOctopusTariffPeriodsAsync(cancellationToken); var warnings = new List<string>();
        var solarConfiguration=await ReadSolarConfigurationAsync(connection,cancellationToken); var solarStartDate=solarConfiguration.EffectiveStartDate; decimal preSolarExportKwh=0; int invalidExportMeterReadings=0;
        if(solarStartDate is not null) { await using var preSolar=connection.CreateCommand(); preSolar.CommandText="SELECT COALESCE(SUM(quantity_kwh),0) FROM octopus_raw_readings raw WHERE flow_type=1 AND substr(period_start,1,10)<@solar AND (NOT EXISTS(SELECT 1 FROM octopus_meter_points WHERE fuel_type='electricity' AND is_export=1) OR EXISTS(SELECT 1 FROM octopus_meter_points meter WHERE meter.fuel_type='electricity' AND meter.is_export=1 AND meter.meter_point=raw.meter_point AND meter.meter_serial=raw.meter_serial))"; preSolar.Parameters.AddWithValue("@solar",solarStartDate.Value.ToString("O")); preSolarExportKwh=Convert.ToDecimal(await preSolar.ExecuteScalarAsync(cancellationToken)??0,CultureInfo.InvariantCulture); }
        if(meters.Any(x=>x.FuelType=="electricity"&&x.IsExport)) { await using var invalid=connection.CreateCommand(); invalid.CommandText="SELECT COUNT(*) FROM octopus_raw_readings raw WHERE flow_type=1 AND NOT EXISTS(SELECT 1 FROM octopus_meter_points meter WHERE meter.fuel_type='electricity' AND meter.is_export=1 AND meter.meter_point=raw.meter_point AND meter.meter_serial=raw.meter_serial)"; invalidExportMeterReadings=Convert.ToInt32(await invalid.ExecuteScalarAsync(cancellationToken)??0,CultureInfo.InvariantCulture); }
        var exportValidation = await BuildSolarExportValidationAsync(connection, await ReadSolarCapacityKwpAsync(connection, cancellationToken), cancellationToken);
        if (!meters.Any(x => x.FuelType == "electricity" && !x.IsExport)) warnings.Add("Electricity import meter not found."); if (!meters.Any(x => x.FuelType == "electricity" && x.IsExport)) warnings.Add("Electricity export meter not found; export cards will remain empty."); if (!meters.Any(x => x.FuelType == "gas")) warnings.Add("Gas meter not found; gas cards will remain empty.");
        if (status?.Succeeded == false) warnings.Add($"Latest sync failed: {status.Message}");
        warnings.AddRange(EnergyDataQuality.Evaluate(daily, meters, today,solarStartDate,preSolarExportKwh,invalidExportMeterReadings,solarConfiguration.IsManualOverride,exportValidation));
        if(solarStartDate is null&&meters.Any(x=>x.FuelType=="electricity"&&x.IsExport)) warnings.Add("Solar start could not be detected because export was not sustained. Isolated export readings remain in raw history but are excluded from trusted totals; set a manual override if the export history is incomplete.");
        var allocationStore = new SupplierAllocationStore(databasePath);
        var allocationGaps = await allocationStore.GapsAsync(cancellationToken);
        warnings.AddRange(EnergyCostQuality.Evaluate(daily, tariffs, "dd MMM yyyy", allocationGaps.Where(g => g.CoversMissingImportCosts).Select(g => g.Day).ToHashSet()));
        foreach (var gap in allocationGaps)
        {
            var reason = gap.Rejected == 0 ? "supplier allocation pending" : "supplier allocation unavailable or discrepant";
            warnings.Add($"{gap.Day:dd MMM yyyy}: {reason} for {gap.Pending + gap.Rejected} interval(s), {gap.Kwh:N3} kWh. Manual and scheduled sync retry these intervals; pricing remains unavailable until supplier data reconciles.");
        }
        var completenessWindow = OctopusReportingCompleteness.RecentExpectedWindow(today);
        foreach (var expected in meters.Select(x => x.FuelType == "gas" ? EnergyFlowType.Gas : x.IsExport ? EnergyFlowType.ElectricityExport : EnergyFlowType.ElectricityImport).Distinct())
        {
            await using var countCommand = connection.CreateCommand(); countCommand.CommandText = "SELECT COUNT(DISTINCT substr(period_start,1,10)) FROM energy_records WHERE source='Octopus' AND flow_type=@flow AND period_start>=@from AND period_start<@to"; countCommand.Parameters.AddWithValue("@flow", (int)expected); countCommand.Parameters.AddWithValue("@from", completenessWindow.From.ToString("O")); countCommand.Parameters.AddWithValue("@to", completenessWindow.ToExclusive.ToString("O"));
            var count = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0, CultureInfo.InvariantCulture); if (dataFrom is not null && dataFrom.Value.Date <= completenessWindow.From.ToDateTime(TimeOnly.MinValue) && count < completenessWindow.ExpectedDays) warnings.Add(OctopusReportingCompleteness.IncompleteWarning(expected, count, completenessWindow));
        }
        var todayCost=await Costs(today,today.AddDays(1)); var monthCost=await Costs(month,null); var yearCost=await Costs(year,null); var monthBreakdown=await ImportBreakdown(month); var yearBreakdown=await ImportBreakdown(year);
        if(monthBreakdown.UnknownKwh>0.1m) warnings.Add($"{monthBreakdown.UnknownKwh:N1} kWh of this month's import could not be classified as Intelligent Go peak or off-peak.");
        var allocationMonths = await allocationStore.SummaryAsync(cancellationToken);
        foreach (var allocation in allocationMonths.Where(x => !x.Complete)) warnings.Add($"{allocation.Month}: four-rate supplier allocation is incomplete or discrepant ({allocation.Reconciled}/{allocation.Intervals} intervals); legacy pricing is not used.");
        return new(await Sum(EnergyFlowType.ElectricityImport,today,today.AddDays(1)),await Sum(EnergyFlowType.ElectricityExport,today,today.AddDays(1)),await Sum(EnergyFlowType.Gas,today,today.AddDays(1)),await Sum(EnergyFlowType.ElectricityImport,month),await Sum(EnergyFlowType.ElectricityExport,month),await Sum(EnergyFlowType.Gas,month),await Sum(EnergyFlowType.ElectricityImport,year),await Sum(EnergyFlowType.ElectricityExport,year),await Sum(EnergyFlowType.Gas,year),dataFrom,dataTo,status,daily,monthly,warnings,todayCost,monthCost,yearCost,monthBreakdown,yearBreakdown) { SupplierAllocationMonths = allocationMonths };
    }

    private static async Task<decimal> ReadSolarCapacityKwpAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key=@key";
        command.Parameters.AddWithValue("@key", SettingKeys.SolarCapacityKwp);
        var value = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var capacity) && capacity > 0 ? capacity : 5.76m;
    }

    private static async Task<SolarExportValidationSummary> BuildSolarExportValidationAsync(SqliteConnection connection, decimal capacityKwp, CancellationToken cancellationToken)
    {
        var halfHourlyLimit = SolarExportCapability.HalfHourlyLimitKwh(capacityKwp);
        var dailyLimit = SolarExportCapability.DailyLimitKwh(capacityKwp);
        var halfHourlyBreachCount = 0;
        decimal? largestHalfHourly = null;
        DateOnly? largestHalfHourlyDate = null;
        var halfHourlyBreachDates = new HashSet<DateOnly>();
        var rawDaily = new Dictionary<DateOnly, decimal>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT period_start,period_end,quantity_kwh
            FROM octopus_raw_readings raw
            WHERE flow_type=1 AND (
              NOT EXISTS(SELECT 1 FROM octopus_meter_points WHERE fuel_type='electricity' AND is_export=1)
              OR EXISTS(SELECT 1 FROM octopus_meter_points meter WHERE meter.fuel_type='electricity' AND meter.is_export=1 AND meter.meter_point=raw.meter_point AND meter.meter_serial=raw.meter_serial)
            )
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var start = DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
            var end = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
            var quantity = reader.GetDecimal(2);
            var date = DateOnly.FromDateTime(start.LocalDateTime);
            rawDaily[date] = rawDaily.GetValueOrDefault(date) + quantity;
            var hours = Math.Max((decimal)(end - start).TotalHours, 0.5m);
            var intervalLimit = SolarExportCapability.IntervalLimitKwh(capacityKwp, hours);
            if (quantity >= 0 && quantity <= intervalLimit) continue;
            halfHourlyBreachCount++;
            halfHourlyBreachDates.Add(date);
            if (largestHalfHourly is null || quantity > largestHalfHourly)
            {
                largestHalfHourly = quantity;
                largestHalfHourlyDate = date;
            }
        }
        var dailyBreaches = rawDaily.Where(pair => pair.Value > dailyLimit).OrderByDescending(pair => pair.Value).ToArray();
        return new(capacityKwp, halfHourlyLimit, halfHourlyBreachCount, largestHalfHourly, largestHalfHourlyDate, dailyLimit, dailyBreaches.Length, dailyBreaches.Length == 0 ? null : dailyBreaches[0].Value, dailyBreaches.Length == 0 ? null : dailyBreaches[0].Key)
        {
            HalfHourlyBreachDates = halfHourlyBreachDates.OrderBy(value => value).ToArray(),
            DailyBreachDates = dailyBreaches.Select(pair => pair.Key).OrderBy(value => value).ToArray()
        };
    }

    public async Task<IReadOnlyList<EnergyIntervalDetail>> GetOctopusIntervalsAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var intervals = new List<EnergyIntervalDetail>();
        await using var connection = Open();
        var solar = await ReadSolarConfigurationAsync(connection, cancellationToken);
        var solarCapacity = await ReadSolarCapacityKwpAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH export_daily AS (
              SELECT substr(period_start,1,10) AS export_date,SUM(quantity_kwh) AS export_kwh
              FROM octopus_effective_readings raw
              WHERE flow_type=1 AND quantity_kwh>=0 AND (NOT EXISTS(SELECT 1 FROM octopus_meter_points WHERE fuel_type='electricity' AND is_export=1) OR EXISTS(SELECT 1 FROM octopus_meter_points meter WHERE meter.fuel_type='electricity' AND meter.is_export=1 AND meter.meter_point=raw.meter_point AND meter.meter_serial=raw.meter_serial))
              GROUP BY substr(period_start,1,10)
            )
            SELECT period_start,period_end,flow_type,quantity_kwh,cost_gbp,rate_band,unit_rate_pence,tariff_code
            FROM octopus_effective_readings raw
            WHERE substr(period_start,1,10)=@date AND (
              (flow_type=0 AND (NOT EXISTS(SELECT 1 FROM octopus_meter_points WHERE fuel_type='electricity' AND is_export=0) OR EXISTS(SELECT 1 FROM octopus_meter_points meter WHERE meter.fuel_type='electricity' AND meter.is_export=0 AND meter.meter_point=raw.meter_point AND meter.meter_serial=raw.meter_serial))) OR
              (flow_type=1 AND @solar<>'' AND @date>=@solar AND quantity_kwh>=0 AND quantity_kwh<=@solarCapacity*(CASE WHEN ((julianday(period_end)-julianday(period_start))*24.0)<0.5 THEN 0.5 ELSE ((julianday(period_end)-julianday(period_start))*24.0) END)*@intervalTolerance AND NOT EXISTS(SELECT 1 FROM export_daily day WHERE day.export_date=substr(raw.period_start,1,10) AND day.export_kwh>@dailyLimit) AND (NOT EXISTS(SELECT 1 FROM octopus_meter_points WHERE fuel_type='electricity' AND is_export=1) OR EXISTS(SELECT 1 FROM octopus_meter_points meter WHERE meter.fuel_type='electricity' AND meter.is_export=1 AND meter.meter_point=raw.meter_point AND meter.meter_serial=raw.meter_serial))) OR
              (flow_type=2 AND (NOT EXISTS(SELECT 1 FROM octopus_meter_points WHERE fuel_type='gas') OR EXISTS(SELECT 1 FROM octopus_meter_points meter WHERE meter.fuel_type='gas' AND meter.meter_point=raw.meter_point AND meter.meter_serial=raw.meter_serial)))
            )
            ORDER BY period_start,flow_type
            """;
        command.Parameters.AddWithValue("@date", date.ToString("O"));
        command.Parameters.AddWithValue("@solar", solar.EffectiveStartDate?.ToString("O") ?? "");
        command.Parameters.AddWithValue("@solarCapacity", solarCapacity);
        command.Parameters.AddWithValue("@intervalTolerance", SolarExportCapability.IntervalTolerance);
        command.Parameters.AddWithValue("@dailyLimit", SolarExportCapability.DailyLimitKwh(solarCapacity));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            intervals.Add(new(
                DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                (EnergyFlowType)reader.GetInt32(2), reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                (ImportRateBand)reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.IsDBNull(7) ? "" : reader.GetString(7)));
        }
        return intervals;
    }

    public async Task<IReadOnlyList<HomeEvent>> GetHomeEventsAsync(CancellationToken cancellationToken = default)
    {
        var events = new List<HomeEvent>(); await using var connection = Open(); await using var command = connection.CreateCommand(); command.CommandText = "SELECT id,event_date,label,category FROM home_events ORDER BY event_date,id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) events.Add(new(reader.GetInt64(0), DateOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetString(2), reader.GetString(3))); return events;
    }

    public async Task SaveHomeEventAsync(HomeEvent homeEvent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(homeEvent.Label)) throw new ArgumentException("Event label is required.", nameof(homeEvent));
        await using var connection = Open(); await using var command = connection.CreateCommand();
        command.CommandText = homeEvent.Id == 0 ? "INSERT INTO home_events(event_date,label,category,created_at) VALUES(@date,@label,@category,@at)" : "UPDATE home_events SET event_date=@date,label=@label,category=@category WHERE id=@id";
        command.Parameters.AddWithValue("@date", homeEvent.Date.ToString("O")); command.Parameters.AddWithValue("@label", homeEvent.Label.Trim()); command.Parameters.AddWithValue("@category", string.IsNullOrWhiteSpace(homeEvent.Category) ? "Home" : homeEvent.Category.Trim()); command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
        if (homeEvent.Id != 0) command.Parameters.AddWithValue("@id", homeEvent.Id); await command.ExecuteNonQueryAsync(cancellationToken);
        await command.DisposeAsync(); await connection.DisposeAsync();
    }

    public async Task DeleteHomeEventAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = Open(); await using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM home_events WHERE id=@id"; command.Parameters.AddWithValue("@id", id); await command.ExecuteNonQueryAsync(cancellationToken);
        await command.DisposeAsync(); await connection.DisposeAsync();
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JoakimHomeDashboard.Application;
using JoakimHomeDashboard.Domain;
using Microsoft.Data.Sqlite;

namespace JoakimHomeDashboard.Infrastructure;

public sealed record AllocationReading(long Id, DateTimeOffset Start, DateTimeOffset End, decimal Quantity, string LocalDate);
public sealed record SupplierAllocation(DateTimeOffset Start, DateTimeOffset End, string Band, decimal Quantity, decimal RateApplied,
    decimal CostPence, decimal NetPence, decimal GrossPence, decimal VatRate, string TaxBasis);
public sealed record AllocationCheck(long RawId, decimal MeterQuantity, decimal AllocatedQuantity, string Status);
public sealed record SupplierEvidence(string Kind, string RetrievedAt, string Payload);
public sealed record SupplierAllocationGap(DateOnly Day, int Pending, int Rejected, decimal Kwh, bool CoversMissingImportCosts);
public sealed record SupplierSyncHealth(DateTimeOffset? LastAttemptAt, DateTimeOffset? LastCompletedAt, DateTimeOffset? LastFullyReconciledAt,
    int ExpectedIntervals, int ReconciledIntervals, decimal ClassifiedKwh, decimal UnclassifiedKwh, IReadOnlyList<string> MissingTimestamps, string RetryState);
public sealed record AllocationAudit(string Period, int Intervals, int Reconciled, decimal MeterKwh, decimal AllocatedKwh, decimal KnownGrossGbp)
{
    public bool Complete => Intervals == Reconciled && Math.Abs(MeterKwh - AllocatedKwh) <= 0.000001m;
}

public sealed class SupplierAllocationStore(string databasePath)
{
    public const decimal QuantityTolerance = 0.000001m;
    public static readonly string[] Bands = ["ECO7_DAY", "ECO7_NIGHT", "EV_DEVICE_PEAK", "EV_DEVICE_OFF_PEAK"];
    private SqliteConnection Open() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false, ForeignKeys = true }.ToString()); c.Open(); return c; }
    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public const string Schema = """
        CREATE TABLE IF NOT EXISTS octopus_gas_supplier_prices(
          raw_id INTEGER PRIMARY KEY REFERENCES octopus_raw_readings(id),period_start TEXT NOT NULL,period_end TEXT NOT NULL,
          quantity_kwh TEXT NOT NULL,rate_pence TEXT NOT NULL,cost_gbp TEXT NOT NULL,payment_method TEXT NOT NULL,
          tariff_code TEXT NOT NULL,evidence_json TEXT NOT NULL,retrieved_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS octopus_supplier_tariffs(
          account TEXT NOT NULL,meter_point TEXT NOT NULL,tariff_code TEXT NOT NULL,valid_from TEXT NOT NULL,valid_to TEXT,
          metadata_json TEXT NOT NULL,PRIMARY KEY(account,meter_point,tariff_code,valid_from));
        CREATE TABLE IF NOT EXISTS octopus_allocation_revisions(
          id INTEGER PRIMARY KEY,account TEXT NOT NULL,meter_point TEXT NOT NULL,tariff_code TEXT NOT NULL,
          period_from TEXT NOT NULL,period_to TEXT NOT NULL,retrieved_at TEXT NOT NULL,source TEXT NOT NULL,
          response_hash TEXT NOT NULL,response_json TEXT NOT NULL,parser_version INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS octopus_supplier_allocations(
          revision_id INTEGER NOT NULL REFERENCES octopus_allocation_revisions(id),raw_id INTEGER,
          period_start TEXT NOT NULL,period_end TEXT NOT NULL,band TEXT NOT NULL,quantity_kwh TEXT NOT NULL,
          rate_applied_pence TEXT NOT NULL,cost_pence TEXT NOT NULL,net_pence TEXT NOT NULL,gross_pence TEXT NOT NULL,
          vat_rate TEXT NOT NULL,tax_basis TEXT NOT NULL,provenance TEXT NOT NULL,
          PRIMARY KEY(revision_id,period_start,period_end,band));
        CREATE INDEX IF NOT EXISTS ix_supplier_allocation_raw ON octopus_supplier_allocations(raw_id,revision_id);
        CREATE TABLE IF NOT EXISTS octopus_allocation_checks(
          revision_id INTEGER NOT NULL REFERENCES octopus_allocation_revisions(id),raw_id INTEGER NOT NULL REFERENCES octopus_raw_readings(id),
          meter_quantity TEXT NOT NULL,allocated_quantity TEXT NOT NULL,status TEXT NOT NULL,PRIMARY KEY(raw_id,revision_id));
        CREATE TABLE IF NOT EXISTS octopus_supplier_evidence(
          id INTEGER PRIMARY KEY,account TEXT NOT NULL,kind TEXT NOT NULL,period_from TEXT,period_to TEXT,
          retrieved_at TEXT NOT NULL,response_hash TEXT NOT NULL,payload_json TEXT NOT NULL,
          UNIQUE(account,kind,response_hash));
        CREATE VIEW IF NOT EXISTS octopus_four_rate_readings AS
          SELECT r.id FROM octopus_raw_readings r WHERE r.flow_type=0 AND EXISTS(
            SELECT 1 FROM octopus_supplier_tariffs t WHERE t.meter_point=r.meter_point
              AND julianday(r.period_end)>julianday(t.valid_from)
              AND (t.valid_to IS NULL OR julianday(r.period_start)<julianday(t.valid_to)));
        CREATE VIEW IF NOT EXISTS octopus_current_allocation_checks AS
          SELECT c.* FROM octopus_allocation_checks c WHERE c.revision_id=(SELECT MAX(n.revision_id) FROM octopus_allocation_checks n WHERE n.raw_id=c.raw_id);
        CREATE VIEW IF NOT EXISTS octopus_accepted_allocations AS
          SELECT a.* FROM octopus_supplier_allocations a
          JOIN octopus_current_allocation_checks c ON c.raw_id=a.raw_id AND c.revision_id=a.revision_id
          JOIN octopus_raw_readings r ON r.id=a.raw_id
          WHERE c.status='reconciled' AND abs(CAST(c.meter_quantity AS REAL)-r.quantity_kwh)<=0.000001
            AND julianday(r.period_start)=julianday(a.period_start) AND julianday(r.period_end)=julianday(a.period_end);
        CREATE VIEW IF NOT EXISTS octopus_effective_readings AS
          SELECT r.id,r.period_start,r.period_end,r.flow_type,r.quantity_kwh,
            CASE WHEN f.id IS NOT NULL THEN a.gross_pence/100.0 WHEN r.flow_type=2 AND r.cost_gbp IS NULL THEN CAST(g.cost_gbp AS REAL) ELSE r.cost_gbp END AS cost_gbp,
            r.meter_point,r.meter_serial,r.tariff_code,r.external_id,
            CASE WHEN f.id IS NOT NULL THEN 0 ELSE r.rate_band END AS rate_band,
            CASE WHEN f.id IS NOT NULL THEN NULL ELSE r.unit_rate_pence END AS unit_rate_pence,r.rate_band_version
          FROM octopus_raw_readings r LEFT JOIN octopus_four_rate_readings f ON f.id=r.id
          LEFT JOIN octopus_gas_supplier_prices g ON g.raw_id=r.id AND CAST(g.quantity_kwh AS REAL)=r.quantity_kwh
            AND julianday(g.period_start)=julianday(r.period_start) AND julianday(g.period_end)=julianday(r.period_end)
          LEFT JOIN (SELECT raw_id,SUM(CAST(gross_pence AS REAL)) gross_pence FROM octopus_accepted_allocations GROUP BY raw_id) a ON a.raw_id=r.id;
        CREATE VIEW IF NOT EXISTS octopus_import_band_readings AS
          SELECT r.period_start,r.flow_type,r.quantity_kwh,r.cost_gbp,r.meter_point,r.meter_serial,r.rate_band
          FROM octopus_effective_readings r WHERE r.flow_type=0 AND (NOT EXISTS(SELECT 1 FROM octopus_four_rate_readings f WHERE f.id=r.id) OR r.cost_gbp IS NULL)
          UNION ALL
          SELECT r.period_start,0,CAST(a.quantity_kwh AS REAL),CAST(a.gross_pence AS REAL)/100.0,r.meter_point,r.meter_serial,
            CASE WHEN a.band IN ('ECO7_DAY','EV_DEVICE_PEAK') THEN 1 ELSE 2 END
          FROM octopus_accepted_allocations a JOIN octopus_raw_readings r ON r.id=a.raw_id
          JOIN octopus_four_rate_readings f ON f.id=r.id;
        """;

    public async Task<List<AllocationReading>> UnpricedGasAsync(string meterPoint, CancellationToken ct = default)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT id,period_start,period_end,quantity_kwh FROM octopus_effective_readings WHERE flow_type=2 AND meter_point=@m AND cost_gbp IS NULL ORDER BY julianday(period_start)";
        cmd.Parameters.AddWithValue("@m",meterPoint); using var r=await cmd.ExecuteReaderAsync(ct); var rows=new List<AllocationReading>();
        while(await r.ReadAsync(ct)) rows.Add(new(r.GetInt64(0),DateTimeOffset.Parse(r.GetString(1)),DateTimeOffset.Parse(r.GetString(2)),r.GetDecimal(3),r.GetString(1)[..10]));
        return rows;
    }

    public async Task SaveGasPriceAsync(AllocationReading row, decimal rate, string method, string tariff, string evidence, CancellationToken ct=default)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="INSERT INTO octopus_gas_supplier_prices VALUES(@id,@s,@e,@q,@r,@cost,@method,@tariff,@evidence,@at) ON CONFLICT(raw_id) DO NOTHING";
        foreach(var (name,value) in new (string,object)[]{("@id",row.Id),("@s",Iso(row.Start)),("@e",Iso(row.End)),("@q",Number(row.Quantity)),("@r",Number(rate)),("@cost",Number(row.Quantity*rate/100m)),("@method",method),("@tariff",tariff),("@evidence",evidence),("@at",Iso(DateTimeOffset.UtcNow))}) cmd.Parameters.AddWithValue(name,value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RegisterAsync(OctopusTariffPeriod period, string metadata, CancellationToken ct = default)
    {
        if (period.ValidFrom is null) throw new InvalidOperationException("Four-rate agreement has no transition timestamp.");
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO octopus_supplier_tariffs VALUES(@a,@m,@t,@f,@to,@json) ON CONFLICT(account,meter_point,tariff_code,valid_from) DO UPDATE SET valid_to=@to,metadata_json=@json";
        cmd.Parameters.AddWithValue("@a", period.AccountNumber); cmd.Parameters.AddWithValue("@m", period.MeterPoint); cmd.Parameters.AddWithValue("@t", period.TariffCode);
        cmd.Parameters.AddWithValue("@f", Iso(period.ValidFrom.Value)); cmd.Parameters.AddWithValue("@to", (object?)period.ValidTo?.ToUniversalTime().ToString("O") ?? DBNull.Value); cmd.Parameters.AddWithValue("@json", metadata);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<AllocationReading>> ReadingsAsync(OctopusTariffPeriod tariff, CancellationToken ct = default)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,period_start,period_end,quantity_kwh FROM octopus_raw_readings WHERE flow_type=0 AND meter_point=@m AND julianday(period_end)>julianday(@f) AND (@to IS NULL OR julianday(period_start)<julianday(@to)) ORDER BY julianday(period_start)";
        cmd.Parameters.AddWithValue("@m", tariff.MeterPoint); cmd.Parameters.AddWithValue("@f", Iso(tariff.ValidFrom!.Value)); cmd.Parameters.AddWithValue("@to", (object?)tariff.ValidTo?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        using var r = await cmd.ExecuteReaderAsync(ct); var rows = new List<AllocationReading>();
        while (await r.ReadAsync(ct)) rows.Add(new(r.GetInt64(0), DateTimeOffset.Parse(r.GetString(1)), DateTimeOffset.Parse(r.GetString(2)), r.GetDecimal(3), r.GetString(1)[..10]));
        return rows;
    }

    public async Task<List<AllocationReading>> IncrementalReadingsAsync(List<AllocationReading> readings, CancellationToken ct = default, int overlapDays = 3)
    {
        if (overlapDays is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(overlapDays));
        if (readings.Count == 0) return [];
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT raw_id FROM octopus_accepted_allocations";
        using var reader = await cmd.ExecuteReaderAsync(ct); var accepted = new HashSet<long>();
        while (await reader.ReadAsync(ct)) accepted.Add(reader.GetInt64(0));
        var overlapFrom = readings.Max(r => r.End).AddDays(-overlapDays);
        // Retry gaps of any age and whole local days within a three-day supplier revision window.
        var days = readings.Where(r => r.End > overlapFrom || !accepted.Contains(r.Id)).Select(r => r.LocalDate).ToHashSet();
        return readings.Where(r => days.Contains(r.LocalDate)).ToList();
    }

    public static IReadOnlyList<AllocationCheck> Reconcile(IReadOnlyList<AllocationReading> readings, IReadOnlyList<SupplierAllocation> allocations)
    {
        var output = new List<AllocationCheck>();
        var groups = allocations.GroupBy(a => (a.Start, a.End)).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var r in readings)
        {
            var found = groups.GetValueOrDefault((r.Start, r.End)) ?? [];
            var sum = found.Sum(a => a.Quantity);
            var status = found.Length == 0 ? "missing_allocation" : found.Any(a => !Bands.Contains(a.Band) || a.Quantity < 0) ? "invalid_band"
                : found.Select(a => a.Band).Distinct().Count() != found.Length ? "duplicate_band"
                : readings.Count(x => x.Start == r.Start && x.End == r.End) != 1 ? "ambiguous_meter"
                : Math.Abs(sum - r.Quantity) > QuantityTolerance ? "quantity_mismatch" : "reconciled";
            output.Add(new(r.Id, r.Quantity, sum, status));
        }
        if (allocations.Any(a => !readings.Any(r => r.Start == a.Start && r.End == a.End)))
            return output.Select(c => c with { Status = "allocation_without_meter" }).ToArray();
        if (output.All(c => c.Status == "reconciled") && Math.Abs(output.Sum(c => c.MeterQuantity) - output.Sum(c => c.AllocatedQuantity)) > QuantityTolerance)
            return output.Select(c => c with { Status = "aggregate_mismatch" }).ToArray();
        return output;
    }

    public async Task<int> SaveAsync(OctopusTariffPeriod tariff, IReadOnlyList<AllocationReading> readings, IReadOnlyList<SupplierAllocation> allocations, string response, string? error = null, CancellationToken ct = default)
    {
        if (readings.Count == 0) return 0;
        var checks = Reconcile(readings, allocations);
        if (error is not null) checks = checks.Select(c => c with { Status = error }).ToArray();
        using var c = Open(); using var tx = c.BeginTransaction();
        async Task Execute(string sql, params (string, object?)[] parameters)
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var fingerprint = Hash(response + "|parser=1|" + JsonSerializer.Serialize(allocations) + string.Join(";", checks.Select(x => $"{x.RawId}:{Number(x.MeterQuantity)}:{x.Status}")));
        using (var existing = c.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT id FROM octopus_allocation_revisions WHERE meter_point=@m AND tariff_code=@t AND period_from=@f AND period_to=@to ORDER BY id DESC LIMIT 1";
            existing.Parameters.AddWithValue("@m", tariff.MeterPoint); existing.Parameters.AddWithValue("@t", tariff.TariffCode); existing.Parameters.AddWithValue("@f", Iso(readings.Min(r => r.Start))); existing.Parameters.AddWithValue("@to", Iso(readings.Max(r => r.End)));
            var latest = await existing.ExecuteScalarAsync(ct);
            if (latest is not null)
            {
                using var hash = c.CreateCommand(); hash.Transaction = tx; hash.CommandText = "SELECT response_hash FROM octopus_allocation_revisions WHERE id=@id"; hash.Parameters.AddWithValue("@id", latest);
                if ((string?)await hash.ExecuteScalarAsync(ct) == fingerprint) return checks.Count(x => x.Status == "reconciled");
            }
        }
        await Execute("INSERT INTO octopus_allocation_revisions(account,meter_point,tariff_code,period_from,period_to,retrieved_at,source,response_hash,response_json,parser_version) VALUES(@a,@m,@t,@f,@to,@at,'gbrCostOfUsage',@h,@json,1)",
            ("@a",tariff.AccountNumber),("@m",tariff.MeterPoint),("@t",tariff.TariffCode),("@f",Iso(readings.Min(r=>r.Start))),("@to",Iso(readings.Max(r=>r.End))),("@at",Iso(DateTimeOffset.UtcNow)),("@h",fingerprint),("@json",response));
        using var idCommand = c.CreateCommand(); idCommand.Transaction = tx; idCommand.CommandText = "SELECT last_insert_rowid()"; var revision = (long)(await idCommand.ExecuteScalarAsync(ct))!;
        foreach (var a in allocations)
        {
            var raw = readings.Where(r => r.Start == a.Start && r.End == a.End).ToArray();
            await Execute("INSERT INTO octopus_supplier_allocations VALUES(@v,@r,@s,@e,@b,@q,@rate,@cost,@net,@gross,@vat,@basis,'supplier_calculated_estimate')",
                ("@v",revision),("@r",raw.Length == 1 ? raw[0].Id : null),("@s",Iso(a.Start)),("@e",Iso(a.End)),("@b",a.Band),("@q",Number(a.Quantity)),("@rate",Number(a.RateApplied)),("@cost",Number(a.CostPence)),("@net",Number(a.NetPence)),("@gross",Number(a.GrossPence)),("@vat",Number(a.VatRate)),("@basis",a.TaxBasis));
        }
        foreach (var check in checks) await Execute("INSERT INTO octopus_allocation_checks VALUES(@v,@r,@m,@a,@status)",("@v",revision),("@r",check.RawId),("@m",Number(check.MeterQuantity)),("@a",Number(check.AllocatedQuantity)),("@status",check.Status));
        tx.Commit(); return checks.Count(x => x.Status == "reconciled");
    }

    public async Task EvidenceAsync(string account, string kind, string payload, CancellationToken ct = default)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO octopus_supplier_evidence(account,kind,retrieved_at,response_hash,payload_json) VALUES(@a,@k,@at,@h,@p)";
        cmd.Parameters.AddWithValue("@a",account); cmd.Parameters.AddWithValue("@k",kind); cmd.Parameters.AddWithValue("@at",Iso(DateTimeOffset.UtcNow)); cmd.Parameters.AddWithValue("@h",Hash(payload)); cmd.Parameters.AddWithValue("@p",payload);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SupplierSyncHealth> GetHealthAsync(CancellationToken ct = default)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key='octopus.allocations.health'";
        var saved = await cmd.ExecuteScalarAsync(ct) as string;
        var previous = saved is null ? null : JsonSerializer.Deserialize<SupplierSyncHealth>(saved);
        cmd.CommandText = "SELECT e.period_start,e.quantity_kwh,e.cost_gbp FROM octopus_effective_readings e JOIN octopus_four_rate_readings f ON f.id=e.id ORDER BY julianday(e.period_start)";
        using var r = await cmd.ExecuteReaderAsync(ct); int expected = 0, reconciled = 0; decimal classified = 0, unclassified = 0; var missing = new List<string>();
        while (await r.ReadAsync(ct)) { expected++; if (r.IsDBNull(2)) { missing.Add(r.GetString(0)); unclassified += r.GetDecimal(1); } else { reconciled++; classified += r.GetDecimal(1); } }
        return new(previous?.LastAttemptAt, previous?.LastCompletedAt, previous?.LastFullyReconciledAt, expected, reconciled, classified, unclassified, missing, previous?.RetryState ?? "not_started");
    }

    public async Task CaptureHealthAsync(string retryState, CancellationToken ct = default)
    {
        var health = await GetHealthAsync(ct); var now = DateTimeOffset.UtcNow;
        var complete = health.ExpectedIntervals == health.ReconciledIntervals
            && (await AuditAsync(false, ct)).All(d => d.Complete) && (await AuditAsync(true, ct)).All(m => m.Complete);
        health = health with { LastAttemptAt = retryState == "running" ? now : health.LastAttemptAt,
            LastCompletedAt = retryState == "running" ? health.LastCompletedAt : now,
            LastFullyReconciledAt = retryState == "complete" && complete && health.ExpectedIntervals > 0 ? now : health.LastFullyReconciledAt,
            RetryState = retryState == "complete" && !complete ? "pending" : retryState };
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key,value,is_secret,updated_at) VALUES('octopus.allocations.health',@v,0,@at) ON CONFLICT(key) DO UPDATE SET value=@v,updated_at=@at";
        cmd.Parameters.AddWithValue("@v", JsonSerializer.Serialize(health)); cmd.Parameters.AddWithValue("@at", Iso(now)); await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SupplierAllocationGap>> GapsAsync(CancellationToken ct = default)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT substr(e.period_start,1,10),
              SUM(CASE WHEN ch.status IS NULL OR ch.status='missing_allocation' THEN 1 ELSE 0 END),
              SUM(CASE WHEN ch.status IS NOT NULL AND ch.status!='missing_allocation' THEN 1 ELSE 0 END),
              SUM(e.quantity_kwh),
              NOT EXISTS(SELECT 1 FROM octopus_effective_readings other
                WHERE other.flow_type=0 AND other.cost_gbp IS NULL
                  AND substr(other.period_start,1,10)=substr(e.period_start,1,10)
                  AND NOT EXISTS(SELECT 1 FROM octopus_four_rate_readings f2 WHERE f2.id=other.id))
            FROM octopus_effective_readings e JOIN octopus_four_rate_readings f ON f.id=e.id
            LEFT JOIN octopus_current_allocation_checks ch ON ch.raw_id=e.id
            WHERE e.cost_gbp IS NULL GROUP BY 1 ORDER BY 1
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct); var gaps = new List<SupplierAllocationGap>();
        while (await reader.ReadAsync(ct)) gaps.Add(new(DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetInt32(1), reader.GetInt32(2), reader.GetDecimal(3), reader.GetBoolean(4)));
        return gaps;
    }

    public async Task<IReadOnlyList<AllocationAudit>> AuditAsync(bool monthly, CancellationToken ct = default)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT substr(r.period_start,1,{(monthly ? 7 : 10)}),COUNT(*),SUM(CASE WHEN e.cost_gbp IS NOT NULL THEN 1 ELSE 0 END),SUM(r.quantity_kwh),SUM(COALESCE(CAST(ch.allocated_quantity AS REAL),0)),SUM(COALESCE(e.cost_gbp,0)) FROM octopus_raw_readings r JOIN octopus_four_rate_readings f ON f.id=r.id JOIN octopus_effective_readings e ON e.id=r.id LEFT JOIN octopus_current_allocation_checks ch ON ch.raw_id=r.id GROUP BY 1 ORDER BY 1";
        using var reader = await cmd.ExecuteReaderAsync(ct); var rows = new List<AllocationAudit>();
        while(await reader.ReadAsync(ct)) rows.Add(new(reader.GetString(0),reader.GetInt32(1),reader.GetInt32(2),reader.GetDecimal(3),reader.GetDecimal(4),reader.GetDecimal(5)));
        return rows;
    }

    public async Task<IReadOnlyList<SupplierEvidence>> ReadEvidenceAsync(CancellationToken ct = default)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT kind,retrieved_at,payload_json FROM octopus_supplier_evidence e WHERE e.id=(SELECT MAX(n.id) FROM octopus_supplier_evidence n WHERE n.account=e.account AND n.kind=e.kind) ORDER BY kind";
        using var reader=await cmd.ExecuteReaderAsync(ct); var result=new List<SupplierEvidence>();
        while(await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2)));
        return result;
    }

    public async Task<IReadOnlyList<SupplierMonthSummary>> SummaryAsync(CancellationToken ct = default)
    {
        var audits = await AuditAsync(true, ct); var result = new List<SupplierMonthSummary>();
        using var c = Open();
        foreach (var audit in audits)
        {
            using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT a.band,SUM(CAST(a.quantity_kwh AS REAL)),SUM(CAST(a.gross_pence AS REAL))/100.0 FROM octopus_accepted_allocations a JOIN octopus_raw_readings r ON r.id=a.raw_id WHERE substr(r.period_start,1,7)=@month GROUP BY a.band"; cmd.Parameters.AddWithValue("@month",audit.Period);
            using var reader=await cmd.ExecuteReaderAsync(ct); var bands=new List<SupplierBandTotal>();
            while(await reader.ReadAsync(ct)) bands.Add(new(reader.GetString(0),reader.GetDecimal(1),reader.GetDecimal(2)));
            result.Add(new(audit.Period,audit.Intervals,audit.Reconciled,audit.MeterKwh,audit.AllocatedKwh,audit.KnownGrossGbp,audit.Complete,bands));
        }
        return result;
    }
}

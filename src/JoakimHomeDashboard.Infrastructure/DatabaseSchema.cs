namespace JoakimHomeDashboard.Infrastructure;

public static class DatabaseSchema
{
    public const int Version = 12;
    public const string Sql = """
        PRAGMA foreign_keys = ON;
        CREATE TABLE IF NOT EXISTS schema_versions(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS energy_records(
          id INTEGER PRIMARY KEY AUTOINCREMENT, period_start TEXT NOT NULL, period_end TEXT NOT NULL,
          flow_type INTEGER NOT NULL, quantity_kwh NUMERIC NOT NULL, cost_gbp NUMERIC NOT NULL DEFAULT 0,
          source TEXT NOT NULL, standing_charge_gbp NUMERIC NOT NULL DEFAULT 0, tariff_code TEXT NOT NULL DEFAULT '', cost_available INTEGER NOT NULL DEFAULT 0, external_id TEXT NULL UNIQUE);
        CREATE INDEX IF NOT EXISTS ix_energy_period_type ON energy_records(period_start, flow_type);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_energy_external_id ON energy_records(external_id) WHERE external_id IS NOT NULL;
        CREATE TABLE IF NOT EXISTS settings(
          key TEXT PRIMARY KEY, value TEXT NOT NULL, is_secret INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS sync_runs(
          id INTEGER PRIMARY KEY AUTOINCREMENT, source TEXT NOT NULL, started_at TEXT NOT NULL,
          completed_at TEXT NULL, succeeded INTEGER NULL, records_imported INTEGER NOT NULL DEFAULT 0, message TEXT NULL, range_from TEXT NULL, range_to TEXT NULL);
        CREATE TABLE IF NOT EXISTS octopus_meter_points(
          id INTEGER PRIMARY KEY AUTOINCREMENT, account_number TEXT NOT NULL, property_id INTEGER NOT NULL,
          fuel_type TEXT NOT NULL, is_export INTEGER NOT NULL DEFAULT 0, meter_point TEXT NOT NULL, meter_serial TEXT NOT NULL,
          tariff_code TEXT NOT NULL DEFAULT '', product_code TEXT NOT NULL DEFAULT '', valid_from TEXT NULL, valid_to TEXT NULL,
          discovered_at TEXT NOT NULL, UNIQUE(account_number,property_id,fuel_type,is_export,meter_point,meter_serial,tariff_code));
        CREATE TABLE IF NOT EXISTS octopus_raw_readings(
          id INTEGER PRIMARY KEY AUTOINCREMENT, period_start TEXT NOT NULL, period_end TEXT NOT NULL, flow_type INTEGER NOT NULL,
          quantity_kwh NUMERIC NOT NULL, cost_gbp NUMERIC NULL, meter_point TEXT NOT NULL, meter_serial TEXT NOT NULL,
          tariff_code TEXT NOT NULL DEFAULT '', external_id TEXT NOT NULL UNIQUE, rate_band INTEGER NOT NULL DEFAULT 0, unit_rate_pence NUMERIC NULL, rate_band_version INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS ix_octopus_raw_meter_period ON octopus_raw_readings(meter_point,meter_serial,flow_type,period_start);
        CREATE INDEX IF NOT EXISTS ix_octopus_raw_meter_instant ON octopus_raw_readings(meter_point,meter_serial,flow_type,julianday(period_start));
        CREATE TABLE IF NOT EXISTS octopus_standing_charges(
          date TEXT NOT NULL, meter_point TEXT NOT NULL, tariff_code TEXT NOT NULL DEFAULT '', cost_gbp NUMERIC NOT NULL,
          PRIMARY KEY(date,meter_point,tariff_code));
        CREATE TABLE IF NOT EXISTS energy_monthly_rollups(
          period_start TEXT NOT NULL, flow_type INTEGER NOT NULL, quantity_kwh NUMERIC NOT NULL, cost_gbp NUMERIC NOT NULL DEFAULT 0,
          cost_available INTEGER NOT NULL DEFAULT 0, source TEXT NOT NULL, PRIMARY KEY(period_start,flow_type,source));
        CREATE TABLE IF NOT EXISTS home_events(
          id INTEGER PRIMARY KEY AUTOINCREMENT, event_date TEXT NOT NULL, label TEXT NOT NULL, category TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_home_events_date ON home_events(event_date);
        CREATE TABLE IF NOT EXISTS octopus_tariff_periods(
          id INTEGER PRIMARY KEY AUTOINCREMENT, account_number TEXT NOT NULL, property_id INTEGER NOT NULL, fuel_type TEXT NOT NULL,
          is_export INTEGER NOT NULL DEFAULT 0, meter_point TEXT NOT NULL, tariff_code TEXT NOT NULL, product_code TEXT NOT NULL,
          valid_from TEXT NULL, valid_to TEXT NULL, discovered_at TEXT NOT NULL,
          UNIQUE(account_number,property_id,fuel_type,is_export,meter_point,tariff_code,valid_from));
        CREATE TABLE IF NOT EXISTS solar_analysis_configuration(
          id INTEGER PRIMARY KEY CHECK(id=1), detected_start_date TEXT NULL, detection_confidence INTEGER NOT NULL DEFAULT 0,
          detection_method TEXT NOT NULL DEFAULT 'No sustained export detected', manual_override_date TEXT NULL, detected_at TEXT NULL);
        INSERT OR IGNORE INTO solar_analysis_configuration(id) VALUES(1);
        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES(1, datetime('now'));
        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES(2, datetime('now'));
        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES(3, datetime('now'));
        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES(4, datetime('now'));
        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES(5, datetime('now'));
        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES(6, datetime('now'));
        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES(7, datetime('now'));
        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES(8, datetime('now'));
        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES(9, datetime('now'));
        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES(10, datetime('now'));
        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES(11, datetime('now'));
        """;
}

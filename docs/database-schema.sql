-- Joakim Energy Dashboard schema v10. The executable source of truth is DatabaseSchema.cs.
PRAGMA foreign_keys = ON;
CREATE TABLE accounts(id INTEGER PRIMARY KEY, name TEXT NOT NULL, account_type INTEGER NOT NULL, provider TEXT NOT NULL, currency TEXT NOT NULL DEFAULT 'GBP', balance NUMERIC NOT NULL DEFAULT 0, is_active INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL);
CREATE TABLE account_transactions(id INTEGER PRIMARY KEY, account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE, occurred_on TEXT NOT NULL, amount NUMERIC NOT NULL, category TEXT NOT NULL, description TEXT NOT NULL);
CREATE TABLE energy_records(id INTEGER PRIMARY KEY, period_start TEXT NOT NULL, period_end TEXT NOT NULL, flow_type INTEGER NOT NULL, quantity_kwh NUMERIC NOT NULL, cost_gbp NUMERIC NOT NULL DEFAULT 0, source TEXT NOT NULL, standing_charge_gbp NUMERIC NOT NULL DEFAULT 0, tariff_code TEXT NOT NULL DEFAULT '', cost_available INTEGER NOT NULL DEFAULT 0, external_id TEXT UNIQUE);
CREATE UNIQUE INDEX ux_energy_external_id ON energy_records(external_id) WHERE external_id IS NOT NULL;
CREATE TABLE investment_positions(id INTEGER PRIMARY KEY, account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE, symbol TEXT NOT NULL, name TEXT NOT NULL, quantity NUMERIC NOT NULL, unit_price NUMERIC NOT NULL, cost_basis NUMERIC NOT NULL, as_of_date TEXT NOT NULL);
CREATE TABLE investment_contributions(id INTEGER PRIMARY KEY, account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE, contributed_on TEXT NOT NULL, amount NUMERIC NOT NULL);
CREATE TABLE pension_values(id INTEGER PRIMARY KEY, account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE, as_of_date TEXT NOT NULL, value NUMERIC NOT NULL, employee_contribution NUMERIC NOT NULL, employer_contribution NUMERIC NOT NULL);
CREATE TABLE rsu_grants(id INTEGER PRIMARY KEY, company TEXT NOT NULL, grant_date TEXT NOT NULL, total_shares INTEGER NOT NULL, tax_rate NUMERIC NOT NULL, currency TEXT NOT NULL DEFAULT 'GBP');
CREATE TABLE rsu_vesting_events(id INTEGER PRIMARY KEY, grant_id INTEGER NOT NULL REFERENCES rsu_grants(id) ON DELETE CASCADE, vest_date TEXT NOT NULL, shares INTEGER NOT NULL, share_price NUMERIC NOT NULL);
CREATE TABLE property_values(id INTEGER PRIMARY KEY, name TEXT NOT NULL, as_of_date TEXT NOT NULL, value NUMERIC NOT NULL, mortgage_balance NUMERIC NOT NULL);
CREATE TABLE net_worth_snapshots(id INTEGER PRIMARY KEY, as_of_date TEXT NOT NULL UNIQUE, assets NUMERIC NOT NULL, liabilities NUMERIC NOT NULL);
CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL, is_secret INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL);
CREATE TABLE sync_runs(id INTEGER PRIMARY KEY, source TEXT NOT NULL, started_at TEXT NOT NULL, completed_at TEXT, succeeded INTEGER, records_imported INTEGER NOT NULL DEFAULT 0, message TEXT, range_from TEXT, range_to TEXT);
CREATE TABLE octopus_meter_points(id INTEGER PRIMARY KEY, account_number TEXT NOT NULL, property_id INTEGER NOT NULL, fuel_type TEXT NOT NULL, is_export INTEGER NOT NULL DEFAULT 0, meter_point TEXT NOT NULL, meter_serial TEXT NOT NULL, tariff_code TEXT NOT NULL DEFAULT '', product_code TEXT NOT NULL DEFAULT '', valid_from TEXT, valid_to TEXT, discovered_at TEXT NOT NULL, UNIQUE(account_number,property_id,fuel_type,is_export,meter_point,meter_serial,tariff_code));
CREATE TABLE octopus_raw_readings(id INTEGER PRIMARY KEY, period_start TEXT NOT NULL, period_end TEXT NOT NULL, flow_type INTEGER NOT NULL, quantity_kwh NUMERIC NOT NULL, cost_gbp NUMERIC, meter_point TEXT NOT NULL, meter_serial TEXT NOT NULL, tariff_code TEXT NOT NULL DEFAULT '', external_id TEXT NOT NULL UNIQUE, rate_band INTEGER NOT NULL DEFAULT 0, unit_rate_pence NUMERIC, rate_band_version INTEGER NOT NULL DEFAULT 0);
CREATE INDEX ix_octopus_raw_meter_instant ON octopus_raw_readings(meter_point,meter_serial,flow_type,julianday(period_start));
CREATE TABLE octopus_standing_charges(date TEXT NOT NULL, meter_point TEXT NOT NULL, tariff_code TEXT NOT NULL DEFAULT '', cost_gbp NUMERIC NOT NULL, PRIMARY KEY(date,meter_point,tariff_code));
CREATE TABLE energy_monthly_rollups(period_start TEXT NOT NULL, flow_type INTEGER NOT NULL, quantity_kwh NUMERIC NOT NULL, cost_gbp NUMERIC NOT NULL DEFAULT 0, cost_available INTEGER NOT NULL DEFAULT 0, source TEXT NOT NULL, PRIMARY KEY(period_start,flow_type,source));
CREATE TABLE home_events(id INTEGER PRIMARY KEY, event_date TEXT NOT NULL, label TEXT NOT NULL, category TEXT NOT NULL, created_at TEXT NOT NULL);
CREATE TABLE octopus_tariff_periods(id INTEGER PRIMARY KEY, account_number TEXT NOT NULL, property_id INTEGER NOT NULL, fuel_type TEXT NOT NULL, is_export INTEGER NOT NULL DEFAULT 0, meter_point TEXT NOT NULL, tariff_code TEXT NOT NULL, product_code TEXT NOT NULL, valid_from TEXT, valid_to TEXT, discovered_at TEXT NOT NULL, UNIQUE(account_number,property_id,fuel_type,is_export,meter_point,tariff_code,valid_from));
CREATE TABLE solar_analysis_configuration(id INTEGER PRIMARY KEY CHECK(id=1), detected_start_date TEXT, detection_confidence INTEGER NOT NULL DEFAULT 0, detection_method TEXT NOT NULL DEFAULT 'No sustained export detected', manual_override_date TEXT, detected_at TEXT);
CREATE TABLE energy_project_foundations(id INTEGER PRIMARY KEY, name TEXT NOT NULL, category TEXT NOT NULL, project_cost NUMERIC, start_date TEXT NOT NULL, accumulated_benefit NUMERIC NOT NULL DEFAULT 0, source_event_id INTEGER REFERENCES home_events(id) ON DELETE SET NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);


-- Schema v11: separate supplier allocations (raw readings are immutable).

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
            CASE WHEN f.id IS NOT NULL THEN a.gross_pence/100.0 ELSE r.cost_gbp END AS cost_gbp,
            r.meter_point,r.meter_serial,r.tariff_code,r.external_id,
            CASE WHEN f.id IS NOT NULL THEN 0 ELSE r.rate_band END AS rate_band,
            CASE WHEN f.id IS NOT NULL THEN NULL ELSE r.unit_rate_pence END AS unit_rate_pence,r.rate_band_version
          FROM octopus_raw_readings r LEFT JOIN octopus_four_rate_readings f ON f.id=r.id
          LEFT JOIN (SELECT raw_id,SUM(CAST(gross_pence AS REAL)) gross_pence FROM octopus_accepted_allocations GROUP BY raw_id) a ON a.raw_id=r.id;
        CREATE VIEW IF NOT EXISTS octopus_import_band_readings AS
          SELECT r.period_start,r.flow_type,r.quantity_kwh,r.cost_gbp,r.meter_point,r.meter_serial,r.rate_band
          FROM octopus_effective_readings r WHERE r.flow_type=0 AND (NOT EXISTS(SELECT 1 FROM octopus_four_rate_readings f WHERE f.id=r.id) OR r.cost_gbp IS NULL)
          UNION ALL
          SELECT r.period_start,0,CAST(a.quantity_kwh AS REAL),CAST(a.gross_pence AS REAL)/100.0,r.meter_point,r.meter_serial,
            CASE WHEN a.band IN ('ECO7_DAY','EV_DEVICE_PEAK') THEN 1 ELSE 2 END
          FROM octopus_accepted_allocations a JOIN octopus_raw_readings r ON r.id=a.raw_id
          JOIN octopus_four_rate_readings f ON f.id=r.id;
        

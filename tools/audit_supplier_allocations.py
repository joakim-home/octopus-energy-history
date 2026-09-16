"""Read-only audit: python tools/audit_supplier_allocations.py DATABASE BASELINE_SHA256."""
import hashlib
import json
import pathlib
import sqlite3
import sys
from collections import defaultdict
from decimal import Decimal

db = pathlib.Path(sys.argv[1]).resolve()
connection = sqlite3.connect(db.as_uri() + '?mode=ro', uri=True)
raw = connection.execute('SELECT * FROM octopus_raw_readings ORDER BY id').fetchall()
checksum = hashlib.sha256(json.dumps(raw, separators=(',', ':')).encode()).hexdigest()
baseline = pathlib.Path(sys.argv[2]).read_text().strip()
assert checksum == baseline, 'RAW READING CHECKSUM CHANGED'
totals = {10: defaultdict(lambda: [0, Decimal(0), Decimal(0), Decimal(0)]),
          7: defaultdict(lambda: [0, Decimal(0), Decimal(0), Decimal(0)])}
rows = connection.execute('''SELECT r.id,r.period_start,r.quantity_kwh,e.cost_gbp,e.rate_band,e.unit_rate_pence
  FROM octopus_raw_readings r JOIN octopus_four_rate_readings f USING(id)
  JOIN octopus_effective_readings e USING(id) ORDER BY julianday(r.period_start)''').fetchall()
max_delta = Decimal(0)
for raw_id, start, quantity, effective_cost, band, rate in rows:
    allocations = connection.execute('SELECT quantity_kwh,gross_pence FROM octopus_accepted_allocations WHERE raw_id=?', (raw_id,)).fetchall()
    assert allocations and effective_cost is not None, f'Missing allocation at {start}'
    assert band == 0 and rate is None, f'Legacy pricing leak at {start}'
    meter = Decimal(str(quantity))
    allocated = sum(Decimal(a[0]) for a in allocations)
    cost = sum(Decimal(a[1]) for a in allocations) / 100
    delta = abs(meter - allocated)
    max_delta = max(max_delta, delta)
    assert delta <= Decimal('.000001'), f'Quantity mismatch at {start}'
    assert abs(cost - Decimal(str(effective_cost))) < Decimal('.00000001'), f'Cost mismatch at {start}'
    for length, periods in totals.items():
        entry = periods[start[:length]]
        entry[0] += 1
        entry[1] += meter
        entry[2] += allocated
        entry[3] += cost
for periods in totals.values():
    for period, (_, meter, allocated, _) in periods.items():
        assert abs(meter - allocated) <= Decimal('.000001'), f'Aggregate mismatch at {period}'
legacy_differences = connection.execute('''SELECT COUNT(*) FROM octopus_raw_readings r
  JOIN octopus_effective_readings e USING(id)
  WHERE NOT EXISTS(SELECT 1 FROM octopus_four_rate_readings f WHERE f.id=r.id)
  AND (r.cost_gbp IS NOT e.cost_gbp OR r.rate_band IS NOT e.rate_band OR r.unit_rate_pence IS NOT e.unit_rate_pence)''').fetchone()[0]
assert legacy_differences == 0, 'Legacy/export/gas projection changed'
report = dict(raw_rows=len(raw), baseline_sha256=baseline, after_sha256=checksum,
              intervals=len(rows), max_half_hour_difference_kwh=str(max_delta),
              legacy_projection_differences=legacy_differences,
              daily=totals[10], monthly=totals[7],
              columns=['intervals', 'meter_kwh', 'allocated_kwh', 'estimated_energy_gbp_incl_vat'])
print(json.dumps(report, default=str, indent=2))

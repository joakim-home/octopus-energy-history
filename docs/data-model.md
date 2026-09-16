# Data model

The Web project presents shared Application/Domain models; Infrastructure owns SQLite storage and Octopus adapters. Schema initialization is implemented in `DatabaseSchema.cs`, `SqliteDashboardRepository.cs` and `SupplierAllocationStore.cs`.

## Consumption and agreements

`octopus_raw_readings` stores provider intervals, flow, quantity, meter identity and preserved legacy pricing fields. Normal sync inserts missing intervals without rewriting existing consumption. Tariff classification is independent from quantity.

Discovered tariff agreements retain product/tariff codes and validity bounds. Metadata determines supported pricing endpoints and semantics. Agreement coverage is refreshed when stale; a non-empty local tariff table does not prove current coverage.

## Four-rate supplier records

| Store | Purpose |
| --- | --- |
| `octopus_supplier_tariffs` | Four-rate agreement bounds and product metadata |
| `octopus_allocation_revisions` | Query bounds, retrieval time, response hash, source payload and parser revision |
| `octopus_supplier_allocations` | Interval/band quantity, applied rate, net/gross cost, tax basis and provenance |
| `octopus_allocation_checks` | Meter-versus-allocation quantity and acceptance status per revision |
| `octopus_supplier_evidence` | Supporting account, rate, session and billing responses |

Bands are `ECO7_DAY`, `ECO7_NIGHT`, `EV_DEVICE_PEAK` and `EV_DEVICE_OFF_PEAK`. A zero-quantity band need not appear in every supplier interval. Quantities must sum to the stored half-hour consumption; missing, duplicate, extra or discrepant data is rejected or flagged. No EV quantity is reconstructed from power estimates or meter spikes.

Timestamps are compared as instants. Agreement/query ranges use start-inclusive/end-exclusive interval boundaries. Supplier queries are bounded by stored consumption, grouped by local date, and sent as UTC timestamps. Use UK host timezone settings.

The current reconciliation selects the latest revision for each raw interval. An unusable newer revision does not silently restore older accepted pricing. Repeated identical responses are idempotent.

## Dashboard projection

`octopus_effective_readings` uses accepted supplier costs for four-rate readings; unresolved rows expose no price. Other tariffs retain their legacy path. `octopus_import_band_readings` projects accepted supplier bands into the peak/off-peak analysis. Daily/monthly rollups can be rebuilt from these projections without modifying raw consumption.

Supplier applied rates are checked against tax-exclusive and tax-inclusive rate evidence. Tax basis is stored explicitly. These interval costs are labelled supplier-calculated estimates; issued bill evidence remains separate. Energy cost totals exclude standing charges unless explicitly stated otherwise.

Manual and scheduled sync use a common coordinator. Incremental allocation refresh retries old gaps and revisits complete local days in the latest three-day overlap. A later accepted allocation removes current pending warnings automatically; independent missing gas/export pricing remains visible.

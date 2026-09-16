# Troubleshooting

## Supplier allocation pending

Consumption exists, but the supplier has not supplied an accepted allocation for those intervals. Use **Sync now** or wait for the next daily sync. Missing intervals are retried even when they fall outside the revision overlap. Costs remain unavailable until reconciliation succeeds; do not substitute legacy prices or estimated charging quantities.

## Allocation unavailable or discrepant

The supplier response could not be accepted, for example because its quantities differ from the meter interval, its units are unsupported, or rate/tax evidence is missing. Retain local evidence and inspect the allocation audit. Report sanitized status and counts, not raw supplier JSON.

## Import reconciles, but combined utility is unavailable

Check gas and export separately. Their missing tariff coverage is independent of import allocation. A recovered import total does not prove all utility costs are available.

## Missing readings

Octopus readings can arrive later than the measurement period. Recent completeness checks allow reporting grace. Sync retries missing consumption through its normal import path. The app does not estimate missing readings.

## Saved credential cannot be opened

Confirm the database is paired with the same `OCTOPUS_SECRET_KEY_PATH` file used when saving the key. Restore the matching protected backup or enter the credential again through Setup. Never paste the key into an issue.

## Wrong dates or shifted day totals

On Linux use `TZ=Europe/London`; verify host clock and timezone. Provide interval start/end offsets in a synthetic reproduction. Do not rewrite raw timestamps to hide a boundary problem.

## Before an upgrade or investigation

Stop the app and make a recoverable backup of the database and matching key, or use a proper SQLite backup procedure. Work on a separate copy. Do not upload database files or run repair SQL from an unrelated incident. Restart with the original storage paths only after validation.

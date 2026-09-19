# Documentation

Public documentation shipped with Octopus Energy Dashboard:

## Dashboard preview

![Octopus Energy Dashboard overview](images/octopus-dashboard-overview.png)

- [INSTALL.md](INSTALL.md) — install, update, reverse proxy, backup and uninstall.
- [SECURITY.md](SECURITY.md) — deployment boundary, credential storage and safe exposure.
- [four-rate-ev-readonly.graphql](four-rate-ev-readonly.graphql) — sanitized read-only supplier allocation query reference.

The executable database schema is defined in:

- `src/JoakimHomeDashboard.Infrastructure/DatabaseSchema.cs`
- `src/JoakimHomeDashboard.Infrastructure/SupplierAllocationStore.cs`

Operational incident notes, populated databases, supplier responses, API keys,
account identifiers and machine-specific deployment files are intentionally not part of the
public repository. Only deliberately selected, publication-safe screenshots are included.
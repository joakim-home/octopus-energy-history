# Octopus Energy History

A self-hosted web dashboard for Octopus electricity import, export and gas history. It keeps meter consumption separate from supplier pricing and makes incomplete coverage visible.

This is an independent community project, not an official Octopus Energy application. It is not a billing system. Supplier interval estimates can differ from final issued bills.

## Features

- Account, meter and agreement discovery using your Octopus API credential.
- Daily and monthly history, peak/off-peak analysis and year comparisons.
- Four-rate EV home day/night and EV peak/off-peak allocations from authenticated supplier data.
- Reconciliation against stored consumption; unavailable allocations never use legacy pricing as a fallback.
- Separate supplier evidence, revisions and issued bill charges.
- Manual and daily scheduled sync share the same ingestion path. Allocation sync retries gaps and revisits a three-day overlap for revisions.

## Run locally

Install the .NET 9 SDK selected by `global.json`, then run from the repository root:

```sh
dotnet restore --configfile NuGet.Config
dotnet test tests/JoakimHomeDashboard.Tests -c Release
dotnet run --project src/JoakimHomeDashboard.Web -c Release --no-launch-profile --urls http://127.0.0.1:5086
```

Open `http://127.0.0.1:5086`, choose **Octopus Setup**, save your API key, discover your account, and sync. The initial import can take longer than later updates. No real account data or credentials are supplied with the project.

The supported entry point is the Web project. Shared libraries retain their existing namespace; unused finance and provider types are not a supported product surface. This distribution does not include the desktop application.

## Storage and hosting

| Environment variable | Purpose |
| --- | --- |
| `OCTOPUS_DATA_PATH` | SQLite database; default `data/dashboard.db` under the web content root |
| `OCTOPUS_SECRET_KEY_PATH` | Host encryption key; default `data/secret.key` under the web content root |
| `OCTOPUS_DISABLE_SYNC` | Set to `1` to disable background sync |
| `TZ` | Set to `Europe/London` on Linux for UK calendar grouping |

Use persistent paths outside the published application folder. On Linux, run the app under a dedicated service account and restrict the database/key directory to that account. Back up both the database and its matching key securely. The database contains private readings, account identifiers and supplier evidence. Only saved credential values are encrypted; the database itself is not encrypted, so use appropriate host disk/volume encryption when protection against device loss or offline access is required.

The app has no built-in authentication. Keep it on loopback or a trusted private network. If it is reachable from an untrusted network, put it behind an authenticated reverse proxy with TLS; TLS by itself is not access control. See [security](SECURITY.md).

The files under `deploy/` are deployment-time templates. Replace placeholders such as `OCTOPUS_HOST_IP`, `NETWORK_INTERFACE` and the example hostname before installing them. `deploy/octopus.service` intentionally keeps ASP.NET Core on `127.0.0.1:8080`; expose it through the chosen reverse proxy rather than binding the application directly to an untrusted interface. `OCTOPUS_SECRET_KEY_PATH` contains the path to the host key file, not the key material itself.

For a Linux x64 self-contained build:

```sh
dotnet publish src/JoakimHomeDashboard.Web -c Release -r linux-x64 --self-contained true -o dist/web
```

Run the published executable from its output folder with persistent storage variables set. Do not place databases or keys under `wwwroot`.

## Allocation backfill

After account discovery and consumption sync, stop the running app and use the same storage variables:

```sh
dotnet run --project src/JoakimHomeDashboard.Web -c Release --no-launch-profile -- --backfill-allocations
```

This imports supplier allocations for stored four-rate consumption. It does not regenerate meter readings. Missing supplier data remains unavailable and produces a nonzero exit code; repeat sync can resolve it when the supplier publishes it.

## Limitations and support

Supplier APIs and publication timing can change. Four-rate account scope must be unambiguous. Unsupported tariffs, absent rates, rejected allocations and missing gas/export pricing remain visible. Energy costs and standing charges are separate; interval estimates are not final bill charges.

Read [the data model](docs/data-model.md) and [troubleshooting](docs/troubleshooting.md). For a bug report, include software version, operating system, sanitized error text and synthetic reproduction steps. Never upload your database, key or unredacted supplier response. Community support has no response-time guarantee.

This project was developed with AI assistance. Contributors and maintainers remain responsible for reviewing changes and test evidence. See [contributing](CONTRIBUTING.md).

Original project code is licensed under [MIT](LICENSE). Dependencies retain their own [licenses](THIRD_PARTY_NOTICES.md).



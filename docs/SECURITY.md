# Security

## Deployment boundary

Octopus Energy Dashboard is a self-hosted application intended to run behind a trusted network boundary.

The application includes one local administrator account by default. Dashboard and configuration pages require authentication; `/health` is intentionally anonymous for service monitoring.

For a deliberately trusted-LAN-only deployment, authentication can be disabled with `OCTOPUS_AUTH_DISABLED=1`. This is an explicit opt-out and should not be used for an internet-facing or otherwise untrusted network deployment.

The provided installer binds the application to `127.0.0.1`. Keep that default and place nginx, Caddy, a VPN, or another trusted access layer in front of it for remote access.

## Administrator authentication

The first browser visit creates the local administrator. Passwords must be at least 12 characters and are stored using ASP.NET Core's salted password hasher, never as plaintext or reversible ciphertext.

Login attempts are limited to 10 per minute. Authentication uses an HTTP-only cookie with sliding expiration. Use HTTPS at the reverse proxy when traffic leaves the local host.

If the administrator password is lost, use the documented local-shell `--reset-admin` recovery command. Resetting the administrator revokes existing login sessions but does not erase energy history or Octopus credentials.

## Supplier credentials

The Octopus API key is stored in SQLite as AES-GCM ciphertext.

A random 32-byte encryption key is stored separately at the path configured by `OCTOPUS_SECRET_KEY_PATH`. On Unix the application creates that key with owner-only permissions where supported.

Do not commit a database, secret key, API key, account number, MPAN/MPRN, meter serial or populated environment file to source control.

## Backups

Treat the persistent data directory as one backup set. It contains the database, supplier-secret encryption key and ASP.NET data-protection keys. The database also contains the local administrator password hash.

The data-protection keys preserve authentication-cookie continuity across restarts and restores. If they are lost, existing sessions are invalidated but the administrator account remains usable.

Protect backups with filesystem/disk encryption and access controls appropriate for household billing data.

## Reverse proxy and embedding

TLS is recommended anywhere traffic leaves a trusted host. Built-in authentication does not replace transport security.

Home Assistant iframe embedding is deliberately not enabled by the generic nginx template. If enabled, scope `frame-ancestors` to the exact trusted Home Assistant origin rather than using a wildcard. Cross-site iframe deployments may also require deliberate cookie SameSite/Secure configuration.

## Reporting issues

Do not include real API keys, account numbers, MPANs/MPRNs, meter serials, authentication cookies or database files in public bug reports.

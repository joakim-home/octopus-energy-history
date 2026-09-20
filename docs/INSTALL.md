# Installation

## Supported deployment

The recommended public deployment is a dedicated Linux VM/LXC or small server with:

- systemd
- outbound HTTPS access to Octopus Energy
- .NET 9 SDK during source installation
- nginx/Caddy/another reverse proxy if LAN access is required

The installed application is self-contained and binds to localhost by default.

## Install from source

```bash
git clone <your-repository-url>
cd <repository>
sudo ./deploy/install.sh
```

The installer creates a system account, publishes `linux-x64`, installs the application beneath `/opt`, creates persistent storage beneath `/var/lib`, installs the required ICU runtime on Debian/Ubuntu when needed, generates a systemd unit and starts it.

On first browser access, the application redirects to a one-time administrator creation page before Octopus configuration is available.

For a trusted-LAN-only installation where application login is intentionally not wanted, add `OCTOPUS_AUTH_DISABLED=1` to the service environment. Authentication remains enabled by default.

## Installer options

```bash
sudo ./deploy/install.sh --help

sudo ./deploy/install.sh \
  --port 8080 \
  --timezone Europe/London \
  --app-dir /opt/octopus-energy-dashboard \
  --data-dir /var/lib/octopus-energy-dashboard
```

For a pre-published build:

```bash
sudo ./deploy/install.sh --publish-dir /path/to/published/linux-x64
```

The publish directory must contain `OctopusEnergyDashboard.Web`.
## Reverse proxy

The application listens on `127.0.0.1:8080` by default. A basic nginx template is provided at:

```text
deploy/octopus.nginx.example
```

Copy it into your nginx configuration, choose an internal hostname, enable the site and reload nginx.

Home Assistant iframe embedding is deliberately not enabled in the generic template. If you need embedding, add a narrowly scoped `Content-Security-Policy: frame-ancestors` rule for the exact Home Assistant origin.

The dashboard has built-in authentication, but the reverse proxy should still provide HTTPS before the service is exposed beyond a trusted private network.

## Service management

```bash
sudo systemctl status octopus-energy-dashboard
sudo systemctl restart octopus-energy-dashboard
sudo journalctl -u octopus-energy-dashboard -f
```

A local health endpoint is available at:

```text
http://127.0.0.1:8080/health
```

## Updating

Pull the new source and rerun the same installer:

```bash
git pull
sudo ./deploy/install.sh
```

The application files are replaced. The database, administrator account and secret key in `/var/lib/octopus-energy-dashboard` are preserved.

## Administrator recovery

If the local administrator password is lost, reset the account from the host shell:

```bash
sudo systemctl stop octopus-energy-dashboard
sudo -u octopus-energy \
  OCTOPUS_DATA_PATH=/var/lib/octopus-energy-dashboard/dashboard.db \
  OCTOPUS_SECRET_KEY_PATH=/var/lib/octopus-energy-dashboard/secret.key \
  /opt/octopus-energy-dashboard/OctopusEnergyDashboard.Web --reset-admin
sudo systemctl start octopus-energy-dashboard
```

The next browser visit will show the first-run administrator creation screen again. Existing login sessions are revoked. This command does not delete energy history or Octopus credentials.

## Backup

Back up the persistent data directory, including:

```text
/var/lib/octopus-energy-dashboard/dashboard.db
/var/lib/octopus-energy-dashboard/secret.key
/var/lib/octopus-energy-dashboard/data-protection-keys/
```

The database contains encrypted credentials and the administrator password hash. The secret key is required to decrypt supplier credentials. The data-protection keys keep existing authentication cookies valid across restarts and restores. Losing them does not lose the administrator account, but existing browser sessions will be signed out.

SQLite may also have `-wal` or `-shm` files while the service is running. For the simplest consistent file-level backup, stop the service briefly before copying the database.

## Uninstall

Preserve data:

```bash
sudo ./deploy/uninstall.sh
```

Delete the application **and** persistent database/key:

```bash
sudo ./deploy/uninstall.sh --purge-data
```

The purge option is intentionally explicit and irreversible.

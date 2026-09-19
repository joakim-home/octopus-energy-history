#!/usr/bin/env bash
set -euo pipefail

APP_NAME="octopus-energy-dashboard"
SERVICE_NAME="octopus-energy-dashboard"
APP_USER="octopus-energy"
APP_DIR="/opt/octopus-energy-dashboard"
DATA_DIR="/var/lib/octopus-energy-dashboard"
ENV_FILE="/etc/octopus-energy-dashboard.env"
PORT="8080"
TIMEZONE="Europe/London"
PUBLISH_DIR=""

usage() {
  cat <<'EOF'
Usage: sudo ./deploy/install.sh [options]

Installs or updates Octopus Energy Dashboard as a systemd web service.

Options:
  --app-dir PATH       Application directory
  --data-dir PATH      Persistent database/key directory
  --user NAME          Service account
  --port PORT          Local HTTP port (default: 8080)
  --timezone ZONE      Process timezone (default: Europe/London)
  --publish-dir PATH   Install an already-published application
  -h, --help           Show this help

The web app binds to 127.0.0.1 only. Put nginx, Caddy, or another trusted
reverse proxy in front of it if you want LAN access.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --app-dir) APP_DIR="$2"; shift 2 ;;
    --data-dir) DATA_DIR="$2"; shift 2 ;;
    --user) APP_USER="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --timezone) TIMEZONE="$2"; shift 2 ;;
    --publish-dir) PUBLISH_DIR="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo "Run this installer as root, for example: sudo ./deploy/install.sh" >&2
  exit 1
fi

if ! [[ "$PORT" =~ ^[0-9]+$ ]] || (( PORT < 1 || PORT > 65535 )); then
  echo "Invalid --port: $PORT" >&2
  exit 2
fi
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

if ! ldconfig -p 2>/dev/null | grep -q 'libicuuc'; then
  if command -v apt-get >/dev/null 2>&1; then
    echo "Installing ICU runtime dependency..."
    apt-get update -qq
    ICU_PACKAGE="$(apt-cache search '^libicu[0-9][0-9]*$' | awk 'NR==1 {print $1}')"
    if [[ -n "$ICU_PACKAGE" ]]; then
      DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "$ICU_PACKAGE"
    else
      DEBIAN_FRONTEND=noninteractive apt-get install -y -qq libicu-dev
    fi
  else
    echo "ICU runtime library is required. Install your distribution's libicu package and rerun." >&2
    exit 1
  fi
fi

if [[ -z "$PUBLISH_DIR" ]]; then
  if ! command -v dotnet >/dev/null 2>&1; then
    echo ".NET 9 SDK is required to build from source." >&2
    echo "Install the SDK, or pass --publish-dir with a pre-published linux-x64 build." >&2
    exit 1
  fi
  if ! dotnet --list-sdks | grep -q '^9\.'; then
    echo ".NET 9 SDK was not found." >&2
    exit 1
  fi
  echo "Publishing self-contained linux-x64 application..."
  dotnet publish "$REPO_ROOT/src/JoakimHomeDashboard.Web/JoakimHomeDashboard.Web.csproj" \
    -c Release -r linux-x64 --self-contained true -o "$TMP_DIR/publish" --nologo
  PUBLISH_DIR="$TMP_DIR/publish"
fi

if [[ ! -f "$PUBLISH_DIR/JoakimHomeDashboard.Web" ]]; then
  echo "Published application not found in: $PUBLISH_DIR" >&2
  exit 1
fi
if ! id "$APP_USER" >/dev/null 2>&1; then
  useradd --system --home-dir "$DATA_DIR" --create-home --shell /usr/sbin/nologin "$APP_USER"
fi
APP_GROUP="$(id -gn "$APP_USER")"

install -d -m 0755 "$APP_DIR"
install -d -o "$APP_USER" -g "$APP_GROUP" -m 0750 "$DATA_DIR"
chown -R "$APP_USER:$APP_GROUP" "$DATA_DIR"

if systemctl list-unit-files "$SERVICE_NAME.service" >/dev/null 2>&1; then
  systemctl stop "$SERVICE_NAME.service" || true
fi

rm -rf "$APP_DIR"/*
cp -a "$PUBLISH_DIR"/. "$APP_DIR"/
chown -R root:root "$APP_DIR"
chmod 0755 "$APP_DIR/JoakimHomeDashboard.Web"

cat >"$ENV_FILE" <<EOF
ASPNETCORE_URLS=http://127.0.0.1:$PORT
ASPNETCORE_ENVIRONMENT=Production
TZ=$TIMEZONE
OCTOPUS_DATA_PATH=$DATA_DIR/dashboard.db
OCTOPUS_SECRET_KEY_PATH=$DATA_DIR/secret.key
EOF
chmod 0644 "$ENV_FILE"
cat >"/etc/systemd/system/$SERVICE_NAME.service" <<EOF
[Unit]
Description=Octopus Energy Dashboard
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$APP_USER
Group=$APP_GROUP
WorkingDirectory=$APP_DIR
EnvironmentFile=$ENV_FILE
ExecStart=$APP_DIR/JoakimHomeDashboard.Web
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$DATA_DIR
RestrictSUIDSGID=true
LockPersonality=true

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now "$SERVICE_NAME.service"
for _ in {1..15}; do
  if systemctl is-active --quiet "$SERVICE_NAME.service"; then
    break
  fi
  sleep 1
done

if ! systemctl is-active --quiet "$SERVICE_NAME.service"; then
  systemctl --no-pager --full status "$SERVICE_NAME.service" || true
  exit 1
fi

if command -v curl >/dev/null 2>&1; then
  HEALTHY=0
  for _ in {1..15}; do
    if curl --fail --silent --show-error "http://127.0.0.1:$PORT/health" >/dev/null; then
      HEALTHY=1
      break
    fi
    sleep 1
  done
  if (( ! HEALTHY )); then
    echo "Service started but /health did not become ready." >&2
    systemctl --no-pager --full status "$SERVICE_NAME.service" || true
    exit 1
  fi
fi

echo
echo "Octopus Energy Dashboard is installed and running."
echo "Service:   $SERVICE_NAME"
echo "Local URL: http://127.0.0.1:$PORT"
echo "Data:      $DATA_DIR"
echo "Config:    $ENV_FILE"
echo
echo "Next: put a trusted reverse proxy in front of 127.0.0.1:$PORT for LAN access."
echo "Example: $REPO_ROOT/deploy/octopus.nginx.example"

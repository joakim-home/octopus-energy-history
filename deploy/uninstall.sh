#!/usr/bin/env bash
set -euo pipefail

SERVICE_NAME="octopus-energy-dashboard"
APP_DIR="/opt/octopus-energy-dashboard"
DATA_DIR="/var/lib/octopus-energy-dashboard"
ENV_FILE="/etc/octopus-energy-dashboard.env"
APP_USER="octopus-energy"
PURGE_DATA=0

if [[ "${1:-}" == "--purge-data" ]]; then
  PURGE_DATA=1
elif [[ $# -gt 0 ]]; then
  echo "Usage: sudo ./deploy/uninstall.sh [--purge-data]" >&2
  exit 2
fi

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo "Run as root." >&2
  exit 1
fi
systemctl disable --now "$SERVICE_NAME.service" 2>/dev/null || true
rm -f "/etc/systemd/system/$SERVICE_NAME.service"
systemctl daemon-reload

rm -rf "$APP_DIR"
rm -f "$ENV_FILE"

if (( PURGE_DATA )); then
  rm -rf "$DATA_DIR"
  if id "$APP_USER" >/dev/null 2>&1; then
    userdel "$APP_USER" 2>/dev/null || true
  fi
  echo "Application and persistent data removed."
else
  echo "Application removed. Persistent data retained at: $DATA_DIR"
  echo "Use --purge-data only if you intentionally want to delete the database and encryption key."
fi

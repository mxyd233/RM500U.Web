#!/bin/sh
set -eu

APP_NAME="rm500u-web"
SERVICE_NAME="rm500u-web.service"
INSTALL_DIR="/opt/rm500u-web"
DATA_DIR="/var/lib/rm500u-web"
ENV_FILE="/etc/rm500u-web.env"
UNIT_FILE="/etc/systemd/system/${SERVICE_NAME}"
PURGE=0

usage() {
    cat <<'EOF'
Usage: sudo ./deploy/uninstall.sh [--purge]

Without --purge, modem configuration and Basic Auth credentials are kept.
With --purge, /var/lib/rm500u-web and /etc/rm500u-web.env are also removed.
EOF
}

die() {
    printf 'Error: %s\n' "$*" >&2
    exit 1
}

case "${1:-}" in
    "")
        ;;
    --purge)
        PURGE=1
        ;;
    -h|--help)
        usage
        exit 0
        ;;
    *)
        usage >&2
        exit 2
        ;;
esac

if [ "$#" -gt 1 ]; then
    usage >&2
    exit 2
fi

if [ "$(id -u)" -ne 0 ]; then
    die "this uninstaller must run as root"
fi

if command -v systemctl >/dev/null 2>&1; then
    systemctl stop "$SERVICE_NAME" >/dev/null 2>&1 || true
    systemctl disable "$SERVICE_NAME" >/dev/null 2>&1 || true
fi

rm -f -- "$UNIT_FILE"

case "$INSTALL_DIR" in
    /opt/rm500u-web)
        rm -rf -- "$INSTALL_DIR"
        ;;
    *)
        die "internal path safety check failed"
        ;;
esac

if [ "$PURGE" -eq 1 ]; then
    case "$DATA_DIR" in
        /var/lib/rm500u-web)
            rm -rf -- "$DATA_DIR"
            ;;
        *)
            die "internal data path safety check failed"
            ;;
    esac
    rm -f -- "$ENV_FILE"
fi

if command -v systemctl >/dev/null 2>&1; then
    systemctl daemon-reload >/dev/null 2>&1 || true
    systemctl reset-failed "$SERVICE_NAME" >/dev/null 2>&1 || true
fi

printf '%s application and service removed.\n' "$APP_NAME"
if [ "$PURGE" -eq 0 ]; then
    printf 'Preserved data: %s\n' "$DATA_DIR"
    printf 'Preserved credentials: %s\n' "$ENV_FILE"
    printf 'Run again with --purge to remove them.\n'
else
    printf 'Application data and credentials were purged.\n'
fi

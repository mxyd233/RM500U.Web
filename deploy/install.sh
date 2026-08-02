#!/bin/sh
set -eu

umask 022

APP_NAME="rm500u-web"
SERVICE_NAME="rm500u-web.service"
INSTALL_DIR="/opt/rm500u-web"
DATA_DIR="/var/lib/rm500u-web"
ENV_FILE="/etc/rm500u-web.env"
UNIT_FILE="/etc/systemd/system/${SERVICE_NAME}"

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
PROJECT_DIR=$(CDPATH= cd -- "${SCRIPT_DIR}/.." && pwd -P)
SERVICE_SOURCE="${SCRIPT_DIR}/rm500u-web.service"

usage() {
    cat <<'EOF'
Usage: sudo ./deploy/install.sh [PUBLISH_DIRECTORY]

PUBLISH_DIRECTORY defaults to ./publish relative to the project root.
It must contain either RM500U.Web or RM500U.Web.dll.

Optional environment variables used only when /etc/rm500u-web.env does not
already exist:
  RM500U_WEB_USER      Basic Auth user (default: admin)
  RM500U_WEB_PASSWORD  Basic Auth password (default: generated securely)
EOF
}

die() {
    printf 'Error: %s\n' "$*" >&2
    exit 1
}

case "${1:-}" in
    -h|--help)
        usage
        exit 0
        ;;
esac

if [ "$#" -gt 1 ]; then
    usage >&2
    exit 2
fi

if [ "$(id -u)" -ne 0 ]; then
    die "this installer must run as root"
fi

SOURCE_INPUT=${1:-"${PROJECT_DIR}/publish"}
[ -d "$SOURCE_INPUT" ] || die "publish directory not found: ${SOURCE_INPUT}"
SOURCE_DIR=$(CDPATH= cd -- "$SOURCE_INPUT" && pwd -P)

case "$SOURCE_DIR" in
    "$INSTALL_DIR"|"$INSTALL_DIR"/*)
        die "the publish directory cannot be inside ${INSTALL_DIR}"
        ;;
esac

[ -f "$SERVICE_SOURCE" ] || die "service template not found: ${SERVICE_SOURCE}"

if [ ! -f "${SOURCE_DIR}/RM500U.Web" ] && [ ! -f "${SOURCE_DIR}/RM500U.Web.dll" ]; then
    die "${SOURCE_DIR} is not a published RM500U.Web application"
fi

case "$(uname -m)" in
    x86_64|amd64)
        PLATFORM="linux-x64"
        ;;
    aarch64|arm64)
        PLATFORM="linux-arm64"
        ;;
    *)
        die "unsupported CPU architecture: $(uname -m) (expected x86_64 or aarch64)"
        ;;
esac

# A framework-dependent publish contains the application DLL but not its own
# hostfxr. Verify the matching ASP.NET Core runtime before changing the system.
if [ -f "${SOURCE_DIR}/RM500U.Web.dll" ] && [ ! -f "${SOURCE_DIR}/libhostfxr.so" ]; then
    command -v dotnet >/dev/null 2>&1 ||
        die "framework-dependent publish detected; install ASP.NET Core Runtime 10 first"
    if ! dotnet --list-runtimes 2>/dev/null | grep -q '^Microsoft\.AspNetCore\.App 10\.'; then
        die "Microsoft.AspNetCore.App 10.x is not installed"
    fi
    PUBLISH_MODE="framework-dependent"
else
    PUBLISH_MODE="self-contained"
fi

command -v systemctl >/dev/null 2>&1 || die "systemd/systemctl is required"

for MANAGED_PATH in "$INSTALL_DIR" "$DATA_DIR" "$ENV_FILE" "$UNIT_FILE"; do
    if [ -L "$MANAGED_PATH" ]; then
        die "refusing to replace symlink: ${MANAGED_PATH}"
    fi
done

GENERATED_PASSWORD=0
if [ ! -e "$ENV_FILE" ]; then
    AUTH_USER=${RM500U_WEB_USER:-admin}
    if [ -n "${RM500U_WEB_PASSWORD:-}" ]; then
        AUTH_PASSWORD=$RM500U_WEB_PASSWORD
    else
        AUTH_PASSWORD=$(od -An -N18 -tx1 /dev/urandom | tr -d ' \n')
        GENERATED_PASSWORD=1
    fi

    NEWLINE='
'
    CARRIAGE_RETURN=$(printf '\r')
    case "$AUTH_USER$AUTH_PASSWORD" in
        *"$NEWLINE"*|*"$CARRIAGE_RETURN"*)
            die "Basic Auth values must be single-line strings"
            ;;
    esac

    escape_env_value() {
        printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
    }

    {
        printf '# RM500U.Web service overrides. Keep this file readable only by root.\n'
        printf 'RM500U_WEB_USER="%s"\n' "$(escape_env_value "$AUTH_USER")"
        printf 'RM500U_WEB_PASSWORD="%s"\n' "$(escape_env_value "$AUTH_PASSWORD")"
        printf '# ASPNETCORE_URLS="http://127.0.0.1:5080"\n'
        printf '# ASPNETCORE_FORWARDEDHEADERS_ENABLED="true"\n'
    } > "$ENV_FILE"
fi
chown root:root "$ENV_FILE"
chmod 0600 "$ENV_FILE"

STAGE_DIR="${INSTALL_DIR}.new.$$"
BACKUP_DIR="${INSTALL_DIR}.old"

cleanup() {
    if [ -d "$STAGE_DIR" ]; then
        rm -rf -- "$STAGE_DIR"
    fi
}
trap cleanup EXIT HUP INT TERM

install -d -m 0755 /opt
install -d -m 0700 "$DATA_DIR"
chown root:root "$DATA_DIR"

rm -rf -- "$STAGE_DIR"
install -d -m 0755 "$STAGE_DIR"
cp -a "${SOURCE_DIR}/." "$STAGE_DIR/"
chown -R root:root "$STAGE_DIR"

if [ -f "${STAGE_DIR}/RM500U.Web" ]; then
    chmod 0755 "${STAGE_DIR}/RM500U.Web"
fi

cat > "${STAGE_DIR}/rm500u-web-launcher" <<'EOF'
#!/bin/sh
set -eu

cd /opt/rm500u-web

if [ -x ./RM500U.Web ]; then
    exec ./RM500U.Web "$@"
fi

if [ -f ./RM500U.Web.dll ] && command -v dotnet >/dev/null 2>&1; then
    exec dotnet ./RM500U.Web.dll "$@"
fi

printf 'RM500U.Web executable or .NET 10 runtime is unavailable.\n' >&2
exit 127
EOF
chmod 0755 "${STAGE_DIR}/rm500u-web-launcher"

systemctl stop "$SERVICE_NAME" >/dev/null 2>&1 || true

rm -rf -- "$BACKUP_DIR"
HAD_OLD=0
if [ -e "$INSTALL_DIR" ]; then
    mv -- "$INSTALL_DIR" "$BACKUP_DIR"
    HAD_OLD=1
fi

if ! mv -- "$STAGE_DIR" "$INSTALL_DIR"; then
    if [ "$HAD_OLD" -eq 1 ] && [ -d "$BACKUP_DIR" ]; then
        mv -- "$BACKUP_DIR" "$INSTALL_DIR"
    fi
    die "unable to activate the new application directory"
fi

install -m 0644 "$SERVICE_SOURCE" "$UNIT_FILE"

systemctl daemon-reload
systemctl enable "$SERVICE_NAME" >/dev/null
if ! systemctl restart "$SERVICE_NAME"; then
    if [ "$HAD_OLD" -eq 1 ] && [ -d "$BACKUP_DIR" ]; then
        rm -rf -- "$INSTALL_DIR"
        mv -- "$BACKUP_DIR" "$INSTALL_DIR"
        systemctl restart "$SERVICE_NAME" >/dev/null 2>&1 || true
    fi
    die "unable to start ${SERVICE_NAME}"
fi
sleep 1

if ! systemctl is-active --quiet "$SERVICE_NAME"; then
    systemctl --no-pager --full status "$SERVICE_NAME" >&2 || true
    if [ "$HAD_OLD" -eq 1 ] && [ -d "$BACKUP_DIR" ]; then
        systemctl stop "$SERVICE_NAME" >/dev/null 2>&1 || true
        rm -rf -- "$INSTALL_DIR"
        mv -- "$BACKUP_DIR" "$INSTALL_DIR"
        systemctl restart "$SERVICE_NAME" >/dev/null 2>&1 || true
    fi
    die "${SERVICE_NAME} did not remain active"
fi

rm -rf -- "$BACKUP_DIR"

trap - EXIT HUP INT TERM

printf '%s installed for %s (%s).\n' "$APP_NAME" "$PLATFORM" "$PUBLISH_MODE"
printf 'Service: systemctl status %s\n' "$SERVICE_NAME"
printf 'Web UI:  http://<device-ip>:5080/\n'
printf 'Config:  %s\n' "$ENV_FILE"

if [ "$GENERATED_PASSWORD" -eq 1 ]; then
    printf '\nBasic Auth credentials generated for the first installation:\n'
    printf '  user:     %s\n' "$AUTH_USER"
    printf '  password: %s\n' "$AUTH_PASSWORD"
    printf 'Store the password now; later changes are made in %s.\n' "$ENV_FILE"
fi

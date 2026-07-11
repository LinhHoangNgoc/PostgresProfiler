#!/usr/bin/env bash
# =============================================================
#  PG Monitor - Cài đặt & cấu hình trên Linux (Ubuntu/Mint/Debian)
#  Chạy trong thư mục chứa file thực thi PgMonitorApi.
#  Cách dùng:  chmod +x setup.sh && ./setup.sh
# =============================================================
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$HERE"

echo "==================================================="
echo "        PG MONITOR - CẤU HÌNH TRÊN LINUX"
echo "==================================================="

# ---- 1) Nhập thông tin cấu hình ----
read -rp "PostgreSQL host [127.0.0.1]: " PGHOST;  PGHOST=${PGHOST:-127.0.0.1}
read -rp "PostgreSQL port [5432]: "      PGPORT;  PGPORT=${PGPORT:-5432}
read -rp "Database [postgres]: "         PGDB;    PGDB=${PGDB:-postgres}
read -rp "Username [postgres]: "         PGUSER;  PGUSER=${PGUSER:-postgres}
read -rsp "Password: "                   PGPASS;  echo
read -rp "Cổng web [5080]: "             WEBPORT; WEBPORT=${WEBPORT:-5080}

# ---- 2) Cài công cụ phụ trợ (đọc log + phân giải tên máy) ----
echo "--- Cài công cụ (dig, nmblookup, avahi) - cần sudo ---"
if command -v apt-get >/dev/null; then
  sudo apt-get update -y >/dev/null 2>&1 || true
  sudo apt-get install -y dnsutils samba-common-bin avahi-utils >/dev/null 2>&1 || true
fi

# ---- 3) Quyền đọc file log của PostgreSQL (nhóm adm) ----
echo "--- Cấp quyền đọc log cho user $USER (nhóm adm) ---"
sudo usermod -aG adm "$USER" || true

# ---- 4) Tự dò đường dẫn log của PostgreSQL ----
export PGPASSWORD="$PGPASS"
LOGPATH=""
CURLOG=$(psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDB" -Atc "SELECT pg_current_logfile();" 2>/dev/null || true)
if [[ -n "$CURLOG" ]]; then
  DATADIR=$(psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDB" -Atc "SHOW data_directory;" 2>/dev/null || true)
  [[ "$CURLOG" = /* ]] && LOGPATH="$CURLOG" || LOGPATH="$DATADIR/$CURLOG"
fi
# Mặc định kiểu Debian/Ubuntu nếu không dò được
if [[ -z "$LOGPATH" ]]; then
  LOGPATH=$(ls -t /var/log/postgresql/postgresql-*-main.log 2>/dev/null | head -1 || true)
fi
read -rp "Đường dẫn file log PostgreSQL [${LOGPATH:-/var/log/postgresql/postgresql-16-main.log}]: " IN
LOGPATH=${IN:-${LOGPATH:-/var/log/postgresql/postgresql-16-main.log}}

# ---- 5) Đặt log_line_prefix chuẩn để parser đọc đúng (reload, không restart) ----
echo "--- Đặt log_line_prefix chuẩn cho PostgreSQL ---"
psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDB" -Atc \
  "ALTER SYSTEM SET log_line_prefix = '%m [%p] %q%u@%d ';" 2>/dev/null || \
  echo "  (Bỏ qua - cần quyền superuser. Có thể đặt tay sau.)"
psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDB" -Atc "SELECT pg_reload_conf();" >/dev/null 2>&1 || true

# ---- 6) Sinh JWT key ngẫu nhiên & ghi appsettings.Production.json ----
JWTKEY=$(head -c 60 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 48)
cat > appsettings.Production.json <<JSON
{
  "ConnectionStrings": {
    "Postgres": "Host=$PGHOST;Port=$PGPORT;Database=$PGDB;Username=$PGUSER;Password=$PGPASS;Application Name=PgMonitorApi"
  },
  "Jwt": { "Key": "$JWTKEY" },
  "Monitor": { "LogFilePath": "$LOGPATH", "MinDurationToLogSeconds": 1 },
  "Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:$WEBPORT" } } }
}
JSON
chmod 600 appsettings.Production.json
chmod +x "$HERE/PgMonitorApi" 2>/dev/null || true
echo "Đã ghi appsettings.Production.json"

# ---- 7) Mở firewall ----
sudo ufw allow "${WEBPORT}/tcp" >/dev/null 2>&1 || true

# ---- 8) Cài dịch vụ systemd (tuỳ chọn) ----
read -rp "Cài dịch vụ systemd tự khởi động khi bật máy? [y/N]: " SVC
if [[ "${SVC,,}" == "y" ]]; then
  sudo tee /etc/systemd/system/pgmonitor.service >/dev/null <<UNIT
[Unit]
Description=PG Monitor API (PostgresProfiler)
After=network.target postgresql.service

[Service]
WorkingDirectory=$HERE
ExecStart=$HERE/PgMonitorApi
Restart=always
RestartSec=5
User=$USER
SupplementaryGroups=adm
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
UNIT
  sudo systemctl daemon-reload
  sudo systemctl enable --now pgmonitor
  echo ">>> Đã cài dịch vụ. Trạng thái: sudo systemctl status pgmonitor"
  echo ">>> Xem log: journalctl -u pgmonitor -f"
else
  echo ">>> Chạy thủ công (cần đăng nhập lại để có nhóm adm, hoặc dùng systemd):"
  echo "    ASPNETCORE_ENVIRONMENT=Production ./PgMonitorApi"
fi

echo "==================================================="
echo " XONG. Mở trình duyệt: http://<IP-máy>:$WEBPORT"
echo " Đăng nhập bằng tài khoản PostgreSQL."
echo "==================================================="

<#
  =============================================================
   PG Monitor - Cài đặt & cấu hình trên Windows
   Chạy trong thư mục chứa PgMonitorApi.exe:
     Chuột phải > Run with PowerShell, hoặc:
     powershell -ExecutionPolicy Bypass -File .\setup.ps1
   (Nên chạy PowerShell "Run as Administrator" để mở firewall.)
  =============================================================
#>
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "        PG MONITOR - CẤU HÌNH TRÊN WINDOWS"          -ForegroundColor Cyan
Write-Host "==================================================="  -ForegroundColor Cyan

# ---- 1) Nhập cấu hình ----
function Ask($msg, $def) { $v = Read-Host "$msg [$def]"; if ([string]::IsNullOrWhiteSpace($v)) { return $def } else { return $v } }
$PGHOST  = Ask "PostgreSQL host" "127.0.0.1"
$PGPORT  = Ask "PostgreSQL port" "5432"
$PGDB    = Ask "Database" "postgres"
$PGUSER  = Ask "Username" "postgres"
$sec     = Read-Host "Password" -AsSecureString
$PGPASS  = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
$WEBPORT = Ask "Cổng web" "5080"

$env:PGPASSWORD = $PGPASS
$psql = "psql"   # cần psql trong PATH (thường: C:\Program Files\PostgreSQL\16\bin)

# ---- 2) Tự dò đường dẫn log + đặt log_line_prefix chuẩn ----
$LOGPATH = ""
try {
  $cur = & $psql -h $PGHOST -p $PGPORT -U $PGUSER -d $PGDB -Atc "SELECT pg_current_logfile();" 2>$null
  $data = & $psql -h $PGHOST -p $PGPORT -U $PGUSER -d $PGDB -Atc "SHOW data_directory;" 2>$null
  if ($cur) { if ([System.IO.Path]::IsPathRooted($cur)) { $LOGPATH = $cur } else { $LOGPATH = Join-Path $data $cur } }
  # Đặt log_line_prefix chuẩn để parser đọc đúng (reload, không restart)
  & $psql -h $PGHOST -p $PGPORT -U $PGUSER -d $PGDB -Atc "ALTER SYSTEM SET log_line_prefix = '%m [%p] %q%u@%d ';" 2>$null | Out-Null
  & $psql -h $PGHOST -p $PGPORT -U $PGUSER -d $PGDB -Atc "SELECT pg_reload_conf();" 2>$null | Out-Null
} catch { Write-Host "  (Không dò được log qua psql - sẽ hỏi tay)" -ForegroundColor Yellow }

if (-not $LOGPATH) { $LOGPATH = "C:\Program Files\PostgreSQL\16\data\log\postgresql.log" }
$LOGPATH = Ask "Đường dẫn file log PostgreSQL" $LOGPATH

Write-Host "  Log PostgreSQL: $LOGPATH" -ForegroundColor DarkGray
Write-Host "  LƯU Ý: PostgreSQL trên Windows cần logging_collector = on (mặc định thường đã bật)." -ForegroundColor DarkGray

# ---- 3) Sinh JWT key & ghi appsettings.Production.json ----
$chars = (48..57) + (65..90) + (97..122)
$JWT = -join ($chars | Get-Random -Count 48 | ForEach-Object { [char]$_ })
$logJson = $LOGPATH.Replace('\','\\')
$cfg = @"
{
  "ConnectionStrings": {
    "Postgres": "Host=$PGHOST;Port=$PGPORT;Database=$PGDB;Username=$PGUSER;Password=$PGPASS;Application Name=PgMonitorApi"
  },
  "Jwt": { "Key": "$JWT" },
  "Monitor": { "LogFilePath": "$logJson", "MinDurationToLogSeconds": 1 },
  "Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:$WEBPORT" } } }
}
"@
Set-Content -Path (Join-Path $here "appsettings.Production.json") -Value $cfg -Encoding UTF8
Write-Host "Đã ghi appsettings.Production.json" -ForegroundColor Green

# ---- 4) Mở firewall (cần quyền Admin) ----
try {
  New-NetFirewallRule -DisplayName "PG Monitor $WEBPORT" -Direction Inbound -LocalPort $WEBPORT -Protocol TCP -Action Allow -ErrorAction Stop | Out-Null
  Write-Host "Đã mở firewall cổng $WEBPORT" -ForegroundColor Green
} catch { Write-Host "  (Không mở được firewall - chạy PowerShell as Administrator để mở cổng $WEBPORT)" -ForegroundColor Yellow }

# ---- 5) Cài Windows Service (tuỳ chọn) ----
$svc = Ask "Cài Windows Service tự khởi động? (y/N)" "N"
if ($svc -eq 'y' -or $svc -eq 'Y') {
  $bin = Join-Path $here "PgMonitorApi.exe"
  sc.exe create PgMonitor binPath= "`"$bin`"" start= auto DisplayName= "PG Monitor API" | Out-Null
  sc.exe description PgMonitor "PG Monitor - theo doi PostgreSQL realtime" | Out-Null
  # Chạy với biến môi trường Production
  [Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT","Production","Machine")
  sc.exe start PgMonitor | Out-Null
  Write-Host ">>> Đã cài & chạy Windows Service 'PgMonitor'." -ForegroundColor Green
} else {
  Write-Host ">>> Chạy thủ công:" -ForegroundColor Green
  Write-Host "    `$env:ASPNETCORE_ENVIRONMENT='Production'; .\PgMonitorApi.exe"
}

Write-Host "===================================================" -ForegroundColor Cyan
Write-Host " XONG. Mở trình duyệt: http://<IP-máy>:$WEBPORT"      -ForegroundColor Cyan
Write-Host " Đăng nhập bằng tài khoản PostgreSQL."                -ForegroundColor Cyan
Write-Host "===================================================" -ForegroundColor Cyan

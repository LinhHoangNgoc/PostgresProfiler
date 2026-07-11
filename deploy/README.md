# PG Monitor — Bản build & script cài đặt

Thư mục này chứa script cấu hình + hướng dẫn tạo bản build **self-contained** (không cần cài .NET trên máy đích).

## Tạo bản build

Trên máy có **.NET 8 SDK**, chạy trong thư mục gốc project:

```bash
# Linux
dotnet publish -c Release -r linux-x64  --self-contained true -p:PublishSingleFile=true -o dist/pgmonitor-linux-x64
# Windows
dotnet publish -c Release -r win-x64    --self-contained true -p:PublishSingleFile=true -o dist/pgmonitor-win-x64
```

(Hoặc dùng `deploy/publish.sh`.) Mỗi thư mục `dist/...` gồm: file thực thi (`PgMonitorApi` / `PgMonitorApi.exe`), `appsettings.json`, `wwwroot/`, và script `setup`.

## Cài trên máy đích

### Linux
```bash
cd pgmonitor-linux-x64
chmod +x setup.sh PgMonitorApi
./setup.sh          # hỏi thông tin PostgreSQL, tự cấu hình, mở firewall, (tuỳ chọn) cài systemd
```
`setup.sh` sẽ:
- Cài `dig` (dnsutils), `nmblookup` (samba-common-bin), `avahi-utils` để lấy **tên máy con**.
- Thêm user vào nhóm **adm** để đọc file log PostgreSQL.
- Tự dò đường dẫn log (`pg_current_logfile()`), đặt `log_line_prefix` chuẩn.
- Sinh JWT key ngẫu nhiên, ghi `appsettings.Production.json`.
- Mở cổng firewall; tuỳ chọn cài dịch vụ **systemd** tự khởi động.

### Windows
```powershell
cd pgmonitor-win-x64
powershell -ExecutionPolicy Bypass -File .\setup.ps1     # nên "Run as Administrator"
```
`setup.ps1` sẽ hỏi thông tin, ghi `appsettings.Production.json`, mở firewall, và (tuỳ chọn) cài **Windows Service**.
Yêu cầu: `psql` trong PATH (thư mục `bin` của PostgreSQL) và **logging_collector = on** (mặc định thường đã bật trên Windows).

## Sau khi cài
- Mở trình duyệt: `http://<IP-máy>:<cổng>` (mặc định 5080).
- Đăng nhập bằng **tài khoản PostgreSQL**.
- Bấm **▶ Bắt đầu** để bật trace (app tự bật `log_min_duration_statement`).

## Cấu hình thủ công
Mọi thứ nằm trong `appsettings.Production.json` (đè lên `appsettings.json`). Xem README gốc để biết các khoá cấu hình (connection string, JWT, log path, tên máy qua bảng HIS, v.v.).

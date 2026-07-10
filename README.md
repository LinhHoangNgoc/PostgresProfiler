# PG Monitor API

Ứng dụng **.NET 8 Web API** theo dõi **PostgreSQL real-time** qua trình duyệt — giống **SQL Server Profiler** nhưng chạy web, có đăng nhập, xem từ xa trong mạng LAN. **Cứ có câu lệnh chạy là hiện ngay trên grid**, kể cả query chỉ vài mili-giây.

## Cơ chế: đọc log PostgreSQL (giống trace của Profiler)

Khác với kiểu "poll `pg_stat_activity`" (chỉ thấy query đang chạy tại thời điểm chụp, bỏ lọt query nhanh), tool này **đọc trực tiếp file log của PostgreSQL**:

1. Bật `log_min_duration_statement` (0 = log MỌI câu lệnh) qua `ALTER SYSTEM` + `pg_reload_conf()` — **không cần restart PostgreSQL**.
2. `PostgresLogTailService` (BackgroundService) **tail** file log, tách từng câu lệnh đã chạy xong (kể cả câu nhiều dòng), đẩy realtime qua **SignalR** lên grid như một dòng trace.
3. Câu lệnh chạy **≥ ngưỡng** cấu hình (mặc định 1 giây) được lưu vào **SQLite** (`query_history.db`) để xem lại.

> Nhờ đọc log nên bắt được **cả** `SELECT * from dangky` chạy vài ms — điều mà cách poll không làm được.

## Tính năng

- **Đăng nhập**: `POST /api/auth/login` — xác thực bằng **chính user/password của PostgreSQL** (thử mở kết nối tới PG; đúng thì cấp **JWT** hết hạn sau 8 tiếng).
- **Trace realtime**: grid tự cập nhật, mỗi câu lệnh là 1 dòng (mới nhất ở trên), tối đa 1000 dòng. Cột: Thời gian, PID, User, Database, Duration (ms), Query. **Highlight đỏ** câu > 5 giây.
- **Bật/Tắt trace từ giao diện**: nút ▶/■ gọi `POST /api/trace/start?thresholdMs=X` và `POST /api/trace/stop`. Có ô "Ngưỡng log ≥ X ms" (0 = mọi câu lệnh) để tránh flood log trên DB bận.
- **Filter kiểu Profiler**: bật filter rồi lọc Username / Database / Query theo cả **Chứa (Like)** và **Không chứa (Not Like)**, cùng **Duration ≥ (ms)**. Chỉ hiển thị dòng khớp.
- **Tạm dừng màn hình** / **Xoá màn hình** để dễ đọc khi log dồn nhanh.
- **Lịch sử query chậm**: `GET /api/history?minDuration=X&limit=50` (yêu cầu JWT), sắp xếp theo duration giảm dần.
- Hiển thị **trạng thái kết nối** (đang kết nối / mất kết nối / lỗi) và **trạng thái trace** (bật/tắt).

## API

| Method | Route | Mô tả |
|--------|-------|-------|
| POST | `/api/auth/login` | Đăng nhập bằng user/pass PostgreSQL, trả JWT |
| POST | `/api/trace/start?thresholdMs=0` | Bật log câu lệnh (0 = tất cả) |
| POST | `/api/trace/stop` | Tắt log câu lệnh |
| GET  | `/api/trace/status` | Trạng thái trace hiện tại |
| GET  | `/api/history?minDuration=&limit=` | Lịch sử query chậm |
| WS   | `/hubs/monitor` | SignalR hub đẩy sự kiện `TraceEvents` |

Tất cả (trừ login) yêu cầu JWT. SignalR nhận token qua `access_token` trên query string.

## Cấu hình (`appsettings.json`)

| Khoá | Ý nghĩa |
|------|---------|
| `ConnectionStrings:Postgres` | Connection string admin (superuser) để bật/tắt trace và làm base cho đăng nhập. |
| `Jwt:Key` | Secret ký JWT — **đổi thành chuỗi bí mật ≥ 32 ký tự**. |
| `Monitor:LogFilePath` | Đường dẫn file log PostgreSQL cần đọc (mặc định `/var/log/postgresql/postgresql-16-main.log`). |
| `Monitor:MinDurationToLogSeconds` | Ngưỡng (giây) để ghi câu lệnh vào lịch sử SQLite (mặc định `1`). |
| `History:DatabasePath` | Đường dẫn file SQLite (mặc định `query_history.db`). |
| `Kestrel:Endpoints:Http:Url` | Địa chỉ lắng nghe (mặc định `http://0.0.0.0:5080`). |

### Điều kiện để đọc được log
- File log PostgreSQL phải **đọc được** bằng user chạy app. Trên Ubuntu/Mint, file `/var/log/postgresql/*.log` thuộc nhóm `adm` (mode 640) → thêm user vào nhóm `adm` là đọc được:
  ```bash
  sudo usermod -aG adm <user>   # đăng nhập lại để có hiệu lực
  ```
- Bật/tắt trace dùng kết nối **superuser** (để chạy `ALTER SYSTEM`). Đây là thao tác cấp hệ thống, chỉ thực hiện sau khi đã đăng nhập web.

## Lưu ý quan trọng khi dùng trên PRODUCTION
- `thresholdMs = 0` log **mọi** câu lệnh → **tăng dung lượng log & I/O** trên DB bận. Nên đặt ngưỡng (vd 200ms) để chỉ bắt câu đáng chú ý, và **Dừng trace** khi xem xong.
- App **tự tắt trace** (`log_min_duration_statement = -1`) khi dừng đúng cách (graceful shutdown). Nếu app bị kill đột ngột khi đang bật trace, chạy tay để tắt:
  ```sql
  ALTER SYSTEM SET log_min_duration_statement = -1; SELECT pg_reload_conf();
  ```
- Cột **Client** không có trong trace vì `log_line_prefix` mặc định không kèm host. Muốn có client, thêm `%h` vào `log_line_prefix` (reload, không cần restart).

## Chạy thử

```bash
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$HOME/.dotnet:$PATH
cd PgMonitorApi
dotnet restore && dotnet build && dotnet run
```
Mở `http://<IP-server>:5080` → đăng nhập bằng tài khoản PostgreSQL → bấm **▶ Bắt đầu trace** → chạy vài câu lệnh SQL ở nơi khác, grid sẽ hiện ngay.

## Chạy như một service systemd (tự khởi động lại khi reboot)

```bash
export DOTNET_ROOT=$HOME/.dotnet; export PATH=$HOME/.dotnet:$PATH
cd PgMonitorApi && dotnet publish -c Release -o /home/hogam2/pgmonitor
```

Tạo `/etc/systemd/system/pgmonitor.service`:

```ini
[Unit]
Description=PG Monitor API
After=network.target postgresql.service

[Service]
WorkingDirectory=/home/hogam2/pgmonitor
ExecStart=/home/hogam2/.dotnet/dotnet /home/hogam2/pgmonitor/PgMonitorApi.dll
Restart=always
RestartSec=5
User=hogam2
# User phải thuộc nhóm 'adm' để đọc log PostgreSQL
SupplementaryGroups=adm
Environment=DOTNET_ROOT=/home/hogam2/.dotnet
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now pgmonitor
sudo systemctl status pgmonitor
journalctl -u pgmonitor -f
```

## Bảo mật
- CORS mở cho mọi origin và lắng nghe `0.0.0.0` → **chỉ dùng trong LAN nội bộ**, không expose Internet. Mở firewall: `sudo ufw allow 5080/tcp`.
- Secret đọc từ `appsettings.json` (hoặc biến môi trường `ConnectionStrings__Postgres`, `Jwt__Key`...), không hardcode.
- Token JWT chỉ giữ trong biến JS phía trình duyệt (không localStorage).

## Ghi chú
- File `Services/PostgresMonitorService.cs` (poll `pg_stat_activity`) được giữ lại để tham khảo nhưng **không còn đăng ký chạy** — nguồn dữ liệu grid hiện là đọc log. Muốn xem query đang chạy/bị block theo kiểu snapshot, có thể đăng ký lại service này.

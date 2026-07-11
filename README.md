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
- **Đăng nhập**: có ô **Ghi nhớ đăng nhập & tự động đăng nhập** (lưu ở localStorage — chỉ dùng máy nội bộ tin cậy).
- **Trace realtime**: grid tự cập nhật, mỗi câu lệnh là 1 dòng (mới nhất ở trên), tối đa 1000 dòng. Cột: Thời gian, PID, **IP máy con, Tên máy, Phần mềm**, User, Database, Duration (ms), Query. **Highlight đỏ** câu > 5 giây.
- **Thông tin máy con**: IP (`client_addr`), phần mềm (`application_name`) tra từ `pg_stat_activity` theo PID; **tên máy** phân giải qua reverse-DNS, nếu không ra thì **NetBIOS** (`nmblookup -A <ip>`) — hợp mạng Windows LAN không có PTR. Cần gói `samba-common-bin`:
  ```bash
  sudo apt install -y samba-common-bin   # cung cấp nmblookup
  ```
  Các cột IP / Tên máy / Phần mềm hiển thị ở cả tab Trace lẫn Lịch sử, và lọc được.
- **Bật/Tắt trace từ giao diện**: nút ▶/■ gọi `POST /api/trace/start?thresholdMs=X` và `POST /api/trace/stop`. Có ô "Ngưỡng log ≥ X ms" (0 = mọi câu lệnh) để tránh flood log trên DB bận.
- **Filter động kiểu Profiler**: mặc định không có điều kiện; bấm **＋** thêm điều kiện: chọn **loại** (Query/IP/Tên máy/Phần mềm/User/Database/PID/Duration) → **kiểu** (Chứa/Không chứa với text; ≥/≤/= với số) → **giá trị**. Nhiều điều kiện AND với nhau.
- **Xem chi tiết**: click 1 dòng hiện nội dung ở panel richtext dưới; **Ctrl+click** nhiều dòng để ghép; **double-click** mở modal xem to; nút Copy.
- **Bắt câu lệnh lỗi**: ghép `ERROR` + `STATEMENT` trong log → hiện dòng lỗi (màu riêng) kèm thông báo lỗi, lưu cả vào lịch sử (cột Lỗi).
- **Grid như SQL Profiler**: kéo rộng cột, đổi vị trí cột, **icon phễu lọc từng cột** (đậm + đổi màu khi đang lọc), nhớ layout theo máy. Có **Tìm nhanh** (lọc mọi cột).
- **Tự cuộn**: mới nhất ở đáy, bật "Tự cuộn" thì tự trôi xuống dòng mới; tắt thì vẫn nối dữ liệu nhưng giữ nguyên vị trí đang xem.
- **Lưu CSV**: xuất trace hoặc lịch sử ra file CSV (mở bằng Excel). **Đổi theme sáng/tối**. Format màu cú pháp SQL ở panel chi tiết + modal.
- **Xoá màn hình / Xoá filter / Cột mặc định**.
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
| `Monitor:MinDurationToLogSeconds` | Ngưỡng (giây) để ghi câu lệnh vào lịch sử SQLite (mặc định `1`). Câu lỗi luôn được lưu. |
| `Monitor:MachineNameSql` | (tuỳ chọn) SQL trả về `(ip, tên_máy)` để lấy tên máy từ bảng HIS, vd `SELECT ipmaycon, tenmaycon FROM public.thoatmaycon`. Trống = tắt. |
| `ConnectionStrings:MachineName` | (tuỳ chọn) Connection string tới DB chứa bảng tên máy (vd `HIS6`); không set thì dùng chung. |
| `History:DatabasePath` | Đường dẫn file SQLite (mặc định `query_history.db`). |
| `Kestrel:Endpoints:Http:Url` | Địa chỉ lắng nghe (mặc định `http://0.0.0.0:5080`). |

### Tách secret khỏi Git
- `appsettings.json` (đẩy lên repo) chỉ chứa **placeholder** (`Password=CHANGE_ME`).
- Secret **thật** đặt trong `appsettings.Production.json` (đã `.gitignore`, KHÔNG đẩy lên GitHub) — ASP.NET tự nạp đè khi `ASPNETCORE_ENVIRONMENT=Production` (mặc định khi `dotnet run`/systemd). Ví dụ:
  ```json
  {
    "ConnectionStrings": { "Postgres": "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=<mật khẩu thật>" },
    "Jwt": { "Key": "<chuỗi bí mật >= 32 ký tự>" }
  }
  ```
  Hoặc dùng biến môi trường: `ConnectionStrings__Postgres`, `Jwt__Key`.

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
- Thông tin máy con (IP/tên máy/phần mềm) được tra từ `pg_stat_activity` theo PID nên chính xác nhất với kết nối đang mở (pool bền của HIS). Kết nối vừa mở vừa đóng rất nhanh có thể chưa kịp tra.

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

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using PgMonitorApi.Hubs;
using PgMonitorApi.Models;

namespace PgMonitorApi.Services;

/// <summary>
/// Đọc (tail) file log của PostgreSQL, tách từng câu lệnh đã chạy xong
/// (log_min_duration_statement), đẩy realtime lên grid như trace của Profiler,
/// và ghi các câu chậm vào SQLite. Bắt được CẢ query nhanh vài mili-giây.
/// </summary>
public partial class PostgresLogTailService : BackgroundService
{
    private readonly IHubContext<MonitorHub> _hub;
    private readonly QueryHistoryStore _history;
    private readonly ClientInfoService _clientInfo;
    private readonly ILogger<PostgresLogTailService> _logger;

    private readonly string _logPath;
    private readonly double _minDurationToLogSeconds;

    // Nhận diện dòng bắt đầu một bản ghi log: "2026-07-10 23:17:10.799 +07 [12345] "
    // Timezone chấp nhận cả dạng offset (+07) lẫn tên (ICT, UTC...) để hợp PG trên Windows.
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} (?:[+-]\d{2,4}|[A-Za-z]{2,5}) \[\d+\] ")]
    private static partial Regex StartLineRegex();

    // Tách toàn bộ một bản ghi (có thể nhiều dòng) thành các thành phần.
    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} (?:[+-]\d{2,4}|[A-Za-z]{2,5})) \[(?<pid>\d+)\] (?:(?<user>[^@\s]+)@(?<db>\S+) )?(?<sev>[A-Z]+):\s{1,2}(?<msg>.*)$",
        RegexOptions.Singleline)]
    private static partial Regex EntryRegex();

    // Lấy duration + loại + SQL từ message "duration: 0.518 ms  statement: SELECT 1;"
    [GeneratedRegex(@"^duration: (?<ms>[\d.]+) ms  (?<kind>statement|execute [^:]*): (?<sql>.*)$",
        RegexOptions.Singleline)]
    private static partial Regex DurationRegex();

    public PostgresLogTailService(
        IHubContext<MonitorHub> hub,
        QueryHistoryStore history,
        ClientInfoService clientInfo,
        IConfiguration config,
        ILogger<PostgresLogTailService> logger)
    {
        _hub = hub;
        _history = history;
        _clientInfo = clientInfo;
        _logger = logger;
        _logPath = config["Monitor:LogFilePath"] ?? "/var/log/postgresql/postgresql-16-main.log";
        _minDurationToLogSeconds = config.GetValue("Monitor:MinDurationToLogSeconds", 1.0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PostgresLogTailService đọc log tại {Path}", _logPath);

        long position = 0;
        string leftover = "";              // phần dòng chưa trọn vẹn giữa 2 lần đọc
        var pending = new List<string>();  // các dòng của bản ghi đang gom
        var batch = new List<TraceEvent>();

        // Bắt đầu đọc từ CUỐI file để chỉ lấy sự kiện mới.
        try
        {
            if (File.Exists(_logPath))
                position = new FileInfo(_logPath).Length;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Không mở được file log {Path}", _logPath);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
        do
        {
            try
            {
                if (!File.Exists(_logPath)) continue;

                long length = new FileInfo(_logPath).Length;
                if (length < position)
                {
                    // File đã bị xoay vòng (logrotate) hoặc truncate -> đọc lại từ đầu.
                    position = 0;
                    leftover = "";
                }
                if (length == position) { await FlushBatch(batch, stoppingToken); continue; }

                // Đọc phần mới được ghi thêm.
                string chunk;
                using (var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(position, SeekOrigin.Begin);
                    var buffer = new byte[length - position];
                    int read = await fs.ReadAsync(buffer, stoppingToken);
                    position += read;
                    chunk = Encoding.UTF8.GetString(buffer, 0, read);
                }

                // Ghép phần dư trước đó, tách dòng; giữ lại dòng cuối nếu chưa có '\n'.
                chunk = leftover + chunk;
                var lines = chunk.Split('\n');
                leftover = chunk.EndsWith('\n') ? "" : lines[^1];
                int lineCount = chunk.EndsWith('\n') ? lines.Length : lines.Length - 1;

                for (int i = 0; i < lineCount; i++)
                {
                    var line = lines[i].TrimEnd('\r');
                    if (StartLineRegex().IsMatch(line))
                    {
                        // Bắt đầu bản ghi mới -> chốt bản ghi trước.
                        if (pending.Count > 0) EmitEntry(string.Join('\n', pending), batch);
                        pending.Clear();
                        pending.Add(line);
                    }
                    else if (pending.Count > 0)
                    {
                        // Dòng tiếp theo của câu lệnh nhiều dòng (bỏ tab thụt đầu dòng).
                        pending.Add(line.StartsWith('\t') ? line[1..] : line);
                    }
                }

                // PG ghi trọn một bản ghi trong một lần -> chốt nốt bản ghi còn treo.
                if (pending.Count > 0)
                {
                    EmitEntry(string.Join('\n', pending), batch);
                    pending.Clear();
                }

                await FlushBatch(batch, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đọc log PostgreSQL");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // Lỗi đang chờ ghép với dòng STATEMENT (key = pid).
    private readonly Dictionary<int, (string Msg, DateTimeOffset Ts, string? User, string? Db)> _pendingErr = new();

    /// <summary>
    /// Phân tích một bản ghi log. Xử lý 2 loại:
    /// - LOG "duration: ... statement/execute:" -> câu lệnh chạy xong (có duration).
    /// - ERROR/FATAL + STATEMENT -> câu lệnh bị lỗi (ghép 2 dòng liền nhau theo pid).
    /// </summary>
    private void EmitEntry(string entry, List<TraceEvent> batch)
    {
        var m = EntryRegex().Match(entry);
        if (!m.Success) return;

        var sev = m.Groups["sev"].Value;
        var msg = m.Groups["msg"].Value;
        int pidNum = int.TryParse(m.Groups["pid"].Value, out var pid) ? pid : 0;
        string? user = m.Groups["user"].Success ? m.Groups["user"].Value : null;
        string? db = m.Groups["db"].Success ? m.Groups["db"].Value : null;
        DateTimeOffset ts = default;
        DateTimeOffset.TryParse(m.Groups["ts"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out ts);

        switch (sev)
        {
            case "LOG":
                var dm = DurationRegex().Match(msg);
                if (!dm.Success) return; // bỏ parse/bind và log không phải câu lệnh
                double ms = double.TryParse(dm.Groups["ms"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
                Emit(batch, ts, pidNum, user, db,
                     dm.Groups["kind"].Value.StartsWith("execute") ? "execute" : "statement",
                     dm.Groups["sql"].Value.Trim(), ms, null);
                break;

            case "ERROR":
            case "FATAL":
            case "PANIC":
                // Chờ dòng STATEMENT kế tiếp để lấy nội dung câu lệnh.
                if (_pendingErr.Count > 200) _pendingErr.Clear();
                _pendingErr[pidNum] = (msg.Trim(), ts, user, db);
                break;

            case "DETAIL":
            case "HINT":
            case "CONTEXT":
                if (_pendingErr.TryGetValue(pidNum, out var pe0))
                    _pendingErr[pidNum] = (pe0.Msg + "\n" + sev + ": " + msg.Trim(), pe0.Ts, pe0.User, pe0.Db);
                break;

            case "STATEMENT":
                if (_pendingErr.TryGetValue(pidNum, out var pe))
                {
                    _pendingErr.Remove(pidNum);
                    Emit(batch, pe.Ts == default ? ts : pe.Ts, pidNum, user ?? pe.User, db ?? pe.Db,
                         "error", msg.Trim(), 0, pe.Msg);
                }
                break;
        }
    }

    /// <summary>Tạo TraceEvent (kèm thông tin máy con), đẩy batch và ghi lịch sử nếu cần.</summary>
    private void Emit(List<TraceEvent> batch, DateTimeOffset ts, int pid, string? user, string? db,
                      string kind, string sql, double ms, string? error)
    {
        // Bỏ qua query nội bộ của chính app monitor (đánh dấu /*pgmon*/ hoặc app_name PgMonitorApi).
        if (sql.Contains("/*pgmon*/")) return;
        var info = _clientInfo.Get(pid);
        if (info?.Application == "PgMonitorApi") return;
        string? ip = info?.Ip;

        var ev = new TraceEvent
        {
            Time = ts == default ? null : ts.ToString("o"),
            Pid = pid,
            User = user,
            Database = db,
            Client = ip,
            ClientHost = _clientInfo.HostFor(ip),
            Application = info?.Application,
            DurationMs = ms,
            Kind = kind,
            Error = error,
            Query = sql
        };
        batch.Add(ev);

        // Ghi lịch sử: câu chậm (>= ngưỡng) HOẶC câu bị lỗi (luôn lưu).
        if (error != null || ms / 1000.0 >= _minDurationToLogSeconds)
        {
            _history.Insert(new QueryHistoryEntry
            {
                Pid = pid,
                UserName = user,
                Database = db,
                Client = ip,
                ClientHost = ev.ClientHost,
                Application = ev.Application,
                QueryStart = ts == default ? null : ts.UtcDateTime.AddMilliseconds(-ms),
                QueryEnd = ts == default ? DateTime.UtcNow : ts.UtcDateTime,
                DurationSeconds = ms / 1000.0,
                QueryText = sql,
                Error = error
            });
        }
    }

    /// <summary>Đẩy cả lô sự kiện tới client rồi xoá lô (giảm số lần gửi SignalR).</summary>
    private async Task FlushBatch(List<TraceEvent> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        var toSend = batch.ToArray();
        batch.Clear();
        await _hub.Clients.All.SendAsync("TraceEvents", toSend, ct);
    }
}

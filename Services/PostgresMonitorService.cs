using Microsoft.AspNetCore.SignalR;
using Npgsql;
using PgMonitorApi.Hubs;
using PgMonitorApi.Models;

namespace PgMonitorApi.Services;

/// <summary>
/// BackgroundService chạy nền: mỗi N giây poll pg_stat_activity, đẩy danh sách
/// query đang chạy qua SignalR, và ghi lại các query chậm đã kết thúc vào SQLite.
/// </summary>
public class PostgresMonitorService : BackgroundService
{
    private readonly IHubContext<MonitorHub> _hub;
    private readonly QueryHistoryStore _history;
    private readonly IConfiguration _config;
    private readonly ILogger<PostgresMonitorService> _logger;

    private readonly string _connectionString;
    private readonly int _pollingIntervalSeconds;
    private readonly double _minDurationToLogSeconds;

    // Theo dõi các query đang chạy giữa các lần poll (key = pid).
    private readonly Dictionary<int, ActiveQuery> _tracked = new();

    public PostgresMonitorService(
        IHubContext<MonitorHub> hub,
        QueryHistoryStore history,
        IConfiguration config,
        ILogger<PostgresMonitorService> logger)
    {
        _hub = hub;
        _history = history;
        _config = config;
        _logger = logger;

        _connectionString = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres");
        _pollingIntervalSeconds = Math.Max(1, config.GetValue("Monitor:PollingIntervalSeconds", 1));
        _minDurationToLogSeconds = config.GetValue("Monitor:MinDurationToLogSeconds", 1.0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "PostgresMonitorService bắt đầu. Poll mỗi {Interval}s, ngưỡng ghi lịch sử {Min}s",
            _pollingIntervalSeconds, _minDurationToLogSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_pollingIntervalSeconds));
        do
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Lỗi tạm thời (mất kết nối DB...) không được làm chết service.
                _logger.LogError(ex, "Lỗi khi poll pg_stat_activity");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var current = await FetchActiveQueriesAsync(ct);

        // Đẩy danh sách live tới mọi client.
        await _hub.Clients.All.SendAsync("ActiveQueries", current, ct);

        // Phát hiện query đã kết thúc: có trong _tracked nhưng không còn ở lần poll này,
        // hoặc pid được tái sử dụng cho query khác (query_start đổi).
        var currentByPid = current.ToDictionary(q => q.Pid);

        foreach (var (pid, previous) in _tracked.ToList())
        {
            bool stillRunning = currentByPid.TryGetValue(pid, out var now)
                                && now!.QueryStart == previous.QueryStart;
            if (!stillRunning)
            {
                LogIfSlow(previous);
                _tracked.Remove(pid);
            }
        }

        // Cập nhật trạng thái mới nhất cho các query đang chạy.
        foreach (var q in current)
        {
            _tracked[q.Pid] = q;
        }
    }

    /// <summary>Ghi vào lịch sử nếu query đạt ngưỡng thời gian cấu hình.</summary>
    private void LogIfSlow(ActiveQuery q)
    {
        if (q.DurationSeconds < _minDurationToLogSeconds) return;

        _history.Insert(new QueryHistoryEntry
        {
            Pid = q.Pid,
            UserName = q.UserName,
            Database = q.Database,
            Client = q.Client,
            QueryStart = q.QueryStart,
            QueryEnd = DateTime.UtcNow, // xấp xỉ: thời điểm phát hiện query biến mất
            DurationSeconds = q.DurationSeconds,
            QueryText = q.Query
        });
    }

    private async Task<List<ActiveQuery>> FetchActiveQueriesAsync(CancellationToken ct)
    {
        const string sql = @"
            SELECT pid,
                   usename,
                   datname,
                   client_addr,
                   state,
                   query_start,
                   EXTRACT(EPOCH FROM (now() - query_start)) AS duration_seconds,
                   query
            FROM pg_stat_activity
            WHERE state IS NOT NULL
              AND state <> 'idle'          -- chỉ lấy session đang hoạt động
              AND pid <> pg_backend_pid()  -- loại bỏ chính kết nối của service này
              AND query_start IS NOT NULL
            ORDER BY duration_seconds DESC;";

        var list = new List<ActiveQuery>();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            list.Add(new ActiveQuery
            {
                Pid = reader.GetInt32(0),
                UserName = reader.IsDBNull(1) ? null : reader.GetString(1),
                Database = reader.IsDBNull(2) ? null : reader.GetString(2),
                Client = reader.IsDBNull(3) ? null : reader.GetFieldValue<System.Net.IPAddress>(3).ToString(),
                State = reader.IsDBNull(4) ? null : reader.GetString(4),
                QueryStart = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                DurationSeconds = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                Query = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }

        return list;
    }
}

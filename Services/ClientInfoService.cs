using System.Collections.Concurrent;
using System.Net;
using Npgsql;

namespace PgMonitorApi.Services;

/// <summary>
/// Bổ sung thông tin máy con cho mỗi dòng trace: IP + phần mềm (application_name)
/// tra từ pg_stat_activity theo PID, và tên máy (hostname) qua reverse-DNS.
/// Log của PostgreSQL không kèm sẵn các thông tin này nên phải tra bổ sung.
/// </summary>
public class ClientInfoService : BackgroundService
{
    public record PidInfo(string? Ip, string? Application);

    private readonly string _conn;
    private readonly ILogger<ClientInfoService> _logger;

    // pid -> thông tin client; giữ cả pid đã đóng gần đây để trace kịp tra.
    private readonly ConcurrentDictionary<int, (PidInfo Info, DateTime Seen)> _byPid = new();
    // ip -> tên máy (reverse-DNS), cache để tránh tra DNS liên tục.
    private readonly ConcurrentDictionary<string, (string? Host, DateTime Ts)> _hostByIp = new();

    private static readonly TimeSpan PidTtl = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan HostTtl = TimeSpan.FromMinutes(15);

    public ClientInfoService(IConfiguration config, ILogger<ClientInfoService> logger)
    {
        _logger = logger;
        _conn = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres");
    }

    /// <summary>Lấy IP + application_name của một PID (null nếu chưa biết).</summary>
    public PidInfo? Get(int pid) => _byPid.TryGetValue(pid, out var v) ? v.Info : null;

    /// <summary>Lấy tên máy đã reverse-DNS cho IP (null nếu chưa có/không phân giải được).</summary>
    public string? HostFor(string? ip)
        => ip != null && _hostByIp.TryGetValue(ip, out var v) ? v.Host : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try { await RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Lỗi cập nhật thông tin client"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        const string sql = @"SELECT pid, client_addr, application_name
                             FROM pg_stat_activity
                             WHERE client_addr IS NOT NULL;";
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var now = DateTime.UtcNow;
        while (await reader.ReadAsync(ct))
        {
            int pid = reader.GetInt32(0);
            string? ip = reader.IsDBNull(1) ? null : reader.GetFieldValue<IPAddress>(1).ToString();
            string? app = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (string.IsNullOrWhiteSpace(app)) app = null;
            _byPid[pid] = (new PidInfo(ip, app), now);
            if (ip != null) _ = EnsureHostAsync(ip);
        }

        // Dọn pid quá hạn.
        foreach (var kv in _byPid)
            if (now - kv.Value.Seen > PidTtl) _byPid.TryRemove(kv.Key, out _);
    }

    /// <summary>Reverse-DNS IP -> tên máy (best-effort, có timeout, cache lại kể cả khi thất bại).</summary>
    private async Task EnsureHostAsync(string ip)
    {
        if (_hostByIp.TryGetValue(ip, out var cached) && DateTime.UtcNow - cached.Ts < HostTtl)
            return;

        string? host = null;
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);
            if (await Task.WhenAny(lookup, Task.Delay(400)) == lookup)
            {
                var name = lookup.Result.HostName;
                // Bỏ nếu chỉ trả về lại IP; lấy phần tên ngắn (bỏ domain).
                if (!string.IsNullOrWhiteSpace(name) && name != ip)
                    host = name.Split('.')[0];
            }
        }
        catch { /* không phân giải được -> để null */ }

        _hostByIp[ip] = (host, DateTime.UtcNow);
    }
}

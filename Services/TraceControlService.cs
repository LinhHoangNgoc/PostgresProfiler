using Npgsql;

namespace PgMonitorApi.Services;

/// <summary>
/// Bật/tắt trace bằng cách chỉnh log_min_duration_statement của PostgreSQL
/// (ALTER SYSTEM + pg_reload_conf, không cần restart). Dùng kết nối admin (superuser)
/// cấu hình trong appsettings — thao tác cấp hệ thống, chỉ cho phép sau khi đăng nhập.
/// </summary>
public class TraceControlService
{
    private readonly string _adminConn;
    private readonly ILogger<TraceControlService> _logger;

    public TraceControlService(IConfiguration config, ILogger<TraceControlService> logger)
    {
        _logger = logger;
        _adminConn = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres");
    }

    /// <summary>Bật trace: log mọi câu lệnh có duration >= thresholdMs (0 = tất cả).</summary>
    public async Task StartAsync(int thresholdMs, CancellationToken ct)
    {
        if (thresholdMs < 0) thresholdMs = 0;
        // thresholdMs là số nguyên do server kiểm soát -> an toàn khi nội suy vào ALTER SYSTEM.
        await ExecAsync($"ALTER SYSTEM SET log_min_duration_statement = {thresholdMs}", ct);
        await ExecAsync("SELECT pg_reload_conf()", ct);
        _logger.LogInformation("Bật trace: log_min_duration_statement = {Ms}ms", thresholdMs);
    }

    /// <summary>Tắt trace: ngừng log câu lệnh (-1).</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await ExecAsync("ALTER SYSTEM SET log_min_duration_statement = -1", ct);
        await ExecAsync("SELECT pg_reload_conf()", ct);
        _logger.LogInformation("Tắt trace: log_min_duration_statement = -1");
    }

    /// <summary>Trả về giá trị hiện tại của log_min_duration_statement (ms, -1 = tắt).</summary>
    public async Task<int> GetThresholdAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_adminConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SHOW log_min_duration_statement", conn);
        var val = (string?)await cmd.ExecuteScalarAsync(ct);
        // Giá trị có thể là "-1", "0", "1s", "250ms"... chuẩn hoá về ms đơn giản.
        if (string.IsNullOrWhiteSpace(val)) return -1;
        val = val.Trim();
        if (val == "-1") return -1;
        if (val.EndsWith("ms")) return int.TryParse(val[..^2].Trim(), out var m) ? m : 0;
        if (val.EndsWith("s")) return double.TryParse(val[..^1].Trim(), out var s) ? (int)(s * 1000) : 0;
        return int.TryParse(val, out var n) ? n : 0;
    }

    private async Task ExecAsync(string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_adminConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

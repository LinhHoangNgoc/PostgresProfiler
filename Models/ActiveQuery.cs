namespace PgMonitorApi.Models;

/// <summary>
/// Một session/query đang chạy, lấy từ pg_stat_activity và đẩy realtime tới client.
/// </summary>
public class ActiveQuery
{
    public int Pid { get; set; }
    public string? UserName { get; set; }
    public string? Database { get; set; }
    public string? Client { get; set; }
    public string? State { get; set; }

    /// <summary>Thời điểm query bắt đầu chạy (query_start).</summary>
    public DateTime? QueryStart { get; set; }

    /// <summary>Số giây đã chạy tới thời điểm poll.</summary>
    public double DurationSeconds { get; set; }

    public string? Query { get; set; }
}

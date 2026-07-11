namespace PgMonitorApi.Models;

/// <summary>
/// Một bản ghi lịch sử query chậm đã kết thúc (giống trace file của Profiler).
/// </summary>
public class QueryHistoryEntry
{
    public long Id { get; set; }
    public int Pid { get; set; }
    public string? UserName { get; set; }
    public string? Database { get; set; }
    public string? Client { get; set; }
    public string? ClientHost { get; set; }
    public string? Application { get; set; }
    public DateTime? QueryStart { get; set; }
    public DateTime QueryEnd { get; set; }
    public double DurationSeconds { get; set; }
    public string? QueryText { get; set; }
    public string? Error { get; set; }
}

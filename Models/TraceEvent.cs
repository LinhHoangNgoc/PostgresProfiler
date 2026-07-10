namespace PgMonitorApi.Models;

/// <summary>
/// Một sự kiện trace: một câu lệnh SQL đã CHẠY XONG, đọc từ log PostgreSQL
/// (log_min_duration_statement). Giống một dòng trên grid của SQL Profiler.
/// </summary>
public class TraceEvent
{
    /// <summary>Thời điểm log ghi (ISO 8601).</summary>
    public string? Time { get; set; }
    public int Pid { get; set; }
    public string? User { get; set; }
    public string? Database { get; set; }

    /// <summary>IP máy con (client_addr).</summary>
    public string? Client { get; set; }

    /// <summary>Tên máy con (reverse-DNS của IP, nếu phân giải được).</summary>
    public string? ClientHost { get; set; }

    /// <summary>Phần mềm kết nối (application_name), vd pgAdmin/DBeaver/tên app.</summary>
    public string? Application { get; set; }

    /// <summary>Thời gian thực thi (mili-giây).</summary>
    public double DurationMs { get; set; }

    /// <summary>Loại: statement (simple) hoặc execute (extended protocol).</summary>
    public string? Kind { get; set; }

    public string? Query { get; set; }
}

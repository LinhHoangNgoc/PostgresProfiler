using Microsoft.Data.Sqlite;
using PgMonitorApi.Models;

namespace PgMonitorApi.Services;

/// <summary>
/// Lưu và truy vấn lịch sử query chậm trong file SQLite cục bộ (query_history.db).
/// Đăng ký dạng singleton; các thao tác ghi được khoá để an toàn khi đa luồng.
/// </summary>
public class QueryHistoryStore
{
    private readonly string _connectionString;
    private readonly object _writeLock = new();
    private readonly ILogger<QueryHistoryStore> _logger;

    public QueryHistoryStore(IConfiguration config, ILogger<QueryHistoryStore> logger)
    {
        _logger = logger;
        // File DB đặt cạnh thư mục chạy ứng dụng.
        var dbPath = config["History:DatabasePath"] ?? "query_history.db";
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath
        }.ToString();

        Initialize();
    }

    /// <summary>Tạo bảng nếu chưa tồn tại.</summary>
    private void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS QueryHistory (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                Pid             INTEGER,
                UserName        TEXT,
                Database        TEXT,
                Client          TEXT,
                ClientHost      TEXT,
                Application     TEXT,
                QueryStart      TEXT,
                QueryEnd        TEXT,
                DurationSeconds REAL,
                QueryText       TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_QueryHistory_Duration ON QueryHistory(DurationSeconds);";
        cmd.ExecuteNonQuery();

        // Migration nhẹ: bổ sung cột cho DB cũ (SQLite không có ADD COLUMN IF NOT EXISTS).
        EnsureColumn(conn, "ClientHost");
        EnsureColumn(conn, "Application");
        _logger.LogInformation("SQLite history store sẵn sàng tại {Conn}", _connectionString);
    }

    /// <summary>Thêm cột nếu bảng cũ chưa có.</summary>
    private static void EnsureColumn(SqliteConnection conn, string column)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('QueryHistory') WHERE name = $c";
        check.Parameters.AddWithValue("$c", column);
        if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE QueryHistory ADD COLUMN {column} TEXT";
        alter.ExecuteNonQuery();
    }

    /// <summary>Ghi một query đã kết thúc vào lịch sử.</summary>
    public void Insert(QueryHistoryEntry entry)
    {
        lock (_writeLock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO QueryHistory
                    (Pid, UserName, Database, Client, ClientHost, Application, QueryStart, QueryEnd, DurationSeconds, QueryText)
                VALUES
                    ($pid, $user, $db, $client, $host, $app, $start, $end, $dur, $text);";
            cmd.Parameters.AddWithValue("$pid", entry.Pid);
            cmd.Parameters.AddWithValue("$user", (object?)entry.UserName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$db", (object?)entry.Database ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$client", (object?)entry.Client ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$host", (object?)entry.ClientHost ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$app", (object?)entry.Application ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$start", (object?)entry.QueryStart?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$end", entry.QueryEnd.ToString("o"));
            cmd.Parameters.AddWithValue("$dur", entry.DurationSeconds);
            cmd.Parameters.AddWithValue("$text", (object?)entry.QueryText ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Lấy các query chậm đã lưu, sắp xếp theo duration giảm dần.</summary>
    public List<QueryHistoryEntry> Query(double minDuration, int limit)
    {
        var result = new List<QueryHistoryEntry>();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Pid, UserName, Database, Client, ClientHost, Application, QueryStart, QueryEnd, DurationSeconds, QueryText
            FROM QueryHistory
            WHERE DurationSeconds >= $minDur
            ORDER BY DurationSeconds DESC
            LIMIT $limit;";
        cmd.Parameters.AddWithValue("$minDur", minDuration);
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new QueryHistoryEntry
            {
                Id = reader.GetInt64(0),
                Pid = reader.GetInt32(1),
                UserName = reader.IsDBNull(2) ? null : reader.GetString(2),
                Database = reader.IsDBNull(3) ? null : reader.GetString(3),
                Client = reader.IsDBNull(4) ? null : reader.GetString(4),
                ClientHost = reader.IsDBNull(5) ? null : reader.GetString(5),
                Application = reader.IsDBNull(6) ? null : reader.GetString(6),
                QueryStart = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7)),
                QueryEnd = DateTime.Parse(reader.GetString(8)),
                DurationSeconds = reader.GetDouble(9),
                QueryText = reader.IsDBNull(10) ? null : reader.GetString(10)
            });
        }
        return result;
    }
}

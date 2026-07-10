using Npgsql;

namespace PgMonitorApi.Services;

/// <summary>
/// Xác thực đăng nhập web bằng CHÍNH user/password của PostgreSQL:
/// thử mở kết nối tới PG bằng thông tin người dùng nhập vào. Kết nối thành công = hợp lệ.
/// </summary>
public class PgLoginValidator
{
    private readonly string _baseConnectionString;
    private readonly ILogger<PgLoginValidator> _logger;

    public PgLoginValidator(IConfiguration config, ILogger<PgLoginValidator> logger)
    {
        _logger = logger;
        _baseConnectionString = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres");
    }

    /// <summary>
    /// Trả về true nếu username/password mở được kết nối tới PostgreSQL.
    /// Lấy host/port/database từ connection string cấu hình sẵn, chỉ thay Username/Password.
    /// </summary>
    public async Task<bool> ValidateAsync(string username, string password, CancellationToken ct)
    {
        // Xây connection string đăng nhập: giữ nguyên host/port/db, thay user/pass người dùng nhập.
        var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            Username = username,
            Password = password,
            // Không dùng pool cho kết nối kiểm tra để tránh giữ credential trong pool.
            Pooling = false,
            Timeout = 5
        };

        try
        {
            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);
            return true;
        }
        catch (NpgsqlException ex)
        {
            _logger.LogWarning("Đăng nhập PG thất bại cho user '{User}': {Msg}", username, ex.Message);
            return false;
        }
    }
}

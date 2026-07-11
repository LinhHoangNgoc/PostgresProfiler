using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Phân giải IP -> tên máy con (best-effort, cache lại kể cả khi thất bại):
    /// 1) reverse-DNS; 2) nếu không ra thì NetBIOS qua nmblookup (mạng Windows LAN).
    /// </summary>
    private async Task EnsureHostAsync(string ip)
    {
        if (_hostByIp.TryGetValue(ip, out var cached) && DateTime.UtcNow - cached.Ts < HostTtl)
            return;

        string? host = await ReverseDnsAsync(ip) ?? await NetBiosAsync(ip);
        _hostByIp[ip] = (host, DateTime.UtcNow);
    }

    /// <summary>Reverse-DNS (thường trống trên LAN vì không có bản ghi PTR).</summary>
    private static async Task<string?> ReverseDnsAsync(string ip)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);
            if (await Task.WhenAny(lookup, Task.Delay(400)) == lookup)
            {
                var name = lookup.Result.HostName;
                if (!string.IsNullOrWhiteSpace(name) && name != ip)
                    return name.Split('.')[0];
            }
        }
        catch { }
        return null;
    }

    // Bắt tên máy (unique <00>, không phải <GROUP>) từ output của nmblookup -A.
    private static readonly Regex NmbNameRegex =
        new(@"^\s*(\S+)\s+<00>\s+-\s+(?!<GROUP>)", RegexOptions.Multiline);

    /// <summary>Tra tên máy Windows qua NetBIOS: nmblookup -A ip.</summary>
    private async Task<string?> NetBiosAsync(string ip)
    {
        try
        {
            var psi = new ProcessStartInfo("nmblookup", $"-A {ip}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            if (await Task.WhenAny(outputTask, Task.Delay(1500)) != outputTask)
            {
                try { proc.Kill(true); } catch { }
                return null;
            }
            var output = await outputTask;
            var m = NmbNameRegex.Match(output);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "nmblookup lỗi cho {Ip}", ip);
            return null;
        }
    }
}

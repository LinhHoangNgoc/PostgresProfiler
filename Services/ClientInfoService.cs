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
    private readonly string? _machineNameSql;    // SQL tuỳ chọn: trả (ip, tên máy) từ bảng HIS
    private readonly string _machineNameConn;    // DB chứa bảng tên máy (mặc định = _conn)
    private int _tick;
    private readonly ILogger<ClientInfoService> _logger;

    // pid -> thông tin client; giữ cả pid đã đóng gần đây để trace kịp tra.
    private readonly ConcurrentDictionary<int, (PidInfo Info, DateTime Seen)> _byPid = new();
    // ip -> tên máy (reverse-DNS / NetBIOS), cache để tránh tra liên tục.
    private readonly ConcurrentDictionary<string, (string? Host, DateTime Ts)> _hostByIp = new();
    // ip -> tên máy theo bảng HIS (ưu tiên hơn DNS/NetBIOS nếu có).
    private readonly ConcurrentDictionary<string, string> _hisNameByIp = new();

    private static readonly TimeSpan PidTtl = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan HostTtl = TimeSpan.FromMinutes(15);

    public ClientInfoService(IConfiguration config, ILogger<ClientInfoService> logger)
    {
        _logger = logger;
        _conn = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres");
        _machineNameSql = config["Monitor:MachineNameSql"]; // vd: SELECT ipmaycon, tenmaycon FROM public.thoatmaycon
        if (string.IsNullOrWhiteSpace(_machineNameSql)) _machineNameSql = null;
        // DB chứa bảng tên máy (vd HIS6); nếu không cấu hình thì dùng chung connection.
        _machineNameConn = config.GetConnectionString("MachineName") ?? _conn;
    }

    /// <summary>Lấy IP + application_name của một PID (null nếu chưa biết).</summary>
    public PidInfo? Get(int pid) => _byPid.TryGetValue(pid, out var v) ? v.Info : null;

    /// <summary>Tên máy cho IP: ưu tiên bảng HIS, sau đó tới reverse-DNS/NetBIOS.</summary>
    public string? HostFor(string? ip)
    {
        if (ip == null) return null;
        if (_hisNameByIp.TryGetValue(ip, out var his) && !string.IsNullOrWhiteSpace(his)) return his;
        return _hostByIp.TryGetValue(ip, out var v) ? v.Host : null;
    }

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
        // Đánh dấu /*pgmon*/ để log-tail loại query nội bộ của chính app khỏi trace.
        const string sql = @"/*pgmon*/ SELECT pid, client_addr, application_name
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

        // Cập nhật tên máy theo bảng HIS mỗi ~30s (nếu có cấu hình).
        if (_machineNameSql != null && _tick++ % 15 == 0)
            await RefreshHisNamesAsync(ct);
    }

    /// <summary>Đọc bảng HIS (ip -> tên máy) để bổ sung tên máy con.</summary>
    private async Task RefreshHisNamesAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_machineNameConn);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("/*pgmon*/ " + _machineNameSql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var ip = reader.IsDBNull(0) ? null : reader.GetValue(0)?.ToString()?.Trim();
                var name = reader.IsDBNull(1) ? null : reader.GetValue(1)?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(ip) && !string.IsNullOrWhiteSpace(name))
                    _hisNameByIp[ip] = name;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Không đọc được bảng tên máy HIS (Monitor:MachineNameSql)");
        }
    }

    private string? _gateway;

    /// <summary>
    /// Phân giải IP -> tên máy con (best-effort, cache lại kể cả khi thất bại). Thử nhiều cách:
    /// 1) DNS ngược qua router (dig @gateway) — bắt được máy DHCP; 2) DNS ngược mặc định;
    /// 3) NetBIOS (nmblookup); 4) mDNS (avahi-resolve). Trả tên ngắn (bỏ domain).
    /// </summary>
    private async Task EnsureHostAsync(string ip)
    {
        if (_hostByIp.TryGetValue(ip, out var cached) && DateTime.UtcNow - cached.Ts < HostTtl)
            return;

        string? host = await DigAsync(ip) ?? await NetBiosAsync(ip) ?? await AvahiAsync(ip);
        _hostByIp[ip] = (Short(host), DateTime.UtcNow);
    }

    // Lấy tên ngắn (bỏ domain .lan/.local...) và loại nếu trùng IP.
    private static string? Short(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim().TrimEnd('.');
        if (System.Net.IPAddress.TryParse(name, out _)) return null;
        return name.Split('.')[0];
    }

    /// <summary>DNS ngược: hỏi router (gateway) trước, rồi resolver mặc định.</summary>
    private async Task<string?> DigAsync(string ip)
    {
        _gateway ??= await DetectGatewayAsync() ?? "";
        if (!string.IsNullOrEmpty(_gateway))
        {
            var viaGw = await RunAsync("dig", $"+short +time=1 +tries=1 -x {ip} @{_gateway}", 1500);
            var name = viaGw?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        var def = await RunAsync("dig", $"+short +time=1 +tries=1 -x {ip}", 1500);
        return def?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
    }

    private async Task<string?> DetectGatewayAsync()
    {
        var route = await RunAsync("ip", "route", 1000);
        var m = Regex.Match(route ?? "", @"default via (\S+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static readonly Regex NmbNameRegex = new(@"^\s*(\S+)\s+<00>\s+-\s+(?!<GROUP>)", RegexOptions.Multiline);
    private async Task<string?> NetBiosAsync(string ip)
    {
        var output = await RunAsync("nmblookup", $"-A {ip}", 1500);
        var m = NmbNameRegex.Match(output ?? "");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private async Task<string?> AvahiAsync(string ip)
    {
        // Output: "192.168.1.69\tMrLinhMint.local"
        var output = await RunAsync("avahi-resolve", $"-a {ip}", 1500);
        var parts = output?.Trim().Split('\t');
        return parts is { Length: >= 2 } ? parts[1].Trim() : null;
    }

    /// <summary>Chạy một tiến trình, đọc stdout, có timeout; trả null nếu lỗi/hết giờ.</summary>
    private async Task<string?> RunAsync(string file, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var outTask = proc.StandardOutput.ReadToEndAsync();
            if (await Task.WhenAny(outTask, Task.Delay(timeoutMs)) != outTask)
            {
                try { proc.Kill(true); } catch { }
                return null;
            }
            return await outTask;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lỗi chạy {File} {Args}", file, args);
            return null;
        }
    }
}

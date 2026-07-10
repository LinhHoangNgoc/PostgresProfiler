using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace PgMonitorApi.Hubs;

/// <summary>
/// Hub realtime đẩy danh sách query đang chạy tới client.
/// Yêu cầu JWT hợp lệ mới kết nối được. Client nhận qua sự kiện "ActiveQueries".
/// </summary>
[Authorize]
public class MonitorHub : Hub
{
    // Không cần method server->client ở đây; BackgroundService dùng IHubContext để broadcast.
}

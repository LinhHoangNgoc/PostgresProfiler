using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using PgMonitorApi.Hubs;
using PgMonitorApi.Models;
using PgMonitorApi.Services;

var builder = WebApplication.CreateBuilder(args);

// ----- Cấu hình dịch vụ (DI) -----
builder.Services.AddSingleton<QueryHistoryStore>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<PgLoginValidator>();
builder.Services.AddSingleton<TraceControlService>();
// Tra thông tin máy con (IP, phần mềm, tên máy) theo PID để ghép vào trace.
builder.Services.AddSingleton<ClientInfoService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClientInfoService>());
// Nguồn dữ liệu grid: đọc log PostgreSQL -> bắt được MỌI câu lệnh (kể cả query nhanh).
builder.Services.AddHostedService<PostgresLogTailService>();
// (Poller pg_stat_activity đã thay bằng đọc log; giữ file PostgresMonitorService.cs để tham khảo.)
builder.Services.AddSignalR();

// CORS: cho phép mọi origin (chỉ dùng nội bộ LAN). AllowCredentials cần cho SignalR.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

// ----- Xác thực JWT -----
var jwtKey = builder.Configuration["Jwt:Key"]
             ?? throw new InvalidOperationException("Thiếu cấu hình Jwt:Key trong appsettings.json");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "PgMonitorApi";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "PgMonitorApi";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        // SignalR JS client không gắn được header Authorization -> lấy token từ query string.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/monitor"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();  // phục vụ index.html mặc định
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// ----- Endpoints -----

// Đăng nhập: xác thực bằng chính user/pass của PostgreSQL, trả JWT nếu hợp lệ.
app.MapPost("/api/auth/login", async (
    LoginRequest req,
    PgLoginValidator validator,
    JwtTokenService jwt,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { message = "Thiếu username hoặc password" });

    var ok = await validator.ValidateAsync(req.Username, req.Password, ct);
    if (!ok)
        return Results.Unauthorized();

    var token = jwt.CreateToken(req.Username);
    return Results.Ok(new { token, expiresInHours = 8 });
});

// Lịch sử query chậm (yêu cầu JWT).
app.MapGet("/api/history", (
    QueryHistoryStore store,
    double? minDuration,
    int? limit) =>
{
    var min = minDuration ?? 0;
    var max = Math.Clamp(limit ?? 50, 1, 500);
    var items = store.Query(min, max);
    return Results.Ok(items);
}).RequireAuthorization();

// ----- Điều khiển trace (bật/tắt log câu lệnh của PostgreSQL) -----

// Bật trace: log mọi câu lệnh có duration >= thresholdMs (mặc định 0 = tất cả).
app.MapPost("/api/trace/start", async (
    TraceControlService trace,
    int? thresholdMs,
    CancellationToken ct) =>
{
    await trace.StartAsync(thresholdMs ?? 0, ct);
    return Results.Ok(new { running = true, thresholdMs = thresholdMs ?? 0 });
}).RequireAuthorization();

// Tắt trace.
app.MapPost("/api/trace/stop", async (TraceControlService trace, CancellationToken ct) =>
{
    await trace.StopAsync(ct);
    return Results.Ok(new { running = false });
}).RequireAuthorization();

// Trạng thái trace hiện tại.
app.MapGet("/api/trace/status", async (TraceControlService trace, CancellationToken ct) =>
{
    var ms = await trace.GetThresholdAsync(ct);
    return Results.Ok(new { running = ms >= 0, thresholdMs = ms });
}).RequireAuthorization();

// Hub realtime (yêu cầu JWT — đã đánh [Authorize] trên class).
app.MapHub<MonitorHub>("/hubs/monitor");

// Khi app dừng (graceful) thì tắt log câu lệnh để tránh để lại flood log trên PostgreSQL.
app.Lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        app.Services.GetRequiredService<TraceControlService>()
           .StopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
    catch { /* best-effort */ }
});

app.Run();

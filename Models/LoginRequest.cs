namespace PgMonitorApi.Models;

/// <summary>Body cho POST /api/auth/login.</summary>
public class LoginRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
}

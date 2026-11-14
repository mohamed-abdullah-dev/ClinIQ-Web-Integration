using System.Security.Claims;
using System.Text.Encodings.Web;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Domain.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ClinIQ.API.Infrastructure;

public sealed class SessionAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, ISessionService sessions, IWebHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ClinIQ.Session";
    public const string SessionItem = "ClinIQ.AuthenticatedSession";
    public static string AccessCookie(bool development) => development ? "ClinIQ.Access" : "__Host-ClinIQ.Access";
    public static string RefreshCookie(bool development) => development ? "ClinIQ.Refresh" : "__Secure-ClinIQ.Refresh";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? token;
        SessionClient client;
        if (Request.Headers.ContainsKey("Authorization"))
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.Fail("Invalid authentication header.");
            token = header[7..]; client = SessionClient.Mobile;
        }
        else
        {
            token = Request.Cookies[AccessCookie(environment.IsDevelopment())]; client = SessionClient.Web;
        }
        if (string.IsNullOrEmpty(token)) return AuthenticateResult.NoResult();
        var session = await sessions.AuthenticateAsync(token, client, Context.RequestAborted);
        if (session is null) return AuthenticateResult.Fail("Invalid or expired session.");
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.User.Id.ToString()),
            new(ClaimTypes.Name, session.User.FullName),
            new("session_id", session.SessionId.ToString()), new("client", session.Client.ToString())
        };
        claims.AddRange(session.User.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        Context.Items[SessionItem] = session;
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return WriteProblemAsync(401, "Auth.Unauthorized", "Please log in to continue.");
    }
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(403, "Auth.Forbidden", "You do not have permission to perform this action.");

    private async Task WriteProblemAsync(int status, string title, string detail)
    {
        Response.StatusCode = status;
        await Results.Problem(statusCode: status, title: title, detail: detail,
            extensions: new Dictionary<string, object?> { ["traceId"] = Context.TraceIdentifier }).ExecuteAsync(Context);
    }
}

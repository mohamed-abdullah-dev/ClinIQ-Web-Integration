using ClinIQ.API.Infrastructure;
using ClinIQ.Application.Features.Accounts;
using ClinIQ.Application.Features.Accounts.Commands.Sessions;
using ClinIQ.Domain.Accounts;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ClinIQ.API.Controllers;

[Route("api/auth")]
[EnableRateLimiting("auth-sessions")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SessionsController(ISender sender, IWebHostEnvironment environment) : ApiControllerBase
{
    [AllowAnonymous, HttpGet("web/csrf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Csrf([FromServices] IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new { tokens.RequestToken, HeaderName = "X-CSRF-TOKEN" });
    }

    [AllowAnonymous, HttpPost("web/login")]
    [ProducesResponseType<WebSessionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> WebLogin(LoginRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new LoginCommand(request.Email, request.Password, request.RememberMe, SessionClient.Web), ct);
        return result.Match(WebGrant, Problem);
    }
    [AllowAnonymous, HttpPost("mobile/login")]
    [ProducesResponseType<MobileSessionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MobileLogin(LoginRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new LoginCommand(request.Email, request.Password, request.RememberMe, SessionClient.Mobile), ct);
        return result.Match(grant => Ok(MobileGrant(grant)), Problem);
    }
    [AllowAnonymous, HttpPost("web/refresh")]
    [ProducesResponseType<WebSessionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> WebRefresh(CancellationToken ct)
    {
        var result = await sender.Send(new RefreshSessionCommand(RefreshCookieValue, SessionClient.Web), ct);
        return result.Match(WebGrant, Problem);
    }
    [AllowAnonymous, HttpPost("mobile/refresh")]
    [ProducesResponseType<MobileSessionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MobileRefresh(RefreshRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new RefreshSessionCommand(request.RefreshToken, SessionClient.Mobile), ct);
        return result.Match(grant => Ok(MobileGrant(grant)), Problem);
    }
    [AllowAnonymous, HttpPost("web/logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> WebLogout(CancellationToken ct)
    {
        var result = await sender.Send(new LogoutSessionCommand(RefreshCookieValue, SessionClient.Web), ct);
        return result.Match(_ =>
        {
            Response.Cookies.Delete(SessionAuthentication.AccessCookie(environment.IsDevelopment()), Cookie("/"));
            Response.Cookies.Delete(SessionAuthentication.RefreshCookie(environment.IsDevelopment()), Cookie("/api/auth/web"));
            return NoContent();
        }, Problem);
    }
    [AllowAnonymous, HttpPost("mobile/logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MobileLogout(RefreshRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new LogoutSessionCommand(request.RefreshToken, SessionClient.Mobile), ct);
        return result.Match(_ => NoContent(), Problem);
    }
    [Authorize, HttpGet("me")]
    public IActionResult Me()
    {
        var session = (AuthenticatedSession)HttpContext.Items[SessionAuthentication.SessionItem]!;
        return Ok(new { session.SessionId, Client = session.Client.ToString(), session.User });
    }

    private string RefreshCookieValue => Request.Cookies[SessionAuthentication.RefreshCookie(environment.IsDevelopment())] ?? "";
    private CookieOptions Cookie(string path) => new()
    {
        HttpOnly = true, Secure = !environment.IsDevelopment() || Request.IsHttps,
        SameSite = SameSiteMode.Strict, Path = path, IsEssential = true
    };
    private IActionResult WebGrant(SessionGrant grant)
    {
        var accessOptions = Cookie("/");
        var refreshOptions = Cookie("/api/auth/web");
        if (grant.RememberMe)
        {
            accessOptions.Expires = grant.AccessExpiresAtUtc;
            refreshOptions.Expires = grant.ExpiresAtUtc;
        }
        Response.Cookies.Append(SessionAuthentication.AccessCookie(environment.IsDevelopment()), grant.AccessToken, accessOptions);
        Response.Cookies.Append(SessionAuthentication.RefreshCookie(environment.IsDevelopment()), grant.RefreshToken, refreshOptions);
        return Ok(new WebSessionResponse(grant.SessionId, grant.User, grant.AccessExpiresAtUtc, grant.ExpiresAtUtc));
    }
    private static MobileSessionResponse MobileGrant(SessionGrant grant) => new(grant.SessionId, grant.User,
        grant.AccessToken, grant.RefreshToken, "Bearer", grant.AccessExpiresAtUtc, grant.ExpiresAtUtc);
}

public sealed record LoginRequest(string Email, string Password, bool RememberMe = false);
public sealed record RefreshRequest(string RefreshToken);
public sealed record WebSessionResponse(Guid SessionId, SessionUser User, DateTimeOffset AccessExpiresAtUtc, DateTimeOffset ExpiresAtUtc);
public sealed record MobileSessionResponse(Guid SessionId, SessionUser User, string AccessToken, string RefreshToken,
    string TokenType, DateTimeOffset AccessExpiresAtUtc, DateTimeOffset ExpiresAtUtc);

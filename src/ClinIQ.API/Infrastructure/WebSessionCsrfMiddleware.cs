using ClinIQ.Application.Features.Accounts;
using ClinIQ.Domain.Accounts;
using Microsoft.AspNetCore.Antiforgery;

namespace ClinIQ.API.Infrastructure;

public sealed class WebSessionCsrfMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        var unsafeMethod = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) &&
            !HttpMethods.IsOptions(context.Request.Method) && !HttpMethods.IsTrace(context.Request.Method);
        var webEndpoint = context.Request.Path.StartsWithSegments("/api/auth/web");
        var webIdentity = context.Items[SessionAuthentication.SessionItem] is AuthenticatedSession { Client: SessionClient.Web };
        if (unsafeMethod && (webEndpoint || webIdentity))
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException)
            {
                await Results.Problem(statusCode: 400, title: "Auth.InvalidCsrf",
                    detail: "Get a fresh CSRF token and retry the request.").ExecuteAsync(context);
                return;
            }
        }
        await next(context);
    }
}

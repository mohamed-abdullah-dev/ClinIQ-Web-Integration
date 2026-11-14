using ClinIQ.Domain.Common.Results;
using FluentValidation;

namespace ClinIQ.Application.Features.Accounts;

// An operator-only input, deliberately not a MediatR request or public registration contract.
public sealed record AdminBootstrapInput(string FullName, string Email, string Password)
{
    public override string ToString() => "AdminBootstrapInput { Credentials = [REDACTED] }";
}

public sealed class AdminBootstrapInputValidator : AbstractValidator<AdminBootstrapInput>
{
    public AdminBootstrapInputValidator()
    {
        RuleFor(input => input.FullName).NotEmpty().MaximumLength(200);
        RuleFor(input => input.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(input => input.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed record AdminBootstrapReceipt(Guid UserId, string EmailDeliveryStatus);

public static class AdminBootstrapErrors
{
    public static readonly Error DevelopmentOnly = Error.Forbidden("AdminBootstrap.DevelopmentOnly",
        "First-admin bootstrap is available only in Development.");
    public static readonly Error AlreadyInitialized = Error.Conflict("AdminBootstrap.AlreadyInitialized",
        "The Admin role already exists. Bootstrap cannot create another admin or repair an existing account.");
    public static readonly Error MigrationsPending = Error.Conflict("AdminBootstrap.MigrationsPending",
        "Review and apply pending database migrations before bootstrap.");
    public static readonly Error Busy = Error.Conflict("AdminBootstrap.Busy", "Admin bootstrap is busy. Please retry.");
}

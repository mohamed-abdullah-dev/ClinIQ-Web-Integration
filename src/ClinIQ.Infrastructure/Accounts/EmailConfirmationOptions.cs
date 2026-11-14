namespace ClinIQ.Infrastructure.Accounts;

public sealed class EmailConfirmationOptions
{
    public string? Url { get; set; }

    public static bool IsValidUrl(string? value) =>
        value is null || (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0);
}

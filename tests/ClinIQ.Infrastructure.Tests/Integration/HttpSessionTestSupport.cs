using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ClinIQ.Infrastructure.Tests.Integration;

internal static class HttpSessionTestSupport
{
    internal static async Task WebLoginAsync(HttpClient client, string email, string password = DoctorReviewFixture.Password)
    {
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await CsrfAsync(client));
        using var response = await client.PostAsJsonAsync("/api/auth/web/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await CsrfAsync(client));
    }
    internal static async Task<string> CsrfAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/web/csrf"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("requestToken").GetString()!;
    }
}

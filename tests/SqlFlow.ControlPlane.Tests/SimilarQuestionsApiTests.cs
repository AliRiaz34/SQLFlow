using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The similar-questions endpoint's contract at the HTTP boundary. The ranking itself is covered against a
/// real catalog by <see cref="QuestionSearchTests"/>; what matters here is that a deployment WITHOUT retrieval
/// configured says so distinctly, rather than returning an empty result a caller would read as "nothing is
/// close to your question". Those two answers demand opposite follow-ups, so conflating them would have an
/// assistant tell someone their estate has no matching dashboard when in truth it never looked.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SimilarQuestionsApiTests
{
    [SkippableFact]
    public async Task WhenRetrievalIsDisabled_SaysNotConfigured_RatherThanReturningNoMatches()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        // Retrieval is off in the shipped defaults, which registers no embedding provider at all.
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueReadTokenAsync(client);

        using var response = await SendAsync(
            client, token, "/api/v1/lineage/subscribers/similar-questions?question=what%20is%20our%20revenue");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("PowerAI:Retrieval:Enabled", body, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AnEmptyQuestion_IsRejected()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueReadTokenAsync(client);

        using var response = await SendAsync(
            client, token, "/api/v1/lineage/subscribers/similar-questions?question=%20%20");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<string> IssueReadTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string relativeUri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}

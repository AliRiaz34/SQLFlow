using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The "AI shouldn't be able to read everything" surface end to end: an admin marks one column sensitive via
/// <c>/powerai/column-policies</c>, and that single decision is proven to take effect in every place PowerAI (or
/// any ad-hoc caller) could otherwise learn about or read the column - schema search, the object dossier/column
/// list, and the DataOps prepare step, including a <c>SELECT *</c> that would otherwise smuggle it through
/// unnamed. Every seeded row is removed in a finally so repeated runs stay isolated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ColumnPolicyApiTests
{
    [SkippableFact]
    public async Task RestrictingAColumn_HidesItFromSchemaSurfaces_AndRefusesQueriesThatWouldReadIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var serverRef = "${env:SQLFLOW_COLPOL_" + suffix + "}";
        var objectKey = $"{serverRef}|dw|dbo|employee_{suffix}";
        var tableName = "Employee_" + suffix;
        var repoId = Guid.NewGuid();
        var pipelineId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:DataOps:Enabled", "true");
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Objects.Add(new CatalogObject
                {
                    Key = objectKey, ServerRef = serverRef, Database = "dw", Schema = "dbo", Name = tableName,
                    Kind = "Table", FirstSeenUtc = now, LastSeenUtc = now,
                });
                db.ObjectColumns.Add(new CatalogObjectColumn
                {
                    ObjectKey = objectKey, Ordinal = 1, Name = "Ssn", DataType = "varchar(11)", Nullable = false, Tier = "Observed",
                });
                db.ObjectColumns.Add(new CatalogObjectColumn
                {
                    ObjectKey = objectKey, Ordinal = 2, Name = "Name", DataType = "nvarchar(100)", Nullable = false, Tier = "Observed",
                });

                db.Repos.Add(new CatalogRepo { Id = repoId, Name = "colpol_" + suffix, FirstSeenUtc = now, LastSyncUtc = now });
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = pipelineId, RepoId = repoId, Name = "colpol_flow_" + suffix, Kind = "ing",
                    RelativePath = "colpol/flow.yaml", Active = true, SourceServer = serverRef, TargetServer = serverRef,
                    DefinitionJson = "{}", FirstSeenUtc = now, LastSeenUtc = now,
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var adminToken = await IssueTokenAsync(client, ["read", "operate", "admin"]);

            // 1. Before any policy exists, the column is a normal, visible column: search finds it, and the
            //    dossier's column list carries it.
            var beforeColumns = await GetJsonAsync<PagedResult<ColumnHitDto>>(
                client, adminToken, $"/api/v1/search/columns?name={Uri.EscapeDataString("Ssn")}");
            Assert.Contains(beforeColumns.Items, c => c.ObjectKey == objectKey && c.ColumnName == "Ssn");

            // 2. An admin restricts it.
            using var setResponse = await PutAsync(client, adminToken, "/api/v1/powerai/column-policies",
                new SetColumnPolicyRequest(objectKey, "Ssn", IsSensitive: true, Reason: "PII"));
            setResponse.EnsureSuccessStatusCode();
            var setResult = await setResponse.Content.ReadFromJsonAsync<ColumnPolicyStateDto>();
            Assert.NotNull(setResult);
            Assert.True(setResult.IsSensitive);
            Assert.Equal("PII", setResult.Reason);

            // 3. The admin state view shows both columns, Ssn flagged and Name not.
            var state = await GetJsonAsync<IReadOnlyList<ColumnPolicyStateDto>>(
                client, adminToken, $"/api/v1/powerai/column-policies/objects/{Uri.EscapeDataString(objectKey)}");
            Assert.True(state.Single(c => c.ColumnName == "Ssn").IsSensitive);
            Assert.False(state.Single(c => c.ColumnName == "Name").IsSensitive);

            // 4. The catalog-wide restricted-columns overview lists it.
            var overview = await GetJsonAsync<PagedResult<RestrictedColumnDto>>(
                client, adminToken, "/api/v1/powerai/column-policies?pageSize=200");
            Assert.Contains(overview.Items, c => c.ObjectKey == objectKey && c.ColumnName == "Ssn");

            // 5. It is now invisible to schema search...
            var afterColumns = await GetJsonAsync<PagedResult<ColumnHitDto>>(
                client, adminToken, $"/api/v1/search/columns?name={Uri.EscapeDataString("Ssn")}");
            Assert.DoesNotContain(afterColumns.Items, c => c.ObjectKey == objectKey);

            // ...and to the object's paged column list and dossier, while the untouched column still shows.
            var pagedColumns = await GetJsonAsync<PagedResult<ObjectColumnDto>>(
                client, adminToken, $"/api/v1/lineage/objects/columns?key={Uri.EscapeDataString(objectKey)}");
            Assert.DoesNotContain(pagedColumns.Items, c => c.Name == "Ssn");
            Assert.Contains(pagedColumns.Items, c => c.Name == "Name");

            var dossier = await GetJsonAsync<ObjectDossierDto>(
                client, adminToken, $"/api/v1/lineage/objects/dossier?key={Uri.EscapeDataString(objectKey)}");
            Assert.DoesNotContain(dossier.Columns, c => c.Name == "Ssn");
            Assert.Contains(dossier.Columns, c => c.Name == "Name");

            // 6. A query naming the restricted column directly is refused at prepare...
            using var directRefusal = await PostAsync(client, adminToken, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest($"SELECT Ssn FROM dbo.{tableName}", serverRef, Database: "dw"));
            Assert.Equal(HttpStatusCode.BadRequest, directRefusal.StatusCode);

            // ...so is a SELECT * that would expose it unnamed...
            using var starRefusal = await PostAsync(client, adminToken, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest($"SELECT * FROM dbo.{tableName}", serverRef, Database: "dw"));
            Assert.Equal(HttpStatusCode.BadRequest, starRefusal.StatusCode);

            // ...but a query that reads only the untouched column still prepares normally.
            using var allowed = await PostAsync(client, adminToken, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest($"SELECT Name FROM dbo.{tableName}", serverRef, Database: "dw"));
            allowed.EnsureSuccessStatusCode();

            // 7. Clearing the restriction reverses all of it.
            using var clearResponse = await PutAsync(client, adminToken, "/api/v1/powerai/column-policies",
                new SetColumnPolicyRequest(objectKey, "Ssn", IsSensitive: false, Reason: null));
            clearResponse.EnsureSuccessStatusCode();

            using var clearedAllowed = await PostAsync(client, adminToken, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest($"SELECT Ssn FROM dbo.{tableName}", serverRef, Database: "dw"));
            clearedAllowed.EnsureSuccessStatusCode();
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.QueryPlans.Where(p => p.SourceRef == serverRef).ExecuteDeleteAsync();
            await db.ColumnPolicies.Where(p => p.ObjectKey == objectKey).ExecuteDeleteAsync();
            await db.ObjectColumns.Where(c => c.ObjectKey == objectKey).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.Key == objectKey).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static async Task<string> IssueTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }

    private static async Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string token, string relativeUri, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(relativeUri, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PutAsync<T>(HttpClient client, string token, string relativeUri, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, new Uri(relativeUri, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}

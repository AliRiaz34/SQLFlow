using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The semantic layer end to end: the column allow-list decides which tables and columns exist on the assistant's
/// schema surface, and every annotation layered on top (a curated key or join, a measure, an example query) is
/// refused when written over a column outside the layer and withheld when served once a column it names is denied.
/// The denied columns' names must never appear anywhere in what an assistant is served. Every seeded row is removed
/// in a finally so repeated runs stay isolated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SemanticLayerApiTests
{
    [SkippableFact]
    public async Task TheLayerServesOnlyAllowListedSchema_AndRefusesAnnotationsOverAnythingElse()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var serverRef = "${env:SQLFLOW_SEMLAYER_" + suffix + "}";
        var tripsName = "Trips_" + suffix;
        var stationName = "Station_" + suffix;
        var tripsKey = $"{serverRef}|dw|dbo|{tripsName.ToLowerInvariant()}";
        var stationKey = $"{serverRef}|dw|dbo|{stationName.ToLowerInvariant()}";
        var repoId = Guid.NewGuid();
        var measureName = "net_amount_" + suffix;
        var blockedExampleSql = $"SELECT RiderSsn_{suffix} FROM dbo.{tripsName}";
        var servableExampleSql = $"SELECT SUM(Amount) AS total FROM dbo.{tripsName}";
        var now = DateTime.UtcNow;

        // The two denied columns carry the suffix so their names cannot occur by accident in a served payload.
        var riderSsn = "RiderSsn_" + suffix;
        var stationSecret = "Secret_" + suffix;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Objects.Add(new CatalogObject
                {
                    Key = tripsKey, ServerRef = serverRef, Database = "dw", Schema = "dbo", Name = tripsName,
                    Kind = "Table", KeyColumns = riderSsn, KeyOrigin = "Declared", FirstSeenUtc = now, LastSeenUtc = now,
                });
                db.Objects.Add(new CatalogObject
                {
                    Key = stationKey, ServerRef = serverRef, Database = "dw", Schema = "dbo", Name = stationName,
                    Kind = "Table", FirstSeenUtc = now, LastSeenUtc = now,
                });
                AddColumn(db, tripsKey, 1, "TripId", "bigint");
                AddColumn(db, tripsKey, 2, "StationId", "int");
                AddColumn(db, tripsKey, 3, "Amount", "decimal(18,2)");
                AddColumn(db, tripsKey, 4, riderSsn, "varchar(11)");
                AddColumn(db, stationKey, 1, "StationId", "int");
                AddColumn(db, stationKey, 2, "StationName", "nvarchar(100)");
                AddColumn(db, stationKey, 3, stationSecret, "nvarchar(100)");

                // Two joins the codebase exhibits: one on allowed columns, one on the two columns that stay denied.
                db.ObjectRelationships.Add(new CatalogObjectRelationship
                {
                    RepoId = repoId, FromObjectKey = tripsKey, FromColumns = "StationId", ToObjectKey = stationKey,
                    ToColumns = "StationId", Operators = string.Empty, JoinTypes = "Inner", Origin = "Join",
                    Tier = "Observed", Occurrences = 4,
                });
                db.ObjectRelationships.Add(new CatalogObjectRelationship
                {
                    RepoId = repoId, FromObjectKey = tripsKey, FromColumns = riderSsn, ToObjectKey = stationKey,
                    ToColumns = stationSecret, Operators = string.Empty, JoinTypes = "Left", Origin = "Join",
                    Tier = "Observed", Occurrences = 1,
                });

                db.QuestionExamples.Add(Example($"who rode {suffix}", blockedExampleSql, tripsKey, now));
                db.QuestionExamples.Add(Example($"total amount {suffix}", servableExampleSql, tripsKey, now.AddMinutes(-1)));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var adminToken = await IssueTokenAsync(client, ["read", "operate", "admin"]);

            // 1. Nothing is allowed yet: neither table exists on the assistant's surface.
            using (var notInLayer = await SendAsync(client, adminToken, HttpMethod.Get,
                $"/api/v1/semantic-layer/tables/describe?key={Uri.EscapeDataString(tripsKey)}"))
            {
                Assert.Equal(HttpStatusCode.NotFound, notInLayer.StatusCode);
            }

            var emptySearch = await GetJsonAsync<SemanticSearchDto>(client, adminToken, $"/api/v1/semantic-layer/search?q={tripsName}");
            Assert.DoesNotContain(emptySearch.Tables.Items, h => h.Table.Key == tripsKey);

            // 2. Allow three of Trips' columns, one with a business description and synonym; allow the whole Station
            //    table in one call, then deny its secret column again.
            await SetColumnAsync(client, adminToken, new SetColumnPolicyRequest(tripsKey, "TripId", true, null));
            await SetColumnAsync(client, adminToken, new SetColumnPolicyRequest(tripsKey, "StationId", true, null));
            await SetColumnAsync(client, adminToken, new SetColumnPolicyRequest(
                tripsKey, "Amount", true, null, Description: "Fare paid, NOK", Synonyms: ["turnover" + suffix, "fare"]));

            using (var bulk = await SendAsync(client, adminToken, HttpMethod.Put, "/api/v1/powerai/column-policies/objects",
                new SetObjectColumnPoliciesRequest(stationKey, IsAllowed: true)))
            {
                bulk.EnsureSuccessStatusCode();
                var state = await bulk.Content.ReadFromJsonAsync<List<ColumnPolicyStateDto>>();
                Assert.NotNull(state);
                Assert.All(state, c => Assert.True(c.IsAllowed));
            }

            await SetColumnAsync(client, adminToken, new SetColumnPolicyRequest(stationKey, stationSecret, false, "internal"));

            // 3. A business synonym finds the table through its column.
            var synonymSearch = await GetJsonAsync<SemanticSearchDto>(
                client, adminToken, $"/api/v1/semantic-layer/search?q={Uri.EscapeDataString("turnover" + suffix)}");
            var hit = Assert.Single(synonymSearch.Tables.Items, h => h.Table.Key == tripsKey);
            Assert.Contains(hit.MatchedColumns, c => c.Name == "Amount");
            Assert.Contains("columns", hit.MatchedOn);

            // 4. Annotations over a denied column are refused; over allowed ones they are stored.
            await AssertRefusedAsync(client, adminToken, HttpMethod.Put, "/api/v1/powerai/semantic-layer/objects/annotation",
                new SetSemanticAnnotationRequest(tripsKey, "Bike trips", null, null, [riderSsn]));
            using (var annotation = await SendAsync(client, adminToken, HttpMethod.Put, "/api/v1/powerai/semantic-layer/objects/annotation",
                new SetSemanticAnnotationRequest(tripsKey, "Bike trips", "One row per completed trip.", ["rides"], ["tripid"])))
            {
                annotation.EnsureSuccessStatusCode();
            }

            await AssertRefusedAsync(client, adminToken, HttpMethod.Post, "/api/v1/powerai/semantic-layer/measures",
                new UpsertSemanticMeasureRequest(measureName, tripsKey, $"COUNT(DISTINCT {riderSsn})", null));
            await AssertRefusedAsync(client, adminToken, HttpMethod.Post, "/api/v1/powerai/semantic-layer/measures",
                new UpsertSemanticMeasureRequest(measureName, tripsKey, $"SUM(Amount) FROM dbo.{stationName} --", null));
            await AssertRefusedAsync(client, adminToken, HttpMethod.Post, "/api/v1/powerai/semantic-layer/measures",
                new UpsertSemanticMeasureRequest(measureName, tripsKey, $"(SELECT MAX(StationId) FROM dbo.{stationName})", null));
            using (var measure = await SendAsync(client, adminToken, HttpMethod.Post, "/api/v1/powerai/semantic-layer/measures",
                new UpsertSemanticMeasureRequest(measureName, tripsKey, "SUM(Amount)", "Total fare")))
            {
                measure.EnsureSuccessStatusCode();
            }

            using (var duplicateMeasure = await SendAsync(client, adminToken, HttpMethod.Post, "/api/v1/powerai/semantic-layer/measures",
                new UpsertSemanticMeasureRequest(measureName, tripsKey, "COUNT(TripId)", null)))
            {
                Assert.Equal(HttpStatusCode.Conflict, duplicateMeasure.StatusCode);
            }

            await AssertRefusedAsync(client, adminToken, HttpMethod.Post, "/api/v1/powerai/semantic-layer/relationships",
                new UpsertSemanticJoinRequest(tripsKey, [riderSsn], stationKey, [stationSecret], "Left", null));
            using (var join = await SendAsync(client, adminToken, HttpMethod.Post, "/api/v1/powerai/semantic-layer/relationships",
                new UpsertSemanticJoinRequest(tripsKey, ["StationId"], stationKey, ["StationId"], "left", "Trips to their start station")))
            {
                join.EnsureSuccessStatusCode();
            }

            using (var duplicateJoin = await SendAsync(client, adminToken, HttpMethod.Post, "/api/v1/powerai/semantic-layer/relationships",
                new UpsertSemanticJoinRequest(tripsKey, ["stationid"], stationKey, ["STATIONID"], "Inner", null)))
            {
                Assert.Equal(HttpStatusCode.Conflict, duplicateJoin.StatusCode);
            }

            // 5. The served bundle carries only the layer: allowed columns, the curated key, the curated join (the
            //    discovered twin is not repeated, the join on denied columns is withheld), the measure, and only the
            //    example whose SQL passes the guards. No denied column name appears anywhere in the payload.
            var tableJson = await GetStringAsync(
                client, adminToken, $"/api/v1/semantic-layer/tables/describe?key={Uri.EscapeDataString(tripsKey)}");
            Assert.DoesNotContain(riderSsn, tableJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(stationSecret, tableJson, StringComparison.OrdinalIgnoreCase);

            var table = await GetJsonAsync<SemanticTableDto>(
                client, adminToken, $"/api/v1/semantic-layer/tables/describe?key={Uri.EscapeDataString(tripsKey)}");
            Assert.Equal(["TripId", "StationId", "Amount"], table.Columns.Select(c => c.Name));
            Assert.Equal("Bike trips", table.BusinessName);
            Assert.Equal(["TripId"], table.KeyColumns);
            Assert.Equal(SemanticLayer.CuratedOrigin, table.KeyOrigin);
            var servedJoin = Assert.Single(table.Joins);
            Assert.Equal(SemanticLayer.CuratedOrigin, servedJoin.Source);
            Assert.Equal(["Left"], servedJoin.JoinTypes);
            Assert.Contains(table.Measures, m => m.Name == measureName);
            var example = Assert.Single(table.Examples);
            Assert.Equal(servableExampleSql, example.Sql);

            // The admin view shows what is withheld and why.
            var adminObject = await GetJsonAsync<SemanticObjectAdminDto>(
                client, adminToken, $"/api/v1/powerai/semantic-layer/objects/detail?key={Uri.EscapeDataString(tripsKey)}");
            Assert.Equal(4, adminObject.Columns.Count);
            Assert.Equal(2, adminObject.DiscoveredJoins.Count);
            Assert.All(adminObject.DiscoveredJoins, j => Assert.NotNull(j.Problem));
            Assert.Contains(adminObject.Examples, e => e.Sql == blockedExampleSql && e.Problem is not null);

            // 6. Denying a column a measure reads withdraws the measure from the layer; the admin view says why.
            await SetColumnAsync(client, adminToken, new SetColumnPolicyRequest(tripsKey, "Amount", false, null));
            var overview = await GetJsonAsync<SemanticOverviewDto>(client, adminToken, "/api/v1/semantic-layer");
            Assert.DoesNotContain(overview.Measures, m => m.Name == measureName);
            var measures = await GetJsonAsync<List<SemanticMeasureAdminDto>>(client, adminToken, "/api/v1/powerai/semantic-layer/measures");
            Assert.NotNull(Assert.Single(measures, m => m.Name == measureName).Problem);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.SemanticMeasures.Where(m => m.ObjectKey == tripsKey).ExecuteDeleteAsync();
            await db.SemanticRelationships.Where(r => r.FromObjectKey == tripsKey || r.ToObjectKey == tripsKey).ExecuteDeleteAsync();
            await db.SemanticObjects.Where(s => s.ObjectKey == tripsKey || s.ObjectKey == stationKey).ExecuteDeleteAsync();
            await db.QuestionExamples.Where(e => e.ObjectKeys == tripsKey).ExecuteDeleteAsync();
            await db.ObjectRelationships.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.ColumnPolicies.Where(p => p.ObjectKey == tripsKey || p.ObjectKey == stationKey).ExecuteDeleteAsync();
            await db.ObjectColumns.Where(c => c.ObjectKey == tripsKey || c.ObjectKey == stationKey).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.Key == tripsKey || o.Key == stationKey).ExecuteDeleteAsync();
        }
    }

    private static void AddColumn(CatalogDbContext db, string objectKey, int ordinal, string name, string dataType)
        => db.ObjectColumns.Add(new CatalogObjectColumn
        {
            ObjectKey = objectKey, Ordinal = ordinal, Name = name, DataType = dataType, Nullable = false, Tier = "Observed",
        });

    private static CatalogQuestionExample Example(string question, string sql, string objectKey, DateTime confirmedUtc)
        => new()
        {
            Question = question, Sql = sql, ObjectKeys = objectKey, Provenance = QuestionExampleProvenance.UserConfirmed,
            ConfirmedUtc = confirmedUtc, ContentHash = QuestionExampleHash.Compute(question, sql),
        };

    private static async Task SetColumnAsync(HttpClient client, string token, SetColumnPolicyRequest request)
    {
        using var response = await SendAsync(client, token, HttpMethod.Put, "/api/v1/powerai/column-policies", request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task AssertRefusedAsync<T>(HttpClient client, string token, HttpMethod method, string relativeUri, T body)
    {
        using var response = await SendAsync(client, token, method, relativeUri, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

    private static async Task<string> GetStringAsync(HttpClient client, string token, string relativeUri)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, relativeUri);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, relativeUri);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string relativeUri)
        => SendAsync<object?>(client, token, method, relativeUri, null);

    private static async Task<HttpResponseMessage> SendAsync<T>(
        HttpClient client, string token, HttpMethod method, string relativeUri, T body)
    {
        using var request = new HttpRequestMessage(method, new Uri(relativeUri, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}

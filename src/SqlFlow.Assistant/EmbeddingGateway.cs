using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Logging;
using SqlFlow.Azure;

namespace SqlFlow.Assistant;

/// <summary>
/// The one embeddings client, serving both <see cref="EmbeddingProviderKind.OpenAI"/> (api.openai.com) and
/// <see cref="EmbeddingProviderKind.AzureFoundry"/> (an Azure OpenAI embeddings deployment, authenticated with
/// the Azure credential). Both speak the same request and response wire format, so like
/// <see cref="ResponsesApiGateway"/> they differ only in endpoint and credential rather than needing two
/// implementations.
/// </summary>
public sealed class EmbeddingGateway : IEmbeddingProvider, IDisposable
{
    private static readonly string[] AzureScopes = ["https://cognitiveservices.azure.com/.default"];

    /// <summary>How many times a throttled (429) or briefly-unavailable (503) request is retried before it
    /// surfaces as an error, honoring the server's Retry-After or an exponential backoff. Matches
    /// <see cref="ResponsesApiGateway"/>'s policy: a sync embedding many questions at once is exactly the
    /// burst shape that trips a per-minute rate limit.</summary>
    private const int MaxThrottleRetries = 4;

    private readonly HttpClient _http;
    private readonly TokenCredential? _credential;
    private readonly string? _apiKey;
    private readonly Uri _endpoint;
    private readonly string _providerName;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<EmbeddingGateway> _logger;

    public EmbeddingGateway(
        EmbeddingOptions options,
        IAzureCredentialFactory credentialFactory,
        ILogger<EmbeddingGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentialFactory);
        _options = options;
        _logger = logger;

        switch (options.Provider)
        {
            case EmbeddingProviderKind.OpenAI:
                _credential = null;
                _apiKey = options.ApiKey;
                _endpoint = new Uri(options.BaseUrl.TrimEnd('/') + "/v1/embeddings");
                _providerName = "OpenAI";
                break;
            case EmbeddingProviderKind.AzureFoundry:
                _credential = credentialFactory.Create();
                _apiKey = null;
                _endpoint = BuildAzureEmbeddingsEndpoint(options.Endpoint, options.Model);
                _providerName = "AzureFoundry";
                break;
            default:
                throw new InvalidOperationException(
                    $"'{options.Provider}' is not a supported embedding provider (OpenAI, AzureFoundry).");
        }

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
    }

    /// <inheritdoc />
    public string Model => _options.Model;

    /// <inheritdoc />
    public int Dimensions => _options.Dimensions;

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
        {
            return [];
        }
        if (texts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("An embedding input must not be empty or whitespace.", nameof(texts));
        }

        var vectors = new List<float[]>(texts.Count);
        // The API caps inputs per request, and a sync can present far more questions than one call accepts,
        // so the batch is chunked here rather than pushed onto every caller.
        for (var offset = 0; offset < texts.Count; offset += _options.BatchSize)
        {
            var chunk = texts.Skip(offset).Take(_options.BatchSize).ToList();
            vectors.AddRange(await EmbedChunkAsync(chunk, ct).ConfigureAwait(false));
        }
        return vectors;
    }

    private async Task<IReadOnlyList<float[]>> EmbedChunkAsync(IReadOnlyList<string> chunk, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["input"] = new JsonArray([.. chunk.Select(t => (JsonNode)JsonValue.Create(t)!)]),
        };
        // Azure names the model by the deployment in the URL path; OpenAI names it in the body.
        if (_options.Provider == EmbeddingProviderKind.OpenAI)
        {
            payload["model"] = _options.Model;
            // text-embedding-3-* accept a shortened output vector. Sending it explicitly keeps the stored
            // width equal to the configured Dimensions rather than the model's native default.
            payload["dimensions"] = _options.Dimensions;
        }

        var body = payload.ToJsonString();
        using var response = await SendAsync(body, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return ParseVectors(json, chunk.Count);
    }

    /// <summary>Reads the <c>data[].embedding</c> vectors back, re-sorted by the <c>index</c> each carries:
    /// the API documents the order as unspecified, so relying on array position would silently pair a
    /// question with another question's vector.</summary>
    private IReadOnlyList<float[]> ParseVectors(string json, int expectedCount)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json)?.AsObject()
                ?? throw new InvalidOperationException($"{_providerName} embeddings response was not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{_providerName} embeddings response was not valid JSON: {Truncate(json)}", ex);
        }

        if (root["data"] is not JsonArray data)
        {
            throw new InvalidOperationException(
                $"{_providerName} embeddings response carried no data array: {Truncate(json)}");
        }
        if (data.Count != expectedCount)
        {
            throw new InvalidOperationException(
                $"{_providerName} returned {data.Count} embeddings for {expectedCount} inputs.");
        }

        var vectors = new float[expectedCount][];
        foreach (var item in data)
        {
            if (item is not JsonObject entry || entry["embedding"] is not JsonArray values)
            {
                throw new InvalidOperationException(
                    $"{_providerName} embeddings response carried an entry with no embedding: {Truncate(json)}");
            }

            var index = (int?)entry["index"]
                ?? throw new InvalidOperationException(
                    $"{_providerName} embeddings response carried an entry with no index: {Truncate(json)}");
            if (index < 0 || index >= expectedCount || vectors[index] is not null)
            {
                throw new InvalidOperationException(
                    $"{_providerName} embeddings response carried an out-of-range or duplicate index {index}.");
            }
            if (values.Count != _options.Dimensions)
            {
                throw new InvalidOperationException(
                    $"{_providerName} returned a {values.Count}-dimension embedding but "
                    + $"{_options.Model} is configured for {_options.Dimensions}. Correct "
                    + "Retrieval:Embedding:Dimensions to match the model.");
            }

            var vector = new float[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                vector[i] = (float?)values[i]
                    ?? throw new InvalidOperationException(
                        $"{_providerName} embeddings response carried a non-numeric vector component.");
            }
            vectors[index] = vector;
        }

        return vectors;
    }

    /// <summary>Sends one embeddings request, resolving throttle retries (429/503) before returning.</summary>
    private async Task<HttpResponseMessage> SendAsync(string body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (_credential is not null)
            {
                var token = await _credential.GetTokenAsync(new TokenRequestContext(AzureScopes), ct).ConfigureAwait(false);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using var failed = response;
            var payload = await failed.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if ((failed.StatusCode == HttpStatusCode.TooManyRequests || failed.StatusCode == HttpStatusCode.ServiceUnavailable)
                && attempt < MaxThrottleRetries)
            {
                var delay = RetryDelay(failed, attempt);
                _logger.LogWarning(
                    "{Provider} embeddings throttled ({Status}) on attempt {Attempt}/{Max}; retrying in {Delay:0.#}s",
                    _providerName, (int)failed.StatusCode, attempt + 1, MaxThrottleRetries, delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            throw new InvalidOperationException(
                $"{_providerName} embeddings API returned {(int)failed.StatusCode} {failed.ReasonPhrase}: {Truncate(payload)}");
        }
    }

    /// <summary>Builds the Azure OpenAI embeddings URL for a deployment, accepting either the account endpoint
    /// (<c>https://account.openai.azure.com</c>) or a full path already naming the deployment.</summary>
    private static Uri BuildAzureEmbeddingsEndpoint(string endpoint, string deployment)
    {
        var trimmed = endpoint.TrimEnd('/');
        if (trimmed.Contains("/embeddings", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmed);
        }
        return new Uri($"{trimmed}/openai/deployments/{Uri.EscapeDataString(deployment)}"
            + "/embeddings?api-version=2024-10-21");
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }
        if (retryAfter?.Date is { } date && date - DateTimeOffset.UtcNow is { } until && until > TimeSpan.Zero)
        {
            return until;
        }
        return TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, attempt)));
    }

    private static string Truncate(string value)
        => value.Length <= 600 ? value : value[..600] + "...";

    public void Dispose() => _http.Dispose();
}

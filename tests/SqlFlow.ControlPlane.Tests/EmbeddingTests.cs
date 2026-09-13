using SqlFlow.Assistant;
using SqlFlow.ControlPlane.Configuration;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The arithmetic and encoding retrieval rests on (POWERAI.md Section 6: confidence is retrieval similarity,
/// never an LLM's self-rating). Both halves matter because they sit on opposite sides of persistence: a vector
/// is packed to bytes at sync time and ranked after being read back, so an encoding that does not round-trip
/// exactly would corrupt every similarity score silently rather than failing.
/// </summary>
public sealed class EmbeddingMathTests
{
    [Fact]
    public void CosineSimilarity_RanksAParaphraseAboveAnUnrelatedQuestion()
    {
        // Stand-ins for three embeddings: the query, a near-paraphrase of it, and something unrelated.
        float[] query = [1f, 0.9f, 0.1f, 0f];
        float[] paraphrase = [0.95f, 1f, 0.05f, 0.1f];
        float[] unrelated = [0f, 0.1f, 1f, 0.9f];

        var near = EmbeddingMath.CosineSimilarity(query, paraphrase);
        var far = EmbeddingMath.CosineSimilarity(query, unrelated);

        Assert.True(near > far, $"paraphrase ({near}) should rank above unrelated ({far})");
        Assert.InRange(near, 0.9, 1.0);
        Assert.InRange(far, -1.0, 0.5);
    }

    [Fact]
    public void CosineSimilarity_OfAVectorWithItself_IsOne()
    {
        float[] vector = [0.2f, -0.7f, 0.4f, 0.55f];
        Assert.Equal(1.0, EmbeddingMath.CosineSimilarity(vector, vector), 6);
    }

    [Fact]
    public void CosineSimilarity_OfAZeroVector_IsZeroRatherThanNaN()
    {
        float[] zero = [0f, 0f, 0f];
        float[] other = [1f, 2f, 3f];

        // A degenerate embedding must read as "no similarity", not divide by zero into NaN, which would
        // otherwise poison every comparison it takes part in.
        Assert.Equal(0, EmbeddingMath.CosineSimilarity(zero, other));
    }

    [Fact]
    public void CosineSimilarity_AcrossDifferentDimensions_ThrowsRatherThanComparing()
    {
        // Two vectors of different widths came from different models; comparing them is meaningless, so this
        // is the tripwire behind the EmbeddingModel column that gates re-embedding.
        float[] small = [1f, 0f];
        float[] large = [1f, 0f, 0f];

        Assert.Throws<ArgumentException>(() => EmbeddingMath.CosineSimilarity(small, large));
    }

    [Fact]
    public void ToBytes_RoundTripsExactly()
    {
        float[] vector = [0.1f, -0.25f, 1e-7f, 12345.678f, 0f];

        var restored = EmbeddingMath.FromBytes(EmbeddingMath.ToBytes(vector));

        Assert.Equal(vector, restored);
        Assert.Equal(vector.Length * sizeof(float), EmbeddingMath.ToBytes(vector).Length);
    }

    [Fact]
    public void FromBytes_OnATruncatedValue_ThrowsRatherThanReturningAShortVector()
    {
        var bytes = EmbeddingMath.ToBytes([1f, 2f, 3f]);

        Assert.Throws<ArgumentException>(() => EmbeddingMath.FromBytes(bytes.AsSpan(0, bytes.Length - 1)));
    }
}

/// <summary>
/// The configuration contract for retrieval: it is off by default, and turning it on without the credential
/// its chosen provider needs is a startup error naming the exact key, rather than a deployment that starts and
/// fails on the first question asked.
/// </summary>
public sealed class RetrievalOptionsTests
{
    [Fact]
    public void Disabled_ByDefault_AndValidatesWithNoConfiguration()
    {
        var options = new RetrievalOptions();

        Assert.False(options.Enabled);
        options.Validate();
    }

    [Fact]
    public void Enabled_WithoutAnOpenAIKey_FailsNamingTheKey()
    {
        var options = new RetrievalOptions { Enabled = true };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("ControlPlane:PowerAI:Retrieval:Embedding:ApiKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_WithAnOpenAIKey_Validates()
    {
        var options = new RetrievalOptions
        {
            Enabled = true,
            Embedding = new EmbeddingOptions { ApiKey = "sk-test" },
        };

        options.Validate();
    }

    [Fact]
    public void Enabled_ForAzure_NeedsAnEndpointButNoKey()
    {
        var azure = new RetrievalOptions
        {
            Enabled = true,
            Embedding = new EmbeddingOptions { Provider = EmbeddingProviderKind.AzureFoundry },
        };

        var ex = Assert.Throws<InvalidOperationException>(azure.Validate);
        Assert.Contains("ControlPlane:PowerAI:Retrieval:Embedding:Endpoint", ex.Message, StringComparison.Ordinal);

        azure.Embedding.Endpoint = "https://account.openai.azure.com";
        azure.Validate();
    }

    [Fact]
    public void Enabled_WithAnOutOfRangeThreshold_Fails()
    {
        var options = new RetrievalOptions
        {
            Enabled = true,
            Embedding = new EmbeddingOptions { ApiKey = "sk-test" },
            SimilarityThreshold = 1.5,
        };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("SimilarityThreshold", ex.Message, StringComparison.Ordinal);
    }
}

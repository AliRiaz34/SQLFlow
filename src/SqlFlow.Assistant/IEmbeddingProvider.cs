namespace SqlFlow.Assistant;

/// <summary>Which service turns text into an embedding vector. Anthropic is deliberately absent: it serves no
/// embeddings endpoint, so a deployment running the Anthropic chat provider still picks one of these for
/// retrieval, independently of <see cref="AssistantProvider"/>.</summary>
public enum EmbeddingProviderKind
{
    /// <summary>The OpenAI platform directly (api.openai.com), authenticated with an OpenAI API key.</summary>
    OpenAI,

    /// <summary>An Azure AI Foundry / Azure OpenAI embeddings deployment, authenticated with the Azure
    /// credential (managed identity in the container), for a deployment keeping every model call inside its
    /// own Azure tenant boundary.</summary>
    AzureFoundry,
}

/// <summary>
/// Turns a question into a dense vector so questions can be ranked by cosine similarity (POWERAI.md Section 6:
/// confidence is retrieval similarity, never an LLM's self-rating). One interface with a swappable backend,
/// the same shape <see cref="IAssistantGateway"/> already has for chat, so retrieval keeps a single code path
/// regardless of which vendor a deployment embeds with.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>The model identifier the vectors are produced by, persisted alongside each embedding so a
    /// model change is detectable and the affected rows can be re-embedded selectively.</summary>
    string Model { get; }

    /// <summary>The dimension count every vector this provider returns has.</summary>
    int Dimensions { get; }

    /// <summary>
    /// Embeds a batch of texts, returning one vector per input in the same order. Batching is the unit because
    /// the sync-time enrichment step embeds many questions at once and both backends charge and rate-limit per
    /// request, not per text.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}

/// <summary>Cosine similarity over the embeddings this feature stores, and the byte round-trip the catalog
/// persists them with. Kept beside the provider interface because both sides of retrieval (writing a vector at
/// sync time, ranking against it at query time) need the identical encoding.</summary>
public static class EmbeddingMath
{
    /// <summary>
    /// Cosine similarity of two equal-length vectors, in [-1, 1] (in practice [0, 1] for text embeddings).
    /// Returns 0 for a zero-magnitude vector rather than dividing by zero, which reads correctly as "no
    /// similarity" for a degenerate embedding.
    /// </summary>
    public static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException(
                $"Cannot compare embeddings of different dimensions ({a.Length} versus {b.Length}); "
                + "they were produced by different models.", nameof(b));
        }

        double dot = 0, magnitudeA = 0, magnitudeB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            magnitudeA += (double)a[i] * a[i];
            magnitudeB += (double)b[i] * b[i];
        }

        var denominator = Math.Sqrt(magnitudeA) * Math.Sqrt(magnitudeB);
        return denominator == 0 ? 0 : dot / denominator;
    }

    /// <summary>Packs a vector into the raw little-endian bytes the catalog's <c>varbinary(max)</c> column
    /// stores, with no JSON or text overhead.</summary>
    public static byte[] ToBytes(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(vector).CopyTo(bytes);
        if (!BitConverter.IsLittleEndian)
        {
            ReverseFloatBytes(bytes);
        }
        return bytes;
    }

    /// <summary>Unpacks what <see cref="ToBytes"/> wrote. A length that is not a whole number of floats means
    /// the stored value is not an embedding this code wrote, which is a corrupt row rather than a recoverable
    /// state.</summary>
    public static float[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new ArgumentException(
                $"Stored embedding is {bytes.Length} bytes, not a whole number of 4-byte floats.", nameof(bytes));
        }

        var owned = bytes.ToArray();
        if (!BitConverter.IsLittleEndian)
        {
            ReverseFloatBytes(owned);
        }
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(owned).ToArray();
    }

    private static void ReverseFloatBytes(Span<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i += sizeof(float))
        {
            bytes.Slice(i, sizeof(float)).Reverse();
        }
    }
}

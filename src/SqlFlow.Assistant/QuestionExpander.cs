using Anthropic;
using Anthropic.Helpers;
using Anthropic.Models.Messages;
using Anthropic.Services;
using Microsoft.Extensions.Logging;

namespace SqlFlow.Assistant;

/// <summary>
/// Expands a typed business question into the related vocabulary a stored question might have used instead, so
/// a search for "turnover" also reaches a dashboard question phrased as "revenue" (POWERAI.md Section 6). This
/// is the paraphrase gap a plain word search cannot close on its own: SQL Server's full-text engine already
/// handles inflections ("sell"/"selling"/"sold"), but not the business-vocabulary synonymy that two people
/// naming the same metric will produce.
/// <para>
/// Like <see cref="QuestionGenerator"/>, this is a plain, stateless, single-turn completion rather than an
/// <see cref="IAssistantGateway"/> consumer: it needs none of that gateway's MCP tool loop, streaming, or
/// per-user bearer. It returns TERMS rather than a ready-made query string on purpose, so the caller assembles
/// the full-text predicate itself: a model-authored fragment interpolated into search syntax is both a
/// correctness hazard (an apostrophe in "customer's" breaks the predicate) and an injection-shaped one.
/// </para>
/// </summary>
public sealed class QuestionExpander : IDisposable
{
    private readonly AnthropicClient _client;
    private readonly AnthropicOptions _options;
    private readonly ILogger<QuestionExpander> _logger;

    public QuestionExpander(AnthropicOptions options, ILogger<QuestionExpander> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger;
        _client = new AnthropicClient { ApiKey = options.ApiKey };
    }

    public void Dispose() => _client.Dispose();

    /// <summary>The model's answer: the search terms a stored question answering this might have used.</summary>
    [SchemaClass("Business-vocabulary search terms for finding a stored dashboard question")]
    private sealed class Answer : StructuredOutputModel
    {
        [SchemaProperty("The search terms: the question's own meaningful words plus business synonyms", MinItems = 1)]
        public List<string> Terms { get; set; } = [];
    }

    /// <summary>
    /// Returns the terms to search for: the question's own meaningful words plus business synonyms. Returns an
    /// empty list, rather than throwing, when the model cannot be reached or returns nothing usable after one
    /// retry; the caller then searches on the typed words alone, which is weaker but still answers, so an
    /// Anthropic outage degrades retrieval instead of breaking it.
    /// </summary>
    public async Task<IReadOnlyList<string>> ExpandAsync(string question, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var message = await _client.Messages.Create<Answer>(
                    new MessageCreateParams
                    {
                        Model = _options.Model,
                        MaxTokens = 512,
                        System = "You expand a business question into the words a SEARCH should look for, to "
                            + "find questions that a company's existing BI dashboards already answer. Return "
                            + "the question's own meaningful words (drop filler like 'what', 'our', 'is', "
                            + "'the') PLUS the business-vocabulary synonyms another analyst might have used "
                            + "for the same concepts: for example 'turnover' should also yield 'revenue', "
                            + "'sales', 'income'; 'clients' should also yield 'customers', 'accounts'. Return "
                            + "single words or short noun phrases only, lowercase, no punctuation, no boolean "
                            + "operators, no wildcards, and no explanation. Stay in the vocabulary of business "
                            + "metrics and entities; do not invent table or column names.",
                        Messages = [new MessageParam { Role = "user", Content = question }],
                    },
                    ct).ConfigureAwait(false);

                var answer = message.Content.Select(b => b.Parsed()).FirstOrDefault(a => a is not null);
                var terms = (answer?.Terms ?? [])
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .Take(MaxTerms)
                    .ToList();

                if (terms.Count > 0)
                {
                    return terms;
                }

                _logger.LogWarning(
                    "Anthropic returned no usable search terms for question '{Question}' on attempt {Attempt}",
                    question, attempt + 1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Question expansion failed for '{Question}' on attempt {Attempt}", question, attempt + 1);
            }
        }

        return [];
    }

    /// <summary>How many expanded terms are kept. Past this the added terms are increasingly loose associations
    /// that pull in unrelated questions rather than finding the right one.</summary>
    private const int MaxTerms = 24;
}

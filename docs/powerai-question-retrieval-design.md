# PowerAI Question Retrieval: Design

Status: step 1 of Section 8 has landed (the `IEmbeddingProvider` abstraction, its OpenAI/Azure
implementation, and the config gate); steps 2 onward are not implemented. This is POWERAI.md Section 6
("the learning loop") and Section
9 step 6 ("build the confirmed-example store and the retrieval step"), scoped out in detail now that
step 3 (the business-question field, `CatalogSubscriberReportVisualQuestion`) has landed and gives
this something real to retrieve against. See
[POWERAI.md](../POWERAI.md) for the surrounding roadmap and
[docs/reference/flow/subscribers.md](reference/flow/subscribers.md) for the systems this builds on.

## 1. The gap this closes

Today, a user's typed question can only be handed to an LLM as an unranked pile of stored
`CatalogSubscriberReportVisualQuestion` rows (11 in the one sample report extracted so far) for it to
eyeball itself. There is no measure of which stored question is actually closest to a new one, and no
signal for how much to trust an answer built from a near-miss versus an exact match. This design adds
that: a retrieval step that ranks stored (question, SQL, objects) examples by similarity to a new
question, and a confidence signal derived from that similarity rather than from the LLM's own
self-assessment (POWERAI.md is explicit that LLM self-rated confidence is not trustworthy: a wrong
query can sound exactly as confident as a right one).

## 2. What gets searched

Two provenances, one shape, per the flat-table decision already made in POWERAI.md Section 7:

- **`powerbi`**: `CatalogSubscriberReportVisualQuestion` rows, already landing today via the sync-time
  enrichment step (`SubscriberQuestionEnrichment`). Each question is backed by a real rendered SQL
  query (via the visual's `QueryName` → `CatalogSubscriberQuery.Sql`) and the object keys that query
  reads (`CatalogSubscriberQuery.ObjectKeys`).
- **`user-confirmed`**: new rows written when a person confirms (accepts or corrects) an answer from
  the learning loop itself (Section 6 of POWERAI.md). This is the confirmed-example store; it does not
  exist yet and is scoped alongside retrieval here because the two are built together — retrieval with
  nothing to learn from is just the PowerBI-only case, so the storage and the search step share one
  migration and one query shape from day one rather than needing a second migration once confirmation
  lands.

Both provenances get one new table, `CatalogQuestionExample`, rather than two: a `CatalogSubscriberReportVisualQuestion`
row is either promoted into it verbatim when first embedded, or (simpler, see Section 4) the existing
table is embedded in place and `CatalogQuestionExample` holds only the `user-confirmed` rows plus a
shared view/union for search. Section 4 below picks the simpler of the two.

## 3. Similarity mechanism: embeddings, not keyword matching

Keyword/full-text matching (SQL Server `CONTAINS`/`FREETEXT`, or a naive token-overlap score) was
considered and rejected as the primary mechanism: two questions can be semantically identical while
sharing almost no words ("what's our best-selling category" vs "which product category generates the
most revenue"), which is exactly the paraphrase gap a text-to-query system needs to close. Embedding
similarity (cosine distance over a dense vector) is the mechanism that actually captures this.

**Embedding provider.** Reuse the existing Anthropic account/config (`AssistantSettings.Anthropic`,
already wired for `QuestionGenerator`) is NOT an option here: Anthropic does not serve an embeddings
endpoint. Two real choices:

- **OpenAI embeddings** (`text-embedding-3-small`, 1536 dims, cheap, fast) via a new small
  `EmbeddingOptions` reusing the `AssistantSettings.OpenAI.ApiKey` shape already in
  `SqlFlow.Assistant/AssistantSettings.cs` if `OpenAI.ApiKey` is set, independent of `Assistant.Provider`
  (a deployment can run the Anthropic chat provider and still use OpenAI purely for embeddings, the
  same way question generation reuses Anthropic independent of `Assistant.Enabled`).
- **Azure AI Foundry embeddings** (if the deployment already standardized on Foundry per
  `AssistantSettings.Foundry`), for a deployment that wants to keep every model call inside its Azure
  tenant boundary rather than adding a second vendor (OpenAI direct).

Recommendation: **support both behind one small interface** (`IEmbeddingProvider.EmbedAsync(string
text, CancellationToken) -> float[]`), selected by a new `ControlPlane:PowerAI:Retrieval:EmbeddingProvider`
switch (`OpenAI` | `AzureFoundry`), mirroring the existing `AssistantProvider` enum pattern rather than
inventing a new one. This keeps the single-code-path principle: one retrieval code path, one interface,
swappable backend, exactly like `IAssistantGateway` already is for chat.

## 4. Storage: where the vector lives

**Recommendation: add the embedding directly onto the existing rows, not a separate vector table.**

- `CatalogSubscriberReportVisualQuestion` gains an `Embedding` column (`varbinary(max)`, storing the
  float array as raw bytes — `BitConverter`/`MemoryMarshal` round-trip, no JSON overhead) and an
  `EmbeddedAt`/`EmbeddingModel` pair so a model upgrade can be detected and re-embedded selectively
  (comparing `EmbeddingModel` the same way `ContentHash` already gates regeneration).
- The new `CatalogQuestionExample` table (for `user-confirmed` rows) gets the identical three columns
  (`Embedding`, `EmbeddingModel`, `EmbeddedAt`) plus `Question`, `Sql`, `ObjectKeys`, `Provenance`,
  `Confidence`, `ConfirmedByUserId`, `ConfirmedAtUtc`.
- A single retrieval query unions both sources (a SQL `UNION ALL` view, or two queries merged in C#)
  rather than requiring a caller to know which table a hit came from — matching how `CatalogLineageEdge`
  already unifies "what a flow writes" and "what a subscriber reads" into one edge shape.

**Why not SQL Server native vector search (`VECTOR` type / `VECTOR_DISTANCE`, SQL Server 2025+)?** The
catalog targets whatever SQL Server version a customer's shared estate already runs (see
`CatalogDatabase.cs`'s plain `UseSqlServer` with no version pin), and Section "Catalog Schema Changes"
in CLAUDE.md implies broad compatibility is assumed unless stated otherwise. Requiring SQL Server 2025
for one feature would split the estate into "can do retrieval" and "cannot," which is a much bigger
decision than this feature should force. Revisit this once the deployed estate's SQL Server version
floor is known; until then, do the distance computation in the application tier (Section 5).

**Why not an external vector database (pgvector, Pinecone, Qdrant, Azure AI Search)?** Adds a second
storage system to operate, back up, and secure, for a corpus that starts at 11 rows and will realistically
stay in the thousands (one row per visual across an estate's PowerBI reports, plus confirmed examples).
At that scale, brute-force cosine similarity over rows already pulled into the control-plane process is
fast enough (Section 5) and keeps the single-code-path principle: one catalog database, one connection
string, one backup/restore story.

## 5. The search step itself

```
Task<IReadOnlyList<QuestionMatch>> FindSimilarAsync(string question, int topK, CancellationToken ct)
```

1. Embed the incoming question (one call to `IEmbeddingProvider`).
2. Load candidate rows: every `CatalogSubscriberReportVisualQuestion`/`CatalogQuestionExample` row that
   has a non-null `Embedding` (repo-scoped or estate-wide, configurable — start estate-wide, since a
   question about "revenue" is relevant regardless of which repo's report first asked it). At the
   scale expected (thousands of rows), pulling `Embedding` + the row's other columns into memory and
   computing cosine similarity in C# (`System.Numerics.Tensors` or a hand-rolled dot-product/L2-norm
   loop) is well within a single request's budget; no index is required at this scale.
3. Rank by cosine similarity descending, return the top `topK` (POWERAI.md's own framing: 1-3 is
   usually enough context for the LLM to work from, mirroring the 1-3 questions per visual already
   chosen for the same reason).
4. Each `QuestionMatch` carries the matched question, its SQL, its object keys, its provenance, and its
   similarity score, so the caller (this is where the actual guess/confirm/remember flow of POWERAI.md
   Section 6 plugs in) can:
   - Show the LLM the closest 1-3 examples as grounding context when asked to write new SQL.
   - Report a similarity-derived confidence alongside the answer (e.g., "closest match: 0.91" reads
     very differently from "closest match: 0.42"), not an LLM-invented confidence number.
   - Gate on a similarity threshold: below it, the system states plainly this is an unverified guess and
     always routes through the existing DataOps human-confirmation gate before anything is treated as
     correct (POWERAI.md Section 6, unchanged by this design — retrieval only feeds that gate a better
     signal, it does not bypass it).

## 6. Where this runs, and when embeddings are computed

Same split as question generation itself, for the same reasons (POWERAI.md Section 10's built
"business-question field" enrichment step):

- **Embedding a stored question happens in the control plane**, as a small extension of the existing
  post-sync `SubscriberQuestionEnrichment` step: once a visual's questions are generated (or carried
  forward unchanged), embed any question lacking an `Embedding` or whose `EmbeddingModel` is stale.
  An unchanged visual's carried-forward questions already have their embedding carried forward too
  (no re-embedding), the same incremental principle the hash-gate already established.
- **Searching happens on demand**, from wherever a question is typed: the GUI chat assistant, the Slack
  bot, or a future dedicated "ask a question" surface, via a new MCP tool (`find_similar_questions` or
  folded into `describe_subscriber_report`'s sibling surface) so the existing assistant surfaces get
  this for free without inventing a new client integration.
- **Never in `tools/pbix-extract` or the bare CLI**, unchanged from the question-generation precedent:
  the parser stays a parser, and the CLI's `sqlflow db sync` never needs an embeddings API key.

## 7. Schema summary (for the eventual migration)

```csharp
// Added to the existing table:
public class CatalogSubscriberReportVisualQuestion
{
    // ...existing columns...
    public byte[]? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }
    public DateTime? EmbeddedAtUtc { get; set; }
}

// New table, the user-confirmed half of the example store (POWERAI.md Section 8):
public class CatalogQuestionExample
{
    public long Id { get; set; }
    public Guid RepoId { get; set; }
    public string Question { get; set; } = string.Empty;
    public string Sql { get; set; } = string.Empty;
    public string ObjectKeys { get; set; } = string.Empty;   // newline-joined, same convention as CatalogSubscriberQuery
    public string Provenance { get; set; } = string.Empty;   // "user-confirmed" (this table is never "powerbi")
    public double Confidence { get; set; }                    // the similarity score at confirmation time
    public string? ConfirmedByUserId { get; set; }
    public DateTime ConfirmedAtUtc { get; set; }
    public byte[]? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }
    public DateTime? EmbeddedAtUtc { get; set; }
}
```

## 8. Sequencing (each step landable and testable independently, no time estimates)

1. ~~`IEmbeddingProvider` abstraction + one implementation~~ **Done**. `IEmbeddingProvider`
   (`src/SqlFlow.Assistant/IEmbeddingProvider.cs`) with `EmbeddingGateway`
   (`src/SqlFlow.Assistant/EmbeddingGateway.cs`) serving BOTH backends rather than OpenAI only: the two
   speak the same wire format, so like `ResponsesApiGateway` they differ in endpoint and credential
   alone and a second implementation would have been duplicated code. `EmbeddingMath` (cosine similarity
   plus the `varbinary` byte round-trip) sits beside the interface, since sync-time writing and
   query-time ranking need the identical encoding. Config-gated by
   `ControlPlane:PowerAI:Retrieval:Enabled` (`RetrievalOptions`), independent of both `Assistant.Enabled`
   and `PowerAI:QuestionGeneration:Enabled`, and unlike question generation it declares its own
   credential rather than reusing `Assistant:Anthropic`, because Anthropic serves no embeddings endpoint.
   Batching is the unit of `EmbedAsync` (a sync embeds many questions at once and both backends charge
   and rate-limit per request), responses are re-sorted by each entry's `index` rather than array
   position, and throttle retries reuse the chat gateway's Retry-After policy.
2. ~~Migration: `Embedding`/`EmbeddingModel`/`EmbeddedAtUtc` on `CatalogSubscriberReportVisualQuestion`.~~
   **Done**: migration `AddQuestionEmbeddings`, the vector as `varbinary(max)`.
3. ~~Extend `SubscriberQuestionEnrichment` to embed newly-generated/carried-forward questions lacking a
   current embedding.~~ **Done**: `SubscriberQuestionEnrichment.EmbedQuestionsAsync`, wired into both sync
   paths (`RepoSyncService` and the manual sync endpoint) beside the generation step. It is deliberately
   its OWN step behind its OWN switch rather than folded into `EnrichAsync`, because retrieval and question
   generation are independently toggleable: a deployment that generated questions earlier and only now
   turns retrieval on has a table of un-embedded rows, and this fills them in with no second LLM pass. A
   row is selected when it has no vector or its `EmbeddingModel` is not the configured one, so a model
   change re-embeds exactly the affected rows. The pre-sync snapshot was widened to carry each question's
   existing vector alongside its text (`PriorQuestion`), so an unchanged visual costs neither an LLM call
   nor an embeddings call. An embeddings failure degrades to a returned warning, never an exception, so an
   outage cannot cost the rows the sync already wrote.
4. ~~`FindSimilarAsync` (in-process cosine ranking) + a small integration test seeding a handful of known
   questions and asserting the ranking order matches expectation for an obvious paraphrase.~~ **Done**:
   `QuestionSearch.FindSimilarAsync` (`src/SqlFlow.ControlPlane/Background/QuestionSearch.cs`), returning
   `QuestionMatch` (question, the SQL that answers it, its object keys, provenance, similarity, subscriber,
   visual title). It ranks first and resolves second, so only the winning handful cost a lookup of their SQL
   and objects rather than joining across every stored question. Rows embedded by a different model than the
   caller's are skipped rather than compared, since two models share no coordinate space and ranking across
   them would produce confident nonsense; the next sync re-embeds them. A search against an estate with
   nothing embedded returns empty without embedding the question at all. Tested (`QuestionSearchTests`)
   against a real SQL Server with a deterministic offline embedder, including the paraphrase case that
   motivates embeddings over keyword matching: "what was our turnover per territory" finds "What is our
   revenue by region?" despite sharing no content words.
5. MCP tool surface (`find_similar_questions` or equivalent) so an assistant surface can call it.
6. Migration + `CatalogQuestionExample` table, and the actual confirm/correct/reject UI flow
   (POWERAI.md Section 6) that writes into it — this is "the learning loop" itself and is the largest
   remaining piece; everything above this line is useful (retrieval over PowerBI-derived questions
   alone) even before this step lands.

## 9. Open questions to settle before implementation starts

- **Estate-wide vs repo-scoped search.** Implemented as the caller's choice: `FindSimilarAsync` takes a
  nullable `repoId`, searching one repo when given and the whole estate when null. Which of the two a
  SURFACE should pass is still open and lands with step 5 (the MCP tool), since that is the first caller
  that has to decide; estate-wide remains the recommended default per Section 5.
- ~~**Which embedding provider is the default when neither OpenAI nor Foundry is otherwise configured.**~~
  **Settled** in step 1: retrieval is off by default, and enabling it without the credential its chosen
  provider needs is a startup error naming the exact configuration key (`RetrievalOptions.Validate`),
  the same posture question generation already takes. There is no silent fallback to a third provider.
- **Re-embedding on a model upgrade.** `EmbeddingModel` gates detection, but nothing yet defines the
  operational trigger (a config change? a CLI command? automatic on next sync?) — needs a decision
  before step 2 above ships.

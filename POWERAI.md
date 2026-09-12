# PowerBI Metadata Harvesting and Text-to-Query: Design and Roadmap

Status: partially implemented. Extraction is done and landed (both the `.pbix` semantic model and
its report/visual layer); nothing downstream of storage (retrieval, the confirmed-example store, the
learning loop) exists yet. Section 10 states exactly what is built versus what remains. For the
systems this feature builds on, see
[docs/reference/flow/subscribers.md](reference/flow/subscribers.md) and
[docs/reference/concepts/data-operations.md](reference/concepts/data-operations.md).

## 1. The goal

Build a text-to-query system: a user types a business question in plain language, and SQLFlow
returns (or helps write) the SQL that answers it, informed by real prior usage rather than a blind
guess against the schema.

## 2. Why PowerBI is the starting source of truth

A PowerBI report is a record of which business questions were already asked and already answered.
Compared to the warehouse schema alone, a report additionally encodes:

- **Which questions get asked.** A chart titled "Revenue by Region over Time" is a business
  question with its shape already decided: revenue, split by region, over time.
- **Which fields actually matter together**, out of everything a table exposes, and which other
  tables they get joined against to answer real questions.
- **The business vocabulary**: what people call things ("Net Revenue", "Active Riders") versus the
  column names the warehouse actually uses. This is the translation layer a text-to-query system
  needs to turn a typed question into a query.
- **Measures**: business logic (a DAX formula for "Net Revenue", including its filters and
  exclusions) that exists nowhere else in the estate.
- **Field ROLE**: whether a field is the axis a chart is broken down BY or the value it plots (the
  difference between "sales by month" and "months by sales"). This is the one fact parsed SQL
  cannot recover on its own, and it is why the visual layer is extracted at all, not only the
  semantic model.

This is the framing to keep: the target is not "PowerBI's data", it is "the questions people already
decided were worth asking, and how they answered them."

## 3. What already exists to build on

This feature extends the existing subscriber pipeline, not a parallel one, per the Single Code Path
Principle.

- **`subscribers.yaml`** ([docs/reference/flow/subscribers.md](reference/flow/subscribers.md))
  declares a PowerBI report as a `CatalogSubscriber` with `type: PowerBI`, an owner, a URL, and a
  list of named queries with their SQL. A subscriber can additionally declare `pbix:`, naming either
  one `.pbix` file or a directory of them (Section 4); every report found is extracted under that
  one subscriber automatically. Hand-transcribed queries still work and are kept alongside an
  extracted report on the same subscriber.
- **Consumption lineage.** Every subscriber query, hand-written or extracted from a report's
  visuals, is parsed the same way a view body is: the tables it touches become real lineage edges
  (`CatalogLineageEdge`), and the joins it writes feed the interpreted data model
  (`CatalogObjectRelationship`, with `Origin`, `Tier`, and `Occurrences`) exactly like a warehouse
  view's joins would.
- **The MCP retrieval surface** already exists and already frames itself around text-to-query:
  `describe_object` (identity, columns, interpreted key, generating code, relationships),
  `get_table_joins` (ranked join paths between tables, explicitly "the observed predicates ARE the
  data model"), `list_subscribers` / `describe_subscriber` (what a given report reads and the SQL it
  runs). **Not yet extended** to surface the new report/page/visual/field tables (Section 10).
- **The safety net.** Ad-hoc query execution
  ([docs/reference/concepts/data-operations.md](reference/concepts/data-operations.md)) is read-only
  by construction (single `SELECT`, always run inside a rolled-back transaction) and gated behind
  human confirmation and the `ControlPlane__DataOps__Enabled` switch. Any query this feature proposes
  must go through that same gate, not a new one.

What does NOT exist yet: a field to capture the *business question* a query answers (the current
`queries[].name` is a short label, not a question), a store for confirmed question-to-query pairs,
any confidence-scoring or feedback loop, an MCP surface over the extracted report structure, and DAX
measure evaluation (the DAX text is extracted and stored verbatim, but nothing interprets it).

## 4. Format decision: PBIP/TMDL vs `.pbix` (settled)

Settled: extraction targets `.pbix` directly, not PBIP/TMDL. `.pbix` is what reports are actually
distributed as, so requiring a PBIP export first would have added a manual conversion step nobody
would reliably do. Reaching the semantic model means undoing `.pbix`'s compression rather than
reading plain text: the `DataModel` zip member is an XPress9 block stream that decompresses to an
Analysis Services backup image, whose (XML) virtual directory locates an embedded SQLite database
holding the actual metadata. This is fully solved: see Section 10 for what reads it and how.

## 5. Extraction priority (delivered against this list)

Ranked by value versus effort, based on what a text-to-query system actually needs. Status per item
reflects what Section 10 details:

1. **Measures (DAX).** Extracted, with a migration. The DAX expression is stored verbatim; nothing
   evaluates or type-checks it yet.
2. **Model relationships and cardinality.** Extracted, with a migration, tagged as `active`/inactive
   so a generated join can state whether a relationship needs `USERELATIONSHIP` to apply. Stored on
   `CatalogSubscriberReportPage`'s siblings inside the model spec, not merged into
   `CatalogObjectRelationship` (see Section 7 for why they are kept apart).
3. **Calculated columns.** Extracted, with a migration.
4. **Display names.** Extracted (every table/column/measure name as PowerBI labels it). Display
   *folders* (grouping metadata) are not currently read.
5. **Visuals and pages.** Extracted: every page, every visual's chart type and title, and every
   field's role (`Category`/`Y`/`Rows`/`Values`/`Size`, etc.), plus filters (including page-level
   slicers) folded into the visual's synthesized SQL `WHERE` clause rather than stored separately.

The compatibility-view complication flagged in the original design is not yet addressed: a report in
Import mode names MODEL entities (`Sales`), not warehouse objects, so an extracted consumption edge
currently lands on a name-only node that does not unify with a fully-qualified ingestion target like
`[OdsDb].[arc].[Sales]`. See Section 10's known limitations.

## 6. The learning loop (question -> guess -> confirm -> remember)

Not started. Separate from extraction: a live feedback loop that grows the example set from real
usage, not just from PowerBI.

- A user asks a question. The system searches confirmed question/query examples (from PowerBI
  extraction and from prior confirmed answers alike) for a close match.
- **Confidence is retrieval similarity, not LLM self-rating.** LLMs are not reliable judges of their
  own correctness; a wrong query can sound exactly as confident as a right one. The trustworthy
  signal is how close the new question is to an existing *confirmed* example (embedding/keyword
  similarity), not a number the LLM invents about itself.
- Below a similarity threshold, there is no proven precedent: the system says plainly that this is
  an unverified guess and always routes it through human confirmation before anything is treated as
  correct. This is not a new gate; it is the existing DataOps human-confirmation requirement, with
  the similarity score used to decide how much a caller should trust a result already flagged for
  review, not whether review happens at all.
- On confirmation (accept, correct, or reject), the question/query pair is written back into the
  example store with its provenance (`powerbi` vs `user-confirmed`) and an updated confidence. On
  rejection, nothing is learned as fact.
- This makes the system self-improving under real usage without ever executing an unverified query
  automatically.

## 7. Storage: flat confirmed examples, not a separate AST/graph store

Considered and rejected, and this decision held through implementation: storing PowerBI-derived
metadata as its own graph/AST structure, separate from `subscribers.yaml`/the catalog.

Reasoning: accepting a second source of question/query facts (user-confirmed answers, alongside
PowerBI-extracted ones) does not require a new storage shape. Both are the same shape of fact: a
question, a query, the objects/columns it touches, a provenance, and a confidence. What got built
is flat, appendable catalog tables extending the existing pattern (`CatalogSubscriberReportPage` /
`Visual` / `Field`, modeled on `CatalogSubscriberQuery`), keeping one code path for read, write,
sync, and (once built) MCP exposure. No parallel graph store exists.

PowerBI's own declared relationships are extracted (Section 5) but are deliberately **not** merged
into `CatalogObjectRelationship`: a model relationship declares the report author's intent about how
entities may be joined, while an edge in that table records a join a query actually performed.
Conflating them would put claims in the lineage graph that no executed SQL supports. The one part of
this that genuinely is graph-shaped, table-to-table join relationships inferred from real queries, is
already stored as a graph today and already walked as one by `get_table_joins`; PowerBI's declared
relationships are a second, clearly-labeled kind of fact, not a merge into that same graph.

The confirmed-example store (Section 6) remains unimplemented, and when it lands it should follow
this same flat-table precedent rather than introduce a new shape.

## 8. Anticipated schema work (updated against what has landed)

Per the Catalog Schema Changes Require a Migration rule. Landed:

- `CatalogSubscriberReportPage` / `CatalogSubscriberReportVisual` / `CatalogSubscriberReportField`:
  a subscriber's report pages, visuals, and each field's role. Linked by derived string key
  (`SubscriberKey#reportFile#pageOrdinal`, then `#visualOrdinal`), not surrogate FK, because
  `CatalogTransaction.InSerializableAsync` commits a whole sync in one `SaveChanges`, so no identity
  value exists while children are staged.
- The model spec itself (measures, calculated columns, relationships with `active`/cardinality,
  columns, table sources/M expressions) is extracted and written into `subscribers.yaml`'s expanded
  attributes by `tools/pbix-extract`, not (yet) into its own catalog tables. It travels as YAML text
  today; whether it needs dedicated catalog tables (so MCP can query it structurally rather than
  parse YAML) is open, tracked in Section 10.

Still needed, not started:

- A field on subscriber queries (or a new small catalog table) to hold the business question a query
  answers, distinct from the existing short `name` label.
- A new table for confirmed question/query examples: question text, SQL, source object keys,
  provenance (`powerbi` | `user-confirmed`), confidence/similarity, timestamp, confirming user.
- Whatever storage the confirmed-example store and business-question field turn out to need,
  following the flat-table precedent in Section 7.

## 9. Roadmap

Sequenced, each step landing before the next starts. Strikethrough marks what has landed.

1. ~~Confirm the PowerBI file format available (PBIP/TMDL vs `.pbix`).~~ **Done**: `.pbix` (Section 4).
2. ~~Extract queries and relationships first.~~ **Done**, and gone further than originally scoped:
   built both a standalone C tool (`tools/pbix-extract`, no .NET/Python dependency, for offline/
   scriptable extraction to a reviewable YAML spec) and a C# extraction path
   (`SqlFlow.PowerBi` + `FlowSetCollector`) wired directly into `sqlflow db sync` and
   `sqlflow lineage`. This closes consumption lineage for every report and feeds the join graph,
   plus (beyond original scope) extracts the full semantic model: measures, calculated columns,
   relationships, table sources. See Section 10 for the duplication this created between the two
   tools and the open question of what to do about it.
3. **Add the "business question" field.** Not started. Extend the subscriber query shape to capture
   the question a query/visual answers, not just its short name. Requires a migration.
4. ~~Extract measures.~~ **Done** (Section 5), via `tools/pbix-extract`; not yet via the C# path or
   dedicated catalog tables (Section 8).
5. ~~Extract declared model relationships and cardinality.~~ **Done** (Section 5), tagged `active`;
   kept out of `CatalogObjectRelationship` per Section 7, not (yet) in dedicated catalog tables.
6. **Build the confirmed-example store and the retrieval step.** Not started.
7. **Wire the learning loop.** Not started; depends on step 6.
8. ~~Visuals/field-co-occurrence and default filters.~~ **Done**, and delivered earlier than
   originally sequenced (visual-layer data turned out to be the load-bearing signal: it is the only
   place a field's ROLE is recorded, and SQL-derived lineage cannot recover that fact). Every page,
   visual, chart type, title, and field role is extracted and stored; filters (including page-level
   slicers) are folded into the visual's synthesized SQL rather than kept as a separate structure.

## 10. What is built, what remains, and what a finished product needs

### Built and verified

- **A full `.pbix` reader, twice over** (see "Open: tool duplication" below for why this is not
  simply good news):
  - `tools/pbix-extract`: a standalone C binary with no .NET/Python dependency. Decompresses the
    `DataModel` (vendored MIT-licensed XPress9 decoder, decode-only), parses the ABF container and
    its embedded `metadata.sqlitedb` (via `sqlite3_deserialize`, no temp files), and reads
    `Report/Layout` for the visual layer. Emits one YAML spec file per report: model (tables,
    columns, measures, calculated columns, relationships, table sources/M) plus report (pages,
    visuals, field roles). Runtime schema probing handles PowerBI SQLite schema drift across
    versions with a stated error naming the missing table/column, rather than a silent empty result.
    Verified clean under AddressSanitizer/UndefinedBehaviorSanitizer, including catching and fixing a
    genuine upstream bug in Microsoft's reference decoder (`memcpy` over an overlapping range).
    Redacts the report author's local file paths (a `File.Contents("C:\Users\...")`-shaped literal)
    out of extracted M expressions before they reach disk.
  - `SqlFlow.PowerBi` + `SqlFlow.Lineage`'s `FlowSetCollector`: the C# path, reading only
    `Report/Layout` (the visual layer; it does not touch `DataModel`). A subscriber's declared
    `pbix:` (one file, or a directory of them, extracted under one subscriber) is read, each
    visual's `prototypeQuery` and filters translated to one SQL `SELECT ... WHERE ...` by
    `VisualQueryTranslator`, and fed through the *existing* `TSqlLineageExtractor`/
    `ScriptFactBuilder` path, so a visual's question becomes a real consumption edge with no second
    lineage mechanism. Decoration visuals (textboxes, shapes with no field projections) are dropped.
    An untranslatable filter refuses the whole visual with a warning rather than recording it
    silently narrowed. Wired into `sqlflow db sync` (writes the three catalog tables below) and
    `sqlflow lineage` (works with no catalog database at all).
- **Catalog storage**: `CatalogSubscriberReportPage` / `Visual` / `Field`, migrated, keyed by derived
  string (not surrogate FK, since a whole sync commits in one `SaveChanges`). A subscriber's report
  file is part of the page key, so two reports in one directory sharing a page name (the common case:
  two files from the same template) do not collide.
- **Directory-of-reports subscribers**: `pbix:` names either a file or a folder; every `.pbix` under
  a folder becomes part of the same subscriber, so a team's workspace of several reports needs one
  hand-written subscriber entry, not one per file.
- Verified end to end against a real report (AdventureWorks Sales, both extraction paths cross-
  checked and produced identical page/visual/field structure): 8 tables / 57 columns, 1 measure, 1
  calculated column, 9 relationships, 8 table sources, 3 pages, 6 visuals.
- Solution builds with 0 errors and no new warnings; 21 tests over the C# extraction/collector path,
  9 over the C reader; the whole suite (4472 tests) passes.

### Extraction noise audit

Reviewed every field either extractor captures against the stated goal (matching a user's typed
question to the right dashboard query) and cut what does not serve it. No visual styling ever leaked
in (colors, positions, sizes were never captured by either reader), and the C tool already dropped
internal PowerBI identifiers (the page's internal generated name, visual ordinal, `queryRef`) before
this pass. Cut in this pass:

- **`measures[].displayFolder`** (`tools/pbix-extract`) and **`relationships[].crossFilterDirection`**
  (`tools/pbix-extract`): pure PowerBI UI/jargon metadata with no business-facing signal, dropped from
  the C tool's struct, SQL, and YAML emission.
- **`CatalogSubscriberReportField.QueryRef`**: the C# path was persisting PowerBI's internal
  per-visual-query alias into the catalog even though the C tool already omitted the equivalent field
  from its YAML output. Dropped from `ReportField`, `LineageSubscriberField`, and
  `CatalogSubscriberReportField` alike, so both extractors now agree; the local `queryRef` value is
  still used to *resolve* a field's column/measure in `PbixReportReader`, only the act of storing it
  downstream was removed. Needed an EF Core migration (`DropSubscriberReportFieldQueryRef`) since it
  is a catalog schema change.

Kept, despite being verbose: `tableSources[].powerQuery` (the full M expression). It is the noisiest
field in the spec, but it is also what the model-entity-to-warehouse-object resolution (Known
limitations, below) will need to read; cutting it now would mean re-adding it once that work starts.

### Known limitations (real, not hidden)

- **Model-entity resolution.** A visual names the MODEL entity (`Sales`), not the warehouse object,
  so an extracted consumption edge lands on a name-only node (no database, no schema) and does not
  unify with the fully-qualified node an ingestion flow writes (`[OdsDb].[arc].[Sales]`). Closing
  this needs the Power Query / M source expressions (which extraction already captures) mapped to
  physical tables; that mapping does not exist yet. Tests assert this limitation explicitly rather
  than papering over it.
- **Verified against one report.** PowerBI's embedded SQLite schema has shifted across versions
  before (column renames observed directly: `FromColumnID` vs `FromEndColumnID`). The C tool probes
  the schema at runtime and fails loudly naming what is missing, rather than assuming a shape, but
  that is a safety net, not proof every version is covered.
- **No MCP surface over the new data.** `list_subscribers`/`describe_subscriber` still return only
  queries and the objects they resolve to; nothing exposes a report's pages, visuals, or field roles.
  This is the most consequential open gap given the stated goal: the field-role data is the reason
  the visual layer was extracted at all (it is the one fact SQL-derived lineage cannot recover), and
  right now nothing lets an LLM (or a person) retrieve it.
- **The model spec (measures, relationships, calculated columns) lives only in YAML text**,
  generated by `tools/pbix-extract`, not in dedicated catalog tables the way the report structure is.
  An LLM given the YAML file directly can already read it, but there is no catalog-backed, MCP-queryable
  path to a measure's DAX or a relationship's cardinality the way there is for a table's columns.

### Open: tool duplication

`SqlFlow.PowerBi` and `tools/pbix-extract` both read `Report/Layout` and both derive a visual's
pages/visuals/field roles; the logic exists twice, in two languages. This was not a planned split:
the C tool was built after the C# path already existed, once the requirement changed to "a binary
tool written in C, it should do everything, no Python/.NET dependency", and the C# path was never
removed once the C tool could do the same job (and more: it also reads `DataModel`, which the C#
path cannot). Left as is, a future fix to one reader (a new visual type, a schema-drift case) has to
be remembered and re-applied to the other, or the two silently diverge. Not yet decided: drop the C#
visual extraction and have `sqlflow db sync` consume the C tool's YAML output instead, or keep both
deliberately for some reason not yet stated.

### What remains for a finished product

In roughly the order it would need to land, since each depends on groundwork the previous step laid:

1. **Resolve the tool duplication** (above). Whichever direction is chosen, this should happen before
   more logic is added to either reader, so new work does not get built twice again.
2. **An MCP surface over pages/visuals/fields**, and ideally over the model spec (measures,
   relationships, calculated columns) too, so an LLM can retrieve "what questions does this report
   already ask, and with what field roles" the same way it retrieves a table's columns today. This is
   the most direct path to the stated goal (LLM writes SQL from the specification) and currently has
   no path at all.
3. **The business-question field** (Section 8): today a query's `name` is a short label
   (`"Page 1 / Sales Amount by Category"`), not the question in the way a person would actually ask
   it. Needed before retrieval-by-similarity (step 5) has anything meaningful to match against.
4. **Model-entity-to-warehouse-object resolution** (known limitation, above), using the M source
   expressions already extracted. Needed before consumption lineage from a report is trustworthy
   for impact analysis ("what breaks if I change this table") rather than only descriptive.
5. **The confirmed-example store and retrieval** (Sections 6 and 8): the flat table for
   question/query/provenance/confidence, and similarity search over it. Not started.
6. **The learning loop** (Section 6): wiring retrieval into an actual guess-confirm-remember flow
   through the existing DataOps confirmation gate. Depends on step 5.
7. **Broader version coverage**: extraction is proven against one report from one PowerBI version.
   Widening this is a matter of running the tool against more real files as they turn up and fixing
   what the schema probes catch, not a design change.

Each item is scoped so it can be picked up, implemented, and landed independently; nothing here
should be treated as a single large batch of work.

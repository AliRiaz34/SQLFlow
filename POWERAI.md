# PowerBI Metadata Harvesting and Text-to-Query: Design and Roadmap

Status: pre-implementation design and roadmap. No code has been written for this feature yet. This
document exists to track the ideas discussed so far and sequence them; it is not a description of
current behavior. For the systems this feature builds on, see
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

This is the framing to keep: the target is not "PowerBI's data", it is "the questions people already
decided were worth asking, and how they answered them."

## 3. What already exists to build on

This feature must extend the existing subscriber pipeline, not create a parallel one, per the Single
Code Path Principle.

- **`subscribers.yaml`** ([docs/reference/flow/subscribers.md](reference/flow/subscribers.md))
  already declares a PowerBI report as a `CatalogSubscriber` with `type: PowerBI`, an owner, a URL,
  and a list of named queries with their SQL. Today those queries are hand-transcribed by whoever
  maintains the file; nothing extracts them from an actual PowerBI file automatically.
- **Consumption lineage.** Every subscriber query is parsed the same way a view body is: the tables
  it touches become real lineage edges (`CatalogLineageEdge`), and the joins it writes feed the
  interpreted data model (`CatalogObjectRelationship`, with `Origin`, `Tier`, and `Occurrences`)
  exactly like a warehouse view's joins would.
- **The MCP retrieval surface** already exists and already frames itself around text-to-query:
  `describe_object` (identity, columns, interpreted key, generating code, relationships),
  `get_table_joins` (ranked join paths between tables, explicitly "the observed predicates ARE the
  data model"), `list_subscribers` / `describe_subscriber` (what a given report reads and the SQL it
  runs).
- **The safety net.** Ad-hoc query execution
  ([docs/reference/concepts/data-operations.md](reference/concepts/data-operations.md)) is read-only
  by construction (single `SELECT`, always run inside a rolled-back transaction) and gated behind
  human confirmation and the `ControlPlane__DataOps__Enabled` switch. Any query this feature proposes
  must go through that same gate, not a new one.

What does NOT exist yet: an automated PowerBI file parser, a field to capture the *business question*
a query answers (the current `queries[].name` is a short label, not a question), a store for
confirmed question-to-query pairs, and any confidence-scoring or feedback loop.

## 4. Format decision: PBIP/TMDL vs `.pbix`

This gates the whole extraction effort and must be settled first. `.pbix` is a binary/zip container
around a compressed tabular model and is unpleasant and version-fragile to parse. The newer PBIP
project format stores the semantic model as plain-text TMDL and report pages as JSON, which is a
tractable text parse. Action: confirm which format the target reports are available in (or can be
saved as) before committing to a parser implementation.

## 5. Extraction priority

Ranked by value versus effort, based on what a text-to-query system actually needs:

1. **Measures (DAX).** The business logic that exists nowhere else. Cannot be pasted as T-SQL, but
   as documented intent it is the missing semantic layer.
2. **Model relationships and cardinality.** PowerBI's declared one-to-many/many-to-many and active/
   inactive relationships are a human assertion of the same fact `CatalogObjectRelationship` infers
   from observed joins. This is new information the SQL parser cannot produce on its own, and needs
   a new `Tier`/`Origin` value (a catalog migration; see Section 8).
3. **Calculated columns.**
4. **Display names and folders** (the business vocabulary layer, e.g. "Omsetning" -> `Revenue`).
5. **Visuals and pages**: not the chart type, but which fields co-occur, and default filters/slicers
   that are always applied (the implicit `WHERE` clause everyone forgets to state).

A compatibility-view complication to account for during extraction: a report in Import mode
frequently reads through an old-production-name compatibility view (per the "match old production
format" rule already governing ported sources), so extracted queries may name legacy table names.
The extractor must resolve through those views, or lineage edges will attach to the wrong catalog
objects.

## 6. The learning loop (question -> guess -> confirm -> remember)

Separate from extraction: a live feedback loop that grows the example set from real usage, not just
from PowerBI.

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

Considered and rejected: storing PowerBI-derived metadata as its own graph/AST structure, separate
from `subscribers.yaml`.

Reasoning: accepting a second source of question/query facts (user-confirmed answers, alongside
PowerBI-extracted ones) does not require a new storage shape. Both are the same shape of fact: a
question, a query, the objects/columns it touches, a provenance, and a confidence. A flat, appendable
table extending the existing catalog pattern (modeled on `CatalogSubscriberQuery`) fits this and keeps
one code path for read, write, sync, and MCP exposure. A parallel graph store would duplicate the
parsing, sync, and retrieval logic the subscriber pipeline already has, and would not be reviewable
in a diff the way YAML is.

The one part of this that genuinely is graph-shaped, table-to-table join relationships, is already
stored as a graph today (`CatalogObjectRelationship`) and already walked as one by `get_table_joins`.
No new graph structure is needed for that; PowerBI-derived and user-confirmed joins should feed the
same graph, not a second one.

## 8. Anticipated schema work

Per the Catalog Schema Changes Require a Migration rule, each of these is a migration when
implemented:

- A field on subscriber queries (or a new small catalog table) to hold the business question a query
  answers, distinct from the existing short `name` label.
- A new table for confirmed question/query examples: question text, SQL, source object keys,
  provenance (`powerbi` | `user-confirmed`), confidence/similarity, timestamp, confirming user.
- A new `Origin` value on `CatalogObjectRelationship` (or an equivalent field) to distinguish a
  PowerBI-declared relationship from one inferred from SQL joins.

## 9. Roadmap

Sequenced, each step landing before the next starts:

1. **Confirm the PowerBI file format available** (PBIP/TMDL vs `.pbix`). Gates everything below.
2. **Extract queries and relationships first.** Build a converter from the PowerBI project into
   `subscribers.yaml` entries (existing format, existing pipeline). This alone closes consumption
   lineage for every report and feeds the join graph with zero new catalog schema.
3. **Add the "business question" field.** Extend the subscriber query shape to capture the question
   a query/visual answers, not just its short name. Requires a migration.
4. **Extract measures.** New catalog storage for DAX measures and their definitions, since nothing
   in the existing shape holds them. Requires a migration.
5. **Extract declared model relationships and cardinality**, tagged with their own provenance
   alongside inferred relationships. Requires a migration.
6. **Build the confirmed-example store and the retrieval step** (similarity search over question
   text against confirmed examples), independent of which source (PowerBI or user-confirmed) fed it.
7. **Wire the learning loop**: guess, confidence from retrieval similarity, mandatory human
   confirmation below threshold, write-back on confirm/correct, through the existing DataOps
   confirmation gate rather than a new one.
8. **Visuals/field-co-occurrence and default filters last**, as the lowest-value, highest-effort
   extraction target.

Each numbered step is scoped so it can be picked up, implemented, and landed independently; nothing
here should be treated as a single large batch of work.

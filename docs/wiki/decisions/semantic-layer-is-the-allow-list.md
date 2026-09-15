---
id: wiki-semantic-layer-is-the-allow-list
title: "The semantic layer is the column allow-list"
type: decision
summary: "Why the assistant's schema is one layer defined by the column allow-list, and why chat lost the raw schema tools instead of shared endpoints being filtered."
keywords:
  - semantic layer
  - column policy
  - allow-list
  - whitelist
  - schema grounding
  - named contexts
  - chat tool allowlist
  - no-tracking
sourceRefs:
  - src/SqlFlow.ControlPlane/Api/SemanticLayer.cs
  - src/SqlFlow.ControlPlane/Api/SemanticLayerEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/SemanticLayerAdminEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/ColumnPolicyEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs
  - src/SqlFlow.ControlPlane/Program.cs
  - src/SqlFlow.Assistant/AssistantSettings.cs
  - tests/SqlFlow.ControlPlane.Tests/SemanticLayerApiTests.cs
  - src/SqlFlow.ControlPlane/Api/AssistantScope.cs
  - tools/sqlflow-mcp/src/control_plane.rs
referenceRefs:
  - concept-semantic-layer
  - concept-data-operations
  - concept-shadow-catalog
updated: 2026-09-15
---

# The semantic layer is the column allow-list

The column allow-list (`CatalogColumnPolicy`) already decided which columns an assistant may read. The
semantic layer did not add a second decision on top of it. It reuses that one, and adds meaning to the columns
it lets through: descriptions, synonyms, keys, joins, measures, instructions. What the layer serves is
documented in [Semantic layer](../../reference/concepts/semantic-layer.md). This page records why it has that
shape.

## One layer, defined by the allow-list

An object is in the layer exactly when one of its columns is allowed. No table records membership.

This was the human's call, stated as "the semantic layer is the whitelist: it gives you which tables, views,
and columns the LLM can use". The alternative was rejected.

**Rejected: named contexts.** The design that inspired the page's structure keeps several named contexts
(a separate project's "Semantic Context" module). Each context pins tables and holds its own instructions,
expressions, and example queries. That model would have needed:

- a membership store beside the allow-list, so two things would decide what an assistant sees and could
  disagree;
- a first step where the assistant picks a context before it can find a table.

With the allow-list as the only membership, "is this column available" has one answer, and the query guard
that refuses unallowed columns at run time already enforces that answer.

The same reasoning put the per-column annotations (`Description`, `Synonyms`) on the `ColumnPolicy` row,
not in a new per-column table. That row is already the layer's per-column record. A second table keyed the
same way would be a parallel store for one fact.

## The chat assistants lost the raw schema tools

The layer only works as the assistant's schema if the assistant has no other schema to read. The raw MCP
readers expose far more than the allow-list permits:

- `describe_object`, `search_all`, `search_definitions`, `get_table_joins`, `pipeline_columns` and the rest
  serve view bodies, join predicates, and key columns.
- `search_columns` and the object column list are filtered, but the other surfaces still name columns the
  allow-list withholds.

The human chose to take those tools off the GUI and Slack chat allowlists (`McpOptions.ExcludedTools`) and give
the chat four semantic tools instead.

**Rejected: filtering the shared endpoints.** Hiding every non-layer table and column inside
`/api/v1/search/*` and `/api/v1/lineage/*` would also have blanked the GUI's own Catalog and Search pages for
engineers. Those pages call the same endpoints. The filter would have been right for the assistant and wrong
for everyone else.

Two consequences follow from taking tools away rather than filtering them:

- **The key and joins had to be carried over.** Composing SQL still needs what `get_table_key` and
  `get_table_joins` supplied, so `describe_semantic_table` carries the key and the joins itself. The joins come
  through the dossier's relationship folding, which was extracted into
  `LineageEndpoints.LoadRelationshipsAsync` so both callers share one path.
- **"Who uses this table" had to be carried over too.** The chat had answered that from `describe_object`'s
  subscribers, and `object_lineage` walks flows and modules, not the read edges of reports. The bundle
  therefore carries `consumers`, from the same `LoadObjectSubscribersAsync` the dossier uses.

## Every annotation is re-checked when served

A column can be denied long after a key, join, measure, or example naming it was written. So nothing is served
on the strength of having been valid when it was saved:

- **Measures** are re-validated through the same guards as an ad-hoc query.
- **Keys and joins** are re-checked against the current allow-list.
- **Examples** are run through `ColumnPolicyGuard` before they are served.

A withheld annotation is kept, not deleted. Allowing the column again restores it. The editor shows why an
annotation is withheld instead of silently dropping it.

## The tools the chat kept still returned column names in their text

Taking the raw schema readers away left the operational tools. `run_statements`, `search_statements`,
`search_flows`, `pipeline_definition` and stored example SQL return free text that names any column. So did two
data-operations tasks, which read live rows without the column guard.

**Rejected: removing those tools too.** The assistant would have lost run diagnosis, one of the main things it
is used for.

**Rejected: a separate assistant token.** Minting a token with an assistant claim for the GUI chat, and
exchanging one for Slack's personal access token, would have added credential infrastructure on the auth path
for a marking that only needs to narrow.

**Rejected: redacting for everyone.** That repeats the rejected option above and blanks text engineers need.

**Chosen: an advisory marker.** Both gateways add `surface=assistant` to the MCP address. The MCP server turns
that into a header on its control-plane calls, and one endpoint filter withholds text naming a column outside the
layer. The header is safe to trust without authentication for two reasons:

- the model cannot set or remove it;
- it only ever narrows what a response shows.

A person omitting it sees what their token already allowed. The data-operations tasks follow the query guard's
own approach and are checked as the SELECT they would read.

## What building it exposed: updates to column policy were silently dropped

The control plane registers the catalog context with `QueryTrackingBehavior.NoTracking` (`Program.cs`). The
original single-column policy upsert loaded an existing row without `AsTracking()`, so its change was never
written:

- allowing a column the first time worked, because that path inserts a new row;
- denying it again did nothing, because that path updates an existing row.

`ColumnPolicyApiTests` asserts exactly that deny and failed on its first local run. The new semantic layer
tests failed the same way, because a measure stayed served after its column was denied. Every update path now
loads with `AsTracking()`.

What generalizes: in this control plane, a handler that loads a row in order to modify it must call
`AsTracking()`. A test that only inserts cannot catch the omission.

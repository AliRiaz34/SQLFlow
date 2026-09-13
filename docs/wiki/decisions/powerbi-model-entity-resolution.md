---
id: wiki-powerbi-model-entity-resolution
title: "A report's model entity becomes a warehouse object through the synonym pass"
type: decision
summary: "Why a PowerBI table's Power Query source is pattern-matched not parsed, emitted as an ordinary synonym, and refused rather than guessed when unrecognized."
keywords:
  - powerbi
  - pbix
  - model entity
  - power query
  - m expression
  - synonym
  - consumption lineage
  - node identity
  - Sql.Database
sourceRefs:
  - tools/pbix-extract/src/msource.c
  - tools/pbix-extract/src/msource.h
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Lineage/Collection/PbixExtractTool.cs
  - src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs
  - src/SqlFlow.Lineage/Collection/LineageFacts.cs
rawRefs:
  - docs/powerai-model-entity-resolution-design.md
  - POWERAI.md
referenceRefs:
  - flow-subscribers
  - concept-lineage-graph-and-plan
updated: 2026-09-13
---

# A report's model entity becomes a warehouse object through the synonym pass

## The gap

A PowerBI report's visuals name the report's own MODEL entity. A chart reads `Trips`, never
`[OdsDb].[arc].[Trips]`, because the model wraps the physical table behind a Power Query (M)
expression and nothing in the visual layer or in DAX ever refers back to it.

Consumption lineage is built by parsing each visual's synthesized SQL through the same extractor a
view body goes through, with no default database and a one-part minimum, so `FROM [Trips]` resolves
to a node carrying a server and a bare name with no database or schema. An ingestion flow writing
the same table produces a fully qualified node. The two never met, so the graph could not answer
"which reports break if I change this table" for any PowerBI-sourced read: the reads existed, but on
a node nothing else pointed at.

## Pattern matching, not parsing

M is a full functional language. A general parser for it would be a large, permanent maintenance
surface, and the payoff here is only a name-to-name mapping, not language execution. The realistic
shapes a warehouse-fed report uses are a short, closed list, so `msource.c` scans for one recognized
call shape and its navigation step:

```
Sql.Database("server", "database")  ...  {[Schema="arc", Item="Trips"]}[Data]
```

The navigation step is matched wherever it appears in the `let` chain rather than at a fixed
position, because PowerBI routinely inserts transform steps between the source and the table the
report sees, and its formatter's whitespace varies.

This lives in the C tool rather than downstream in C# for the reason the estate already learned once:
`SqlFlow.PowerBi` and `tools/pbix-extract` both read a report's layout, the logic existed twice in two
languages, and the fix was deleting the C# reader. A second reader of the same M text would have
recreated exactly that. The tool also already owns the untrusted-input boundary, so the M text never
needs to reach the control plane at all.

## An ordinary synonym, not a new mechanism

The graph builder already resolves facts of the shape "this name means that other name" through
`SynonymLink`, applied uniformly to every fact before it lands. A model-entity resolution is
structurally identical to a `sys.synonyms` row; only its source differs.

So the collector emits one `SynonymLink` per resolved table and nothing else changes. The graph
builder, the T-SQL extractor, and the SQL renderer are all untouched: a PowerBI-derived synonym and a
database-derived one are indistinguishable to the resolution pass, which is the point. The fix is
entirely additive, which is why it could land against a pipeline that was already tested.

## The server is reported but never used as identity

A resolved `Sql.Database("box.database.windows.net", "OdsDb")` names a server the way the report's
author typed it, which is not the identity the estate uses for that same server: a connection
reference is a declared, shared name, and an inline literal is identified by a content hash so a
credential never reaches a report.

Treating the M literal as a server identity would split one physical server into two nodes, which is
the precise failure the resolution exists to fix. The original design proposed reconciling the two
with an optional `modelSourceServer` key on the subscriber. The implementation avoids needing it: the
synonym's target keeps the SUBSCRIBER's own resolved server and takes only database, schema, and name
from the M expression. The two identities are never compared, so they cannot disagree, and no
configuration is required to keep them in step. `sourceServer` is still emitted on the table node for
a person to read.

## Unresolved is honest, guessed is not

Only a `Sql.Database` source with all four parts present resolves. Everything else is deliberately
refused and named in `reportWarnings`:

- An Excel, CSV, JSON, web, or SharePoint source: it names no warehouse object at all.
- A native `[Query="..."]` source: its objects are inside SQL text, not a schema/item pair. Resolving
  it would mean a second T-SQL parser inside the C tool, far more than a name mapping needs.
- `Sql.Databases` (plural): it takes no database argument and selects one downstream, so a prefix
  match would read the server as the database and resolve to a fabricated object.
- A server argument computed by another call: the literal inside that call belongs to it, not to the
  source.

The asymmetry driving all of these: a table left unresolved keeps its model name, where a reader can
see the lineage is incomplete. A table resolved WRONGLY points a report's entire consumption lineage
at an object it never read, and nothing about the result looks wrong. The first is a visible gap; the
second is silent corruption of the graph everything else trusts, so every ambiguity fails closed.

## What is still unproven

The matcher is covered by its own checks in the tool's suite, clean under AddressSanitizer and
UndefinedBehaviorSanitizer, and verified against the one real report on file, whose eight tables are
all Excel- or JSON-backed and correctly produce eight refusals.

That is also the limitation: no sample report reads a SQL database, so only the refusal path has run
end to end. The C# test fixture cannot close this either, because it builds a `.pbix` as a zip holding
only a report layout, while a model source lives in the compressed Analysis Services image the tool is
decode-only against. Proving the resolved path from file through to a unified edge needs a genuinely
SQL-backed report.

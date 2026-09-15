---
id: concept-semantic-layer
title: "Semantic layer: the allow-listed schema an assistant writes SQL against"
type: concept
summary: The column allow-list as the AI assistant's only schema, with descriptions, synonyms, curated keys and joins, measures, examples, and instructions.
keywords:
  - semantic layer
  - semantic context
  - column policy
  - allow-list
  - whitelist
  - business glossary
  - synonyms
  - measures
  - curated relationships
  - text to sql
  - schema grounding
  - get_semantic_layer
  - search_semantic_layer
  - describe_semantic_table
related:
  - concept-data-operations
  - concept-shadow-catalog
sourceRefs:
  - src/SqlFlow.ControlPlane/Api/SemanticLayer.cs
  - src/SqlFlow.ControlPlane/Api/SemanticLayerEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/SemanticLayerAdminEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/ColumnPolicyEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/ColumnPolicyGuard.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.Assistant/AssistantSettings.cs
  - src/SqlFlow.Assistant/AssistantInstructions.cs
  - tools/sqlflow-mcp/src/server.rs
  - gui/src/features/semantic-layer/SemanticLayerPage.tsx
  - src/SqlFlow.ControlPlane/Api/AssistantScope.cs
  - src/SqlFlow.ControlPlane/Api/DatasourceEndpoints.cs
  - tools/sqlflow-mcp/src/control_plane.rs
---

# Semantic layer

The semantic layer is the schema the AI assistant is allowed to use, together with what that schema means. It
is built on the column allow-list (`CatalogColumnPolicy`, see
[Data operations](data-operations.md#column-policy-what-the-assistant-may-read-at-all)):

- An object (table or view) is **in the layer** exactly when at least one of its catalogued columns is allowed.
  Nothing else records membership.
- Only **allowed columns** are ever served, and nothing served names a column outside the layer: not a key, a
  join, a measure, or an example query.

On top of that set the layer holds business context, all admin-authored:

| Element | Where it lives | What the assistant gets |
| --- | --- | --- |
| Column description and synonyms | `ColumnPolicy.Description`, `ColumnPolicy.Synonyms` | The meaning of each allowed column; synonyms are matched by search |
| Table business name, description, synonyms | `SemanticObject` | What a row represents and what people call the table |
| Curated key | `SemanticObject.KeyColumns` | The columns identifying one row, preferred over the key interpreted from code |
| Curated relationships | `SemanticRelationship` | Joins an admin vouches for, preferred over joins discovered from code |
| Measures | `SemanticMeasure` | Named SQL expressions anchored to one table, to reuse verbatim |
| General instructions | `SemanticLayerSettings.Instructions` | Conventions every query must follow |
| Example queries | `QuestionExample` (the existing confirmed-question store) | Questions already answered with SQL reading the table |

## What is served, and what is withheld

Every annotation is re-checked against the allow-list each time it is served, because a column can be denied
long after something naming it was written (`SemanticLayer.cs`):

- **Key**: the curated key if every column of it is still allowed, else the codebase-interpreted key
  (`CatalogObject.KeyColumns`) under the same condition, else none. A partial key is never served.
- **Joins**: a curated join, then each join discovered from the codebase (`CatalogObjectRelationship`, folded by
  the same code as the object dossier), served only when the other table is in the layer and every column on
  both sides is allowed. A discovered join with the same columns as a curated one is not served twice.
- **Measures**: composed as `SELECT expression AS [measure_value] FROM anchor` and served only when that parses
  to exactly one scalar select over the anchor (no second table, filter, grouping, or subquery; comments and `;`
  are refused outright) and passes `ReadOnlyQueryGuard` and `ColumnPolicyGuard`.
- **Examples**: the newest `QuestionExample` rows whose `ObjectKeys` include the table, served only when their
  SQL passes `ReadOnlyQueryGuard` and `ColumnPolicyGuard`; at most 10 per table.

The admin editor shows every annotation with its state: `served`, or `withheld` with the reason.

Writes are checked the same way: saving a curated key, a relationship, or a measure over a column that is not
allowed is refused with 400. A duplicate measure name, or a relationship repeating the same tables and columns,
is refused with 409.

## Read API (read scope)

| Route | Returns |
| --- | --- |
| `GET /api/v1/semantic-layer` | Instructions, the (database, schema) pairs holding layer tables with counts, and every servable measure |
| `GET /api/v1/semantic-layer/search?q=` | Layer tables matching every word (name, schema, business name, description, synonyms, or an allowed column's name, description, or synonyms), with the matched columns; plus matching measures |
| `GET /api/v1/semantic-layer/tables?database=&schema=` | Layer tables, paged, with business context and allowed column count |
| `GET /api/v1/semantic-layer/tables/describe?key=` | One table's bundle: allowed columns, key, joins with a ready `on` clause, measures, examples, consumers, instructions. 404 when the table is not in the layer |

## Admin API (admin scope)

| Route | Purpose |
| --- | --- |
| `GET /api/v1/powerai/semantic-layer/schemas` | Objects with catalogued columns per (database, schema), and how many are in the layer |
| `GET /api/v1/powerai/semantic-layer/objects?database=&schema=&name=&inLayer=` | Objects with allowed/total column counts, paged |
| `GET /api/v1/powerai/semantic-layer/objects/detail?key=` | One object's editor state: every column with its policy, annotation, curated and discovered joins, measures, examples, each with served/withheld state |
| `PUT /api/v1/powerai/semantic-layer/objects/annotation` | Replace business name, description, synonyms, curated key (all empty removes the annotation) |
| `GET`, `PUT /api/v1/powerai/semantic-layer/instructions` | Read or replace the layer-wide instructions (at most 20000 characters) |
| `GET`, `POST /api/v1/powerai/semantic-layer/measures`; `PUT`, `DELETE .../measures/{id}` | List (with state), create, replace, delete measures |
| `POST /api/v1/powerai/semantic-layer/relationships`; `PUT`, `DELETE .../relationships/{id}` | Create, replace, delete curated relationships (`joinType` `Inner` or `Left`) |

Column allow state, description, and synonyms are written through `PUT /api/v1/powerai/column-policies` (one
column) and `PUT /api/v1/powerai/column-policies/objects` (every column of an object).

## MCP tools

| Tool | Route |
| --- | --- |
| `get_semantic_layer` | `GET /api/v1/semantic-layer` |
| `search_semantic_layer` | `GET /api/v1/semantic-layer/search` |
| `list_semantic_tables` | `GET /api/v1/semantic-layer/tables` |
| `describe_semantic_table` | `GET /api/v1/semantic-layer/tables/describe` |

On the GUI chat and Slack assistants these are the **only** schema tools (`McpOptions.GuiDefaultTools`,
`McpOptions.SlackDefaultTools`). The raw schema readers, which see every catalogued column regardless of the
allow-list (`list_schemas`, `catalog_tree`, `lineage_objects`, `lineage_object_detail`, `lineage_object_columns`,
`describe_object`, `search_all`, `search_objects`, `search_columns`, `search_definitions`, `search_flow_columns`,
`pipeline_columns`, `get_table_key`, `get_table_joins`, `detect_unique_key`), are in `McpOptions.ExcludedTools`.
They remain available to other MCP clients, and the GUI's Catalog pages are unchanged.

## The assistant surface

The tool allowlist decides which tools the chat assistants have; the assistant surface decides what the remaining
tools show them. Operational tools such as `run_statements`, `search_statements`, `search_flows`,
`pipeline_definition`, and `find_similar_questions` return free text (SQL, flow YAML, stored example queries) that
can name any column, so the allow-list is applied to that text too:

- **Marking.** Both chat gateways give their MCP connector `McpOptions.AssistantServerUri`: `Mcp:ServerUrl` with
  `surface=assistant` added to its query. The MCP server reads the query (`request_surface`) and sends
  `X-SqlFlow-Surface: assistant` on every control-plane call of that session (`control_plane.rs`). The caller's
  bearer still decides what it may do; the header only narrows what it is shown, and the model cannot set or remove
  it. A request without the header, from a person or another MCP client, is served exactly as before.
- **Text.** `AssistantRedactionFilter`, on the read and operate endpoint groups, re-serializes a successful JSON
  result for a marked request and withholds every string that contains a column name outside the layer when that
  column's table is named in the same string or in a string of the same or an enclosing JSON object or array. A
  withheld string reads `[withheld: names a column outside the semantic layer]`; a problem result's `detail` is
  withheld the same way. Results that are not a 200 JSON value (streams, files, 201 and 202 responses) pass through
  unchanged. Matching is by identifier token (bare words, `[bracketed]` and `"quoted"` names), case-insensitive,
  and errs toward withholding: a column name that is allowed on one table but denied on another is withheld when
  the denying table is named nearby.
- **Tasks.** `POST /api/v1/datasources/tasks` from a marked request checks the task as the SELECT it would read,
  through `ReadOnlyQueryGuard` and `ColumnPolicyGuard` (`ColumnPolicyGuard.ComposeTaskSelect`), and refuses it with
  400 `Refused by the column allow-list` otherwise:

  | Task | Checked as |
  | --- | --- |
  | `duplicateKeys` | `SELECT <columns> FROM <schema>.<object>`; the key columns must be named |
  | `compareBaseline`, `inventory` mode | Refused: it lists every object of a schema |
  | `compareBaseline`, `schema` mode | `SELECT * FROM <schema>.<object>` |
  | `compareBaseline`, `data` mode | `SELECT <keyExpressions>, <compareColumns, or *> FROM <schema>.<object> WHERE <where>` |
  | Any other operation | Refused: no assistant tool enqueues one |

Object names are not withheld: operational tools still name tables outside the layer (a run's target, a lineage
step), but never their columns.

## GUI

Admin > **Semantic layer** (`/semantic-layer`; `/column-policies` redirects there) has three tabs:

- **Tables**: a database > schema > object tree badged with allowed/total columns (optionally only objects in
  the layer), and for the selected object the About (business name, description, synonyms, key), Columns (allow
  toggle, description, synonyms, reason; allow all, deny all), Relationships (curated and discovered), Measures,
  and Examples tabs. The selected object is in the URL as `?object=`.
- **Instructions & measures**: the layer-wide instructions and every measure.
- **Blocked columns**: every column outside the layer, denied or never reviewed.

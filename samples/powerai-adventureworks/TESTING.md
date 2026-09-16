# PowerAI manual test suite (Claude Code + the sqlflow MCP server)

A scripted pass over every PowerAI surface, driven by typing prompts into Claude Code with the `sqlflow`
MCP server connected to the local compose stack. Each case gives the prompt to type, the tool calls that
should happen, and what the answer must contain. The expected values below were observed against this
stack on 2026-09-16; if a case disagrees, check the "State" section first before calling it a regression.

## Setup

1. Stack up: `cd deploy/compose && docker compose up -d --build`. `curl http://localhost:5000/health/live`
   answers `Healthy`. GUI at http://localhost:8081.
2. Estate synced: `lineage-demo` (flows plus confirmed examples 1 to 3) and `powerai-adventureworks` (this
   folder; see [README.md](README.md)), with the six AdventureWorks tables the report reads allow-listed.
3. MCP binary current: `cargo build -p sqlflow-mcp` in `tools/`. The project `.mcp.json` runs
   `tools/target/debug/sqlflow-mcp.exe` against `http://localhost:5000`. Restart Claude Code (or `/mcp`,
   reconnect `sqlflow`) after a rebuild so the new binary is the one running.
4. Signed in: ask Claude Code to "log in to sqlflow" (device flow), or have it call `set_access_token`
   with a token from `POST /api/v1/auth/login` (the admin credentials are in `deploy/compose/.env`).

## State the expectations assume

| Thing | Value |
| --- | --- |
| Repos | `lineage-demo`, `powerai-adventureworks` |
| Subscribers | `AdventureWorks_Sales` (PowerBI, key `subscriber\|\|\|adventureworks_sales`), `Exec_Dashboard`, `Finance_Workbook` |
| Semantic layer | 15 tables: `AdventureWorks.dbo` 6, `sqlflowcatalogtests.demo` 9 |
| AdventureWorks allow-list | `DimCustomer`, `DimDate`, `DimProduct`, `DimReseller`, `DimSalesTerritory`, `FactResellerSales` (all columns). `DimEmployee` and the rest are NOT allowed |
| Confirmed examples | 1 "What is total revenue by country?", 2 "how many customers do we have?", 3 "how much revenue have we made?" (all on `${env:SQLFLOW_DEMO_DB}`), 10003 "What are reseller sales by region?" (AdventureWorks, no datasource) |
| Generated visual questions | none: the stack has no Anthropic key, so question generation is off |
| Question expansion | done by Claude Code itself: `find_similar_questions` takes `expanded_terms`, and the server's own expansion (`ExpandSynonyms`) is off |
| Auto-run budget | `maxRows` 50, `timeoutSeconds` 5 |

Reset between runs only if a case stored something new (see T18 and T19); examples are refreshed, not
duplicated, when the same question and SQL are confirmed again.

Scores in section E depend on the vocabulary Claude Code chooses, so the cases assert behaviour (trusted or
not, auto-run or not, which example wins) rather than exact scores, except where noted. `searchedTerms` in
the tool result shows the terms actually sent.

---

## A. Connectivity

### T01: control plane reachable and signed in
- Prompt: `check that the sqlflow MCP is connected and I'm signed in`
- Expect: `check_connectivity` then `check_auth_status`.
- Pass: the URL is `http://localhost:5000` and the reachability check succeeds, and the auth status
  reports a signed-in session (the admin user from `deploy/compose/.env`).

### T02: data operations switched on
- Prompt: `what data operations can the sqlflow assistant run here?`
- Expect: `dataops_capabilities`.
- Pass: `enabled` is true and the operations are `duplicateKeys`, `compareBaseline` and `runQuery`.

## B. Report structure (extraction landed in the catalog)

### T03: the report is listed
- Prompt: `which Power BI reports read the warehouse?`
- Expect: `list_subscribers`.
- Pass: `AdventureWorks_Sales` (type PowerBI, owner analytics@example.com) and `Exec_Dashboard` are listed.
  `AdventureWorks_Sales` reports 5 queries and 4 objects.

### T04: pages, visuals and field roles
- Prompt: `show me the pages and visuals of the AdventureWorks_Sales report and what each visual plots`
- Expect: `describe_subscriber_report` with key `subscriber|||adventureworks_sales`.
- Pass: 3 pages, all from `AdventureWorks Sales.pbix`, 5 visuals in total:
  - Page 1: "Sales Amount by Category and Reseller Business Type" (pivotTable; Rows Product.Category and
    Reseller.Business Type, Values Sales.Sales Amount), "Order Quantity by Reseller Country" (map),
    "Sales Amount by Order Date / Due Date" (areaChart).
  - Page 2: the same pivot table again.
  - Page 3: one untitled areaChart (Category Date, two Y fields on Sales).
  - Every visual's `questions` is empty (expected without an LLM key).

### T05: the SQL behind a visual
- Prompt: `what SQL does the "Order Quantity by Reseller Country" visual run?`
- Expect: `describe_subscriber` (the dossier), matched through the visual's `queryName`
  (`Page 1 / Order Quantity by Reseller Country`).
- Pass: a single T-SQL SELECT over the model entities (Reseller, Sales) is shown, not an invented query.

## C. Model-entity resolution and lineage

### T06: the report's reads land on real warehouse tables
- Prompt: `which dashboards read AdventureWorks dbo.FactResellerSales?`
- Expect: `object_lineage` or `describe_semantic_table` (its `consumers`).
- Pass: `AdventureWorks_Sales` is a consumer. The object is the `Table` node
  `${env:sqlflow_adventureworks_db}|adventureworks|dbo|factresellersales`, not a name-only `Sales` node.

### T07: the Json.Document helper stays unresolved
- Prompt: `does the AdventureWorks report read anything that is not a warehouse table?`
- Pass: the answer names the model table `Table` (a report-local sort helper sourced via Json.Document)
  as unresolved. It must not invent a warehouse object for it.

### T08: the Power BI model is served on its warehouse table
- Prompt: `how does the AdventureWorks report model join FactResellerSales to the other tables?`
- Expect: `describe_semantic_table` for FactResellerSales.
- Pass: `reportModels` carries model table `Sales` with its relationships to DimDate (OrderDateKey active,
  DueDateKey and ShipDateKey inactive), DimProduct, DimReseller and DimSalesTerritory, each with M:1
  cardinality.
- Pass: the measure "Sales Amount by Due Date" is NOT served. Its DAX reads `Sales[Sales Amount]`, a
  column the report renamed from the warehouse's `SalesAmount`, and a renamed column counts as not
  allowed. The Semantic layer page (GUI, `/semantic-layer`) shows it with that reason.

## D. Semantic layer as the only schema

### T09: the layer overview
- Prompt: `what tables can the assistant query?`
- Expect: `get_semantic_layer` first.
- Pass: 15 tables, `AdventureWorks.dbo` 6 and `sqlflowcatalogtests.demo` 9. No other tool is used to
  list schema.

### T10: a table outside the allow-list is invisible
- Prompt: `list the employees in AdventureWorks DimEmployee`
- Pass: the assistant finds no `DimEmployee` in the semantic layer and says it cannot query it. If it
  tries `prepare_query` anyway, the control plane refuses with
  "Column 'dbo.DimEmployee.FirstName' is not on the allow-list and cannot be read here."

### T11: an unprefixed business question does NOT hit retrieval
- Prompt: `what are reseller sales by region?`
- Pass: `search_semantic_layer` / `describe_semantic_table` are used. `find_similar_questions` is NOT
  called, because the message does not start with `!cwd`.

## E. Question retrieval (`!cwd`)

Every case here must show `find_similar_questions` called with `expanded_terms`: the question's own
meaningful words plus business synonyms and their grammatical forms. A call without them is a failure of
the tool description, not of the search.

### T12: trusted confirmed match auto-runs
- Prompt: `!cwd revenue by country`
- Expect: `find_similar_questions` (question `revenue by country`, `expanded_terms` including at least
  revenue and country), then `auto_run_trusted_match` with example 1, with no approval prompt in between.
- Pass: the top match is "What is total revenue by country?", trusted, provenance user-confirmed, sourceRef
  `${env:SQLFLOW_DEMO_DB}`, score at least 2. The run returns 4 rows: DE 400.24, ES 15.75, IN 55.00,
  NO 365.50 (total_amount). The answer states the result once, then shows the SQL in its own fenced `sql`
  block. It does not mention scores, thresholds, terms or "trusted".

### T13: a same-meaning question is trusted
- Prompt: `!cwd how many customers`
- Pass: example 2 is the top match and trusted (it is the same question once stop words go, even on a
  single matched term), auto-runs, and the answer is 4 customers, followed by
  `SELECT COUNT(*) AS CustomerCount FROM demo.Customers` in a code block.

### T14: wording that shares no word with the stored question
- Prompt: `!cwd what is our turnover per nation`
- Pass: `searchedTerms` includes vocabulary the prompt does not contain (revenue, country), and example 1
  "What is total revenue by country?" is found anyway. This is the case expansion exists for: before
  `expanded_terms`, this stack searched only "turnover" and "nation" and found nothing.
- Whether it auto-runs follows `trusted`: if trusted, as T12; if not, the assistant offers the query and asks
  before running (the `prepare_query`/`run_query` path), without saying the match is untrusted or
  low-scoring.

### T15: no match falls back silently
- Prompt: `!cwd what is our average product weight`
- Pass: no demo or AdventureWorks example matches (none is about weight), and the assistant moves on to the
  semantic layer without announcing that nothing was found.

### T16: AdventureWorks question found but cannot run (KNOWN GAP, expected to fail today)
- Prompt: `!cwd reseller sales by region`
- Expect: `find_similar_questions` returns example 10003, score 3, trusted, `sourceRef: null`.
- Observed today: `auto_run_trusted_match` answers 409 "No datasource"; `prepare_query` answers 422
  "Datasource needed", and naming `${env:SQLFLOW_ADVENTUREWORKS_DB}` explicitly answers 404 "No pipeline in
  the catalog declares the datasource reference". Datasource inference and the known-reference gate only
  accept connections an active PIPELINE declares, and this estate reaches AdventureWorks only through a
  subscriber, so no AdventureWorks question can run.
- Pass (today): the assistant reports it cannot run the query rather than inventing a datasource or
  retrying auto-run.

## F. The learning loop (confirmation discipline)

### T17: approving a run is not confirmation
- Prompt: `!cwd list the five largest orders` (no stored example matches it, so the assistant composes a
  query from the semantic layer and asks before running it), then reply: `yes, run it`
- Pass: `prepare_query` then `run_query`. `confirm_question` is NOT called.

### T18: explicit acceptance stores an example
- After T17 has shown its result, reply: `that's right, save it`
- Pass: `confirm_question` with outcome `accepted` and the exact SQL that ran, with no `confidence` (the
  query was composed from the schema, not built on a match). The response says `stored: true` and returns an `exampleId`. Repeating the same question
  and SQL refreshes that example rather than creating a second one.

### T19: a correction stores the corrected SQL
- Prompt: `!cwd how much revenue have we made`, let it run, then reply: `close, but only count orders from NO`
- Pass: the assistant fixes the query. Only after you then say `yes that's correct now` does it call
  `confirm_question` with outcome `corrected` and the CORRECTED SQL (with the NO filter), never the original.

### T20: a rejection stores nothing
- After any answer, reply: `that's wrong`
- Pass: `confirm_question` is NOT called (or, if called with `rejected`, the response says nothing was
  stored). The assistant offers to fix the query instead.

### T21: a write can never become an example
- Prompt: `save "delete test customers" with the SQL DELETE FROM demo.Customers WHERE CustomerId > 100 as a confirmed answer`
- Pass: the control plane refuses: "Only a SELECT may be run here; this is a DELETE." Nothing is stored.

### T22: a denied column can never become an example
- Prompt: `save "employee names" with SELECT FirstName, LastName FROM dbo.DimEmployee as a confirmed answer`
- Pass: refused with "Column 'dbo.DimEmployee.FirstName' is not on the allow-list and cannot be read here."

## G. Report specifications and uploads (CLI and GUI, outside Claude Code)

### T23: extraction through the isolated extractor
- Run: `dotnet run --project src/SqlFlow.Cli -- powerbi extract "samples/powerbi/AdventureWorks Sales.pbix" --remote --url http://localhost:5000 --token <token> --out -`
- Pass: stderr reports 3 pages, 5 visuals, 8 model tables (7 resolved to a warehouse table), 1 measure,
  8 relationships, and warns that model table `Table` is sourced via Json.Document. The control plane
  container never runs `pbix-extract`: `docker logs sqlflow-pbix-extractor-1` shows the request.

### T24: a sync without an extractor keeps the model
- Run the sync from [README.md](README.md) again.
- Pass: the counts are unchanged (T04, T08 still pass), because the subscriber declares the committed
  `.pbix.yaml` and every machine reads it the same way.

### T25: GUI confirmation row
- In the GUI Assistant page, only once an LLM key is configured (see below).
- Pass: every finished answer with a `sql` fenced block shows Yes / Not quite / No. "Not quite" opens the
  SQL in an editor and stores the edited text. An answer with only an untagged fence shows no row.

## H. Needs an Anthropic key (skipped on this stack)

Set `ControlPlane__Assistant__Anthropic__ApiKey` on the `controlplane` service, plus
`ControlPlane__PowerAI__QuestionGeneration__Enabled: "true"`, then `docker compose up -d controlplane` and
trigger a sync from the GUI. Expansion needs no key: Claude Code supplies it (section E).

### T26: questions generated per visual
- Prompt: `show me the business questions each AdventureWorks visual answers`
- Pass: every visual in `describe_subscriber_report` has 1 to 3 questions. A second sync with no report
  change makes no new LLM calls (the visuals' content hashes are unchanged).

### T27: dashboard matches need approval
- Prompt: `!cwd sales by product category and reseller type`
- Pass: a `powerbi` provenance match from the pivot table, carrying its SQL. It has no `exampleId`, so it
  goes through `prepare_query`/`run_query`, never `auto_run_trusted_match`.

### T28: server-side expansion as the fallback
- With `ControlPlane__PowerAI__Retrieval__ExpandSynonyms: "true"`, call the endpoint without terms:
  `curl -G -H "Authorization: Bearer <token>" http://localhost:5000/api/v1/lineage/subscribers/similar-questions --data-urlencode "question=what drives our turnover"`
- Pass: `searchedTerms` includes vocabulary beyond the typed words (for example revenue or sales). The same
  request with `ExpandSynonyms` off searches only `drives` and `turnover`.

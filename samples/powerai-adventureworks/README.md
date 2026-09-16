# PowerAI AdventureWorks sample

A one-report estate for exercising PowerAI end to end against the local compose stack
(`deploy/compose`): report extraction, model-entity resolution, the semantic layer, question
retrieval, confirmation, and auto-run.

It holds no flows. Its only content is the `AdventureWorks_Sales` Power BI subscriber and the committed
specification of `samples/powerbi/AdventureWorks Sales.pbix`, whose model reads the `AdventureWorks`
database (AdventureWorksDW2022) that the stack's `adventureworks-init` service restores.

## Syncing it into the local catalog

From the repository root, with the stack up (SQL Server published on the host port from
`deploy/compose`, 14333 by default):

```bash
export SQLFLOW_CATALOG_DB="Server=localhost,14333;Database=SqlFlowCatalog;User ID=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=True"
export SQLFLOW_ADVENTUREWORKS_DB="Server=localhost,14333;Database=AdventureWorks;User ID=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=True"
dotnet run --project src/SqlFlow.Cli -- db sync samples/powerai-adventureworks --repo powerai-adventureworks --connect
```

`--connect` inventories the live `dbo` tables, which the semantic layer needs before any of their
columns can be allowed. PowerAI's column policy is default-deny, so allow the report's tables
afterwards (the Semantic layer page's Allow all, or
`PUT /api/v1/powerai/column-policies/objects` per object).

The worker resolves `${env:SQLFLOW_ADVENTUREWORKS_DB}` from the compose file, so queries against this
report run inside the stack.

## Test plan

[TESTING.md](TESTING.md) is the manual test suite for driving PowerAI from Claude Code through the
`sqlflow` MCP server.

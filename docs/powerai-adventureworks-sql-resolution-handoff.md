# Handoff: repoint the sample .pbix at SQL Server to prove model-entity resolution

Status: waiting on a Windows machine with PowerBI Desktop. Everything Linux-side is done and
verified; this document is the exact remaining steps.

## Why this exists

POWERAI.md Section 10's "Known limitations" says model-entity-to-warehouse-object resolution is
built and unit-tested, but never proven end to end, because the only sample report on file
(`samples/powerbi/AdventureWorks Sales.pbix`) has every table sourced via `Excel.Workbook` or
`Json.Document`, neither of which `tools/pbix-extract`'s resolver (`src/msource.c`) recognizes. Only
the refusal path (a table correctly reported as unresolved) has ever been exercised against a real
report; the resolved path has only 27 unit tests, no live proof.

This was confirmed directly in a manual test session: syncing the sample report produced warnings
like `model table 'Sales' is sourced via Excel.Workbook, which names no warehouse object`, for every
one of its 8 tables, and the rendered SQL for every visual came back with model/DAX-style names
(`FROM [Sales] AS [s]`, `[s].[Sales Amount by Due Date]`) rather than resolved warehouse identifiers.

## What is already done (Linux side, verified)

A real SQL Server database now exists with the exact dimensional shape this report's model expects:
Microsoft's official **AdventureWorksDW2022** sample (the data-warehouse edition: `DimCustomer`,
`DimProduct`, `DimReseller`, `DimDate`, `DimSalesTerritory`, `FactResellerSales`, ...), restored as
database `AdventureWorks`.

- `deploy/docker/adventureworks-restore.sh`: downloads the official `.bak` from
  `github.com/Microsoft/sql-server-samples` and restores it, idempotently (skips once the database
  exists).
- `deploy/compose/docker-compose.yml`: a new `adventureworks-init` one-shot service runs that script
  against the stack's `mssql` service on `docker compose up`.
- Verified live, twice: a fresh restore (12,242 pages, ~26 dimensional/fact tables) and a re-run that
  correctly skipped. Column names for every table this report's model needs were read directly from
  the restored database (below).

**AdventureWorksLT2022 (the OLTP/normalized edition) was tried first and rejected**: its schema
(`SalesLT.Customer`, `SalesLT.Product`, `SalesLT.SalesOrderHeader`, ...) is transactional, not
dimensional, and has no `Reseller`, no `Sales Territory`, no denormalized `Sales` fact, so nothing in
it matches this report's model despite the shared "AdventureWorks" name.

## What is NOT done: the .pbix's Power Query still reads Excel

Repointing the database does nothing by itself. The `.pbix` file's model still names
`Excel.Workbook(File.Contents("..."), ...)` as every table's source; that M expression lives inside
the compressed, binary `DataModel` part of the `.pbix` (an XPress9-encoded ABF container with an
embedded SQLite catalog), not as editable plain text. `tools/pbix-extract` only reads that format;
nothing in this repo writes it. The only faithful way to change it is PowerBI Desktop, which is
Windows-only.

## Steps to do on a Windows machine with PowerBI Desktop

1. Bring up the compose stack (or otherwise get `AdventureWorksDW2022` reachable from the Windows
   machine): `docker compose -f deploy/compose/docker-compose.yml up -d mssql adventureworks-init`.
   Confirm the `AdventureWorks` database exists and has data (`SELECT COUNT(*) FROM
   dbo.FactResellerSales` should return a large number).
2. Open `samples/powerbi/AdventureWorks Sales.pbix` in PowerBI Desktop.
3. Open **Transform data** (Power Query Editor). For each of the 8 tables, replace its `Source` step
   with `Sql.Database("<host>", "AdventureWorks")` and select the matching table below instead of the
   Excel sheet reference. Use **Table.SelectColumns** (or just select the fewer/renamed columns
   directly in the query editor) to keep only the columns the model actually uses, renamed to match
   what the model's existing column names expect — the DW schema uses different column names than
   the Excel export did, so a rename step per table is required, not just a source swap.

   | Report table | New source | Column rename (Excel name → DW column) |
   |---|---|---|
   | `Customer` | `dbo.DimCustomer` | `CustomerKey`→`CustomerKey`, `Customer ID`→`CustomerAlternateKey`, `Customer`→ concat of `FirstName`+`LastName` (or `EnglishOccupation` if a closer analog is wanted — there is no single "Customer" display name column; a computed column is needed), `City`/`State-Province`/`Country-Region`/`Postal Code`→ join to `DimGeography` via `GeographyKey` (DW normalizes geography out of `DimCustomer`) |
   | `Date` | `dbo.DimDate` | `DateKey`→`DateKey`, `Date`→`FullDateAlternateKey`, `Fiscal Year`→`FiscalYear`, `Fiscal Quarter`→`FiscalQuarter`, `Month`→`EnglishMonthName` (or derive from `FullDateAlternateKey`), `Full Date`→`FullDateAlternateKey`, `MonthKey`→ derive from `CalendarYear`*100+`MonthNumberOfYear` (DW has no single MonthKey column) |
   | `Product` | `dbo.DimProduct` (join `dbo.DimProductSubcategory`, `dbo.DimProductCategory`) | `ProductKey`→`ProductKey`, `SKU`→`ProductAlternateKey`, `Product`→`EnglishProductName`, `Standard Cost`→`StandardCost`, `Color`→`Color`, `List Price`→`ListPrice`, `Model`→`ModelName`, `Subcategory`→`DimProductSubcategory.EnglishProductSubcategoryName`, `Category`→`DimProductCategory.EnglishProductCategoryName` |
   | `Reseller` | `dbo.DimReseller` | `ResellerKey`→`ResellerKey`, `Reseller ID`→`ResellerAlternateKey`, `Business Type`→`BusinessType`, `Reseller`→`ResellerName`, `City`/`State-Province`/`Country-Region`/`Postal Code`→ join to `DimGeography` via `GeographyKey` |
   | `Sales` | `dbo.FactResellerSales` | `SalesOrderLineKey`→ composite of `SalesOrderNumber`+`SalesOrderLineNumber` (DW has no single surrogate line key), `ResellerKey`→`ResellerKey`, `CustomerKey`→ N/A (FactResellerSales has no CustomerKey; it is reseller-channel only — drop or repoint the report's Customer relationship to go through Reseller/Geography instead), `ProductKey`→`ProductKey`, `OrderDateKey`→`OrderDateKey`, `DueDateKey`→`DueDateKey`, `ShipDateKey`→`ShipDateKey`, `SalesTerritoryKey`→`SalesTerritoryKey`, `Order Quantity`→`OrderQuantity`, `Unit Price`→`UnitPrice`, `Extended Amount`→`ExtendedAmount`, `Unit Price Discount Pct`→`UnitPriceDiscountPct`, `Product Standard Cost`→`ProductStandardCost`, `Total Product Cost`→`TotalProductCost`, `Sales Amount`→`SalesAmount` |
   | `Sales Order` | `dbo.FactResellerSales` (distinct `SalesOrderNumber`/`SalesOrderLineNumber`) | `SalesOrderLineKey`→ composite key as above, `Sales Order`→`SalesOrderNumber`, `Sales Order Line`→`SalesOrderLineNumber`, `Channel`→ literal `"Reseller"` (DW has no Channel column; this report's Channel concept does not exist in FactResellerSales, which is reseller-only — consider dropping this column) |
   | `Sales Territory` | `dbo.DimSalesTerritory` | `SalesTerritoryKey`→`SalesTerritoryKey`, `Region`→`SalesTerritoryRegion`, `Country`→`SalesTerritoryCountry`, `Group`→`SalesTerritoryGroup` |
   | `Table` (the small Category/Sorting helper table) | leave as-is (`Json.Document`) | This table is report-local metadata (a sort-order helper for Product Category), not a warehouse concept; it has no DW equivalent and does not need to move. |

   **The `Customer`/`Sales` mismatch is the one real modeling problem**, not just a rename: DW's
   `FactResellerSales` has no `CustomerKey` at all (this DW ships reseller-channel sales only; retail
   customer sales live in the separate `FactInternetSales` against `DimCustomer`, an unrelated fact).
   The report's current model treats `Customer` and `Reseller` as siblings both hanging off `Sales`.
   The faithful DW equivalent is to drop the `Customer`↔`Sales` relationship (or repoint the
   `Customer` table to `FactInternetSales` and accept it becomes an independent, unrelated table in
   this report, which changes what the model can join). Decide this explicitly rather than silently
   dropping rows; either choice is a real modeling call, not a mechanical rename.

4. Save the file (**File > Save**, keep it as `AdventureWorks Sales.pbix` or save a new copy such as
   `AdventureWorks Sales (SQL).pbix` if you want to keep the Excel-sourced original for comparison in
   the estate).
5. Copy the changed `.pbix` back to this repo (or hand it to me directly) and I'll re-run the same
   sync used in the earlier manual test:
   ```
   sqlflow db sync . --repo powerai-manual-test --connect
   ```
   with `SQLFLOW_PBIX_EXTRACT` pointed at `tools/pbix-extract/build/pbix-extract`. Where it was
   `Excel.Workbook`/`Json.Document` warnings before, it should now report `sourceServer`/
   `sourceDatabase`/`sourceSchema`/`sourceName` for every resolved table, and the rendered SQL for
   each visual should carry real `[dbo].[DimReseller]`/`[dbo].[FactResellerSales]`-shaped identifiers
   once the `LineageGraphBuilder` synonym pass rewrites the model names, rather than the current
   `FROM [Sales] AS [s]`.

## What to bring back to me

The updated `.pbix` file (or its path if it's already back in this repo). I'll do the sync, confirm
the resolved `SynonymLink`s and rendered SQL for real, and update POWERAI.md Section 10's "Known
limitations" from "built but unproven" to verified, with the actual evidence.

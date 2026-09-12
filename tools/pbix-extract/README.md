# pbix-extract

Reads a Power BI report's semantic model and writes it as a SQLFlow YAML specification: the
tables and columns that exist, what each measure computes (its DAX), which columns are themselves
computed, how the tables relate, and where each table's data comes from.

The point is to state what a model MEANS, in a form a SQL author, or a language model asked to
write SQL, can work from. A warehouse schema says what columns exist; it does not say that
"Sales Amount by Due Date" means `CALCULATE(SUM(Sales[Sales Amount]), USERELATIONSHIP(...))`, or
that `Sales.DueDateKey -> Date.DateKey` is a real relationship that is deliberately inactive.
That knowledge lives only in the report, and this extracts it.

## Why this is a standalone binary

A `.pbix` report's `DataModel` part is a compressed Analysis Services backup: an XPress9 block
stream wrapping a container whose directory is UTF-16 XML, wrapping a SQLite database that holds
the model's metadata. Reading it means running a decompressor over a binary file that, from
SQLFlow's point of view, is untrusted input.

So this is a separate executable, built outside `SqlFlow.sln`, run offline against files already
on disk. Nothing here links into the SQLFlow control plane, and the only thing that crosses back
is reviewed YAML text. A malformed or hostile report can at worst crash this tool.

The report's *visual* layer (pages, visuals, which fields sit in which role) is a different
matter: it is plain UTF-16 JSON in the `Report/Layout` part, carries no native-code risk, and is
read inside SQLFlow directly by `SqlFlow.PowerBi` at every `sqlflow db sync` via a subscriber's
`pbix:` key. The two are complementary — that half tracks the questions a report asks, this half
describes the model those questions are asked against.

## Build

Needs a C11 compiler and the development packages for libzip, expat, and SQLite (with
`sqlite3_deserialize`, i.e. SQLite 3.23 or newer):

```sh
make
```

The binary lands at `build/pbix-extract`. `make clean` removes the build tree.

## Usage

```sh
build/pbix-extract "reports/AdventureWorks Sales.pbix"                 # to standard output
build/pbix-extract report.pbix --name Sales_Report --out spec.yaml     # to a file
```

`--name` sets the subscriber key in the emitted YAML (default: the file's base name with spaces
replaced by underscores). `--out` writes to a file instead of standard output.

The output is a fragment, not a complete `subscribers.yaml`. Merging it into the estate means
adding the things the `.pbix` does not itself declare: `owner:`, `description:`, `url:`, and the
`server:` connection alias the report reads through.

## What it reads, and what it does not

Read from the model's embedded metadata:

- **tables / columns** with the model's declared data type for each
- **measures**: name, home table, DAX expression, description
- **calculatedColumns**: name, home table, DAX expression
- **relationships**: endpoints, cardinality (`M:1`, `1:1`, …), and whether the relationship is
  active
- **tableSources**: each table's Power Query (M) expression

Power BI's auto-generated date-hierarchy tables (the `LocalDateTable_*` /
`DateTableTemplate_*` scaffolding, flagged in the model as system objects) are excluded: nobody
authored them, and they would bury the real model in noise.

Not read: the report's visual layer (see above), row-level security, perspectives, translations,
KPIs, and the column data itself. A model whose inner files are XPress8-compressed
(`ApplyCompression`) is reported as unsupported rather than silently returning nothing.

## Schema variation

Power BI's embedded metadata schema changes between versions: columns are renamed and added. Every
query this tool runs is built against the columns the database actually has, probed at runtime —
for example a relationship's endpoints are `FromColumnID` on current models and
`FromEndColumnID` on older ones.

Where a needed table or column is missing in both spellings, the tool stops and says which one it
could not find, rather than returning an empty section. An empty model and an unreadable one mean
very different things, and conflating them is how a wrong answer gets trusted.

This has been verified against one real report. Other Power BI versions may use schema shapes the
probes do not yet cover; the failure mode is a stated error naming the unexpected column, which is
enough to extend the probe.

## Vendored code

`vendor/xpress9/` is the XPress9 reference decoder, MIT licensed (see
`vendor/xpress9/LICENSE`), taken from [xpress9-python](https://github.com/Hugoberry/xpress9-python),
which wraps Microsoft's own reference implementation. Only the decode path is vendored; the
encoder translation units are deliberately absent, since a report is only ever read. The sources
are compiled with warnings suppressed, because they are upstream's code rather than this
project's (this project's own sources build clean under `-Wall -Wextra -Wpedantic`).

One deliberate divergence from upstream, marked `LOCAL FIX` in the source:
`Xpress9DecLz77.c` shifted its sliding window with `memcpy` on overlapping ranges (the source and
destination are the same buffer, and they overlap whenever the shift distance is less than the
window size, which is the ordinary case). That is undefined behavior, and AddressSanitizer flags
it as `memcpy-param-overlap`. It is now `memmove`. The change produces byte-identical output on
the reports tested, but the original relied on a particular libc's forward-copy behavior, and a
silently corrupted decode window would surface much later as plausible-looking nonsense.

The tool is checked under AddressSanitizer and UndefinedBehaviorSanitizer against a real report;
both are silent, with no leaks. Since this code parses a binary file that should be treated as
untrusted, that check is part of the build-and-verify routine rather than a one-off:

```sh
# from tools/pbix-extract
mkdir -p build-asan
for f in src/*.c; do
  cc -O1 -g -fsanitize=address,undefined -fno-omit-frame-pointer -std=c11 \
     -D_POSIX_C_SOURCE=200809L -Isrc -Ivendor/xpress9/include \
     $(pkg-config --cflags libzip expat sqlite3) -c "$f" -o "build-asan/$(basename "$f" .c).o"
done
for f in vendor/xpress9/src/*.c; do
  cc -O1 -g -fsanitize=address,undefined -fno-omit-frame-pointer -std=c11 -DBUILD_STATIC \
     -Ivendor/xpress9/include -w -c "$f" -o "build-asan/$(basename "$f" .c).o"
done
cc -fsanitize=address,undefined build-asan/*.o -o build-asan/pbix-extract \
   $(pkg-config --libs libzip expat sqlite3)
ASAN_OPTIONS=detect_leaks=1 ./build-asan/pbix-extract report.pbix > /dev/null
```

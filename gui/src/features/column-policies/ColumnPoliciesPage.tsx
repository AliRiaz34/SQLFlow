import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, ShieldAlert } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Sheet, SheetContent, SheetFooter, SheetHeader, SheetTitle,
} from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { isApiError } from "../../api/client";
import { columnPolicyApi, searchApi } from "../../api/endpoints";
import type { ColumnPolicyState, ObjectHit, RestrictedColumn } from "../../api/types";
import { ConnectionRef } from "../../components/ConnectionRef";
import { CorrelationError } from "../../components/CorrelationError";
import { FilterBar } from "../../components/FilterBar";
import { EmptyState } from "../../components/EmptyState";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";

/** One error-to-text mapping for every toast on this page (the API's detail wins over a generic title). */
function errorText(error: unknown): string {
  if (isApiError(error)) {
    return error.detail ?? error.title;
  }

  return error instanceof Error ? error.message : String(error);
}

const OBJECT_POLICY_KEY = (objectKey: string) => ["column-policies", "object", objectKey] as const;
const OVERVIEW_LIST_KEY = ["column-policies", "list"] as const;

/**
 * One column of the managed object: a restriction switch and an optional free-text reason, saved
 * independently of each other. The switch saves immediately (the point of a toggle); the reason saves on
 * blur so a click through several words does not fire a request per keystroke. Both send the other field's
 * current value along, since the endpoint is a full upsert of the row.
 */
function ColumnPolicyRow({ objectKey, column }: { objectKey: string; column: ColumnPolicyState }) {
  const queryClient = useQueryClient();
  const [reason, setReason] = useState(column.reason ?? "");

  useEffect(() => {
    setReason(column.reason ?? "");
  }, [column.reason]);

  const save = useMutation({
    mutationFn: (isSensitive: boolean) => columnPolicyApi.set({
      objectKey,
      columnName: column.columnName,
      isSensitive,
      reason: reason.trim() === "" ? null : reason.trim(),
    }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: OBJECT_POLICY_KEY(objectKey) });
      void queryClient.invalidateQueries({ queryKey: OVERVIEW_LIST_KEY });
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const reasonDirty = reason.trim() !== (column.reason ?? "");

  return (
    <TableRow data-testid="column-policy-row">
      <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px] text-muted-foreground">
        {column.ordinal}
      </TableCell>
      <TableCell className="whitespace-nowrap px-3 py-1.5">
        <span className="font-mono text-[12px] font-medium">{column.columnName}</span>
      </TableCell>
      <TableCell className="whitespace-nowrap px-3 py-1.5">
        <Mono>{column.dataType ?? "-"}</Mono>
        {column.nullable && <span className="ml-1 text-xs text-muted-foreground">null</span>}
      </TableCell>
      <TableCell className="whitespace-nowrap px-3 py-1.5">
        <div className="flex items-center gap-2">
          <Switch
            checked={column.isSensitive}
            disabled={save.isPending}
            onCheckedChange={(checked) => save.mutate(checked)}
            aria-label={`Restrict ${column.columnName}`}
            data-testid="column-policy-toggle"
          />
          {save.isPending && <Loader2 className="size-3.5 animate-spin text-muted-foreground" />}
        </div>
      </TableCell>
      <TableCell className="px-3 py-1.5">
        <Input
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          onBlur={() => {
            if (reasonDirty) {
              save.mutate(column.isSensitive);
            }
          }}
          placeholder="Reason (optional)"
          className="h-7 w-56 text-[12px]"
          disabled={save.isPending}
          data-testid="column-policy-reason"
        />
      </TableCell>
      <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px] text-muted-foreground">
        {column.updatedBy ?? "-"} <RelativeTime value={column.updatedUtc} />
      </TableCell>
    </TableRow>
  );
}

const MANAGE_HEADERS = ["#", "Column", "Type", "Restricted", "Reason", "Last changed"];

/** The per-table toggle list, in a right side sheet (DESIGN.md 7.4): every column of the selected object with
 * its current restriction state, editable inline. */
function ManageColumnsSheet({
  objectKey,
  objectLabel,
  onClose,
}: {
  objectKey: string;
  objectLabel: string;
  onClose: () => void;
}) {
  const query = useQuery({
    queryKey: OBJECT_POLICY_KEY(objectKey),
    queryFn: () => columnPolicyApi.forObject(objectKey),
  });

  return (
    <Sheet
      open
      onOpenChange={(next) => {
        if (!next) {
          onClose();
        }
      }}
    >
      <SheetContent className="w-full sm:max-w-3xl" data-testid="column-policy-sheet">
        <SheetHeader>
          <SheetTitle>Restricted columns for {objectLabel}</SheetTitle>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-3 overflow-y-auto px-4">
          {query.isError && (
            isApiError(query.error)
              ? <CorrelationError error={query.error} />
              : <p className="text-[13px] text-destructive">{errorText(query.error)}</p>
          )}
          <p className="text-[13px] text-muted-foreground">
            Restricting a column hides it from the assistant&apos;s schema search and describe results, and
            refuses any ad-hoc query that touches it. Each toggle saves immediately; a reason saves when you
            leave the field.
          </p>
          <Table>
            <TableHeader>
              <TableRow className="hover:bg-transparent">
                {MANAGE_HEADERS.map((header) => (
                  <TableHead key={header} className="h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground">
                    {header}
                  </TableHead>
                ))}
              </TableRow>
            </TableHeader>
            <TableBody>
              {query.data === undefined && !query.isError && Array.from({ length: 6 }, (_, i) => (
                <TableRow key={`skeleton-${i}`}>
                  {MANAGE_HEADERS.map((header) => (
                    <TableCell key={header} className="px-3 py-2">
                      <Skeleton className="h-4 w-full" />
                    </TableCell>
                  ))}
                </TableRow>
              ))}
              {query.data?.map((column) => (
                <ColumnPolicyRow key={column.columnName} objectKey={objectKey} column={column} />
              ))}
            </TableBody>
          </Table>
          {query.data !== undefined && query.data.length === 0 && (
            <EmptyState title="This object has no known columns yet" description="Sync the catalog first." />
          )}
        </div>
        <SheetFooter className="flex-row justify-end">
          <Button size="sm" onClick={onClose} data-testid="column-policy-sheet-done">Done</Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}

const objectColumns: Column<ObjectHit>[] = [
  { id: "name", header: "Name", render: (row) => <span className="font-mono text-[12px] font-medium">{row.name}</span> },
  { id: "kind", header: "Kind", render: (row) => <Badge variant="secondary">{row.kind}</Badge> },
  { id: "schema", header: "Schema", render: (row) => <Mono>{row.schema ?? "-"}</Mono> },
  { id: "database", header: "Database", render: (row) => <Mono>{row.database ?? "-"}</Mono> },
  { id: "serverRef", header: "Server", render: (row) => <ConnectionRef value={row.serverRef} /> },
];

/**
 * Governs which catalog columns the AI assistant (and the ad-hoc query surface generally) may never read:
 * find a table, toggle its sensitive columns, and audit every restriction currently in effect across the
 * whole catalog. Admin-scope, mirroring UsersPage's shape (a searchable list plus a side-sheet editor).
 */
export default function ColumnPoliciesPage() {
  const [searchInput, setSearchInput] = useState("");
  const [searchTerm, setSearchTerm] = useState("");
  const [selected, setSelected] = useState<{ key: string; label: string } | null>(null);

  // The table search debounces keystrokes so each pause, not each character, costs an API call.
  useEffect(() => {
    const timer = window.setTimeout(() => setSearchTerm(searchInput.trim()), 400);
    return () => window.clearTimeout(timer);
  }, [searchInput]);

  const overviewColumns: Column<RestrictedColumn>[] = [
    {
      id: "objectName",
      header: "Object",
      render: (row) => <span className="font-mono text-[12px] font-medium">{row.objectName}</span>,
    },
    { id: "database", header: "Database", render: (row) => <Mono>{row.database ?? "-"}</Mono> },
    { id: "schema", header: "Schema", render: (row) => <Mono>{row.schema ?? "-"}</Mono> },
    { id: "columnName", header: "Column", render: (row) => <Mono>{row.columnName}</Mono> },
    {
      id: "reason",
      header: "Reason",
      render: (row) => (row.reason ? row.reason : <span className="text-muted-foreground">-</span>),
    },
    {
      id: "updatedBy",
      header: "Changed by",
      render: (row) => (row.updatedBy ? row.updatedBy : <span className="text-muted-foreground">-</span>),
    },
    { id: "updatedUtc", header: "Changed", render: (row) => <RelativeTime value={row.updatedUtc} /> },
    {
      id: "actions",
      header: "Actions",
      align: "right",
      render: (row) => (
        <Button
          variant="ghost"
          size="xs"
          onClick={() => setSelected({ key: row.objectKey, label: row.objectName })}
          data-testid="column-policy-overview-manage"
        >
          Manage
        </Button>
      ),
    },
  ];

  return (
    <Page data-testid="page-column-policies">
      <PageHeader
        title="Column policies"
        subtitle="Restrict columns the AI assistant may never see. A restricted column is hidden from schema search and describe, and any ad-hoc query touching it is refused at execution time."
      />

      <div className="flex flex-col gap-2">
        <h2 className="text-[13px] font-semibold">Find a table</h2>
        <FilterBar>
          <Input
            value={searchInput}
            onChange={(e) => setSearchInput(e.target.value)}
            placeholder="Search tables and views by name"
            aria-label="Table or view name"
            className="h-8 w-72"
            data-testid="column-policy-search"
          />
        </FilterBar>

        {searchTerm === "" ? (
          <EmptyState
            icon={<ShieldAlert />}
            title="Type a table or view name to find it"
            description="Pick a result to open its column list and toggle which columns are restricted."
            data-testid="column-policy-search-hint"
          />
        ) : (
          <PagedTable<ObjectHit>
            queryKey={["column-policies", "search", searchTerm]}
            fetchPage={(page, pageSize) => searchApi.objects(searchTerm, { page, pageSize })}
            columns={objectColumns}
            rowKey={(row) => row.key}
            onRowClick={(row) => setSelected({ key: row.key, label: row.name })}
            emptyMessage={`No tables or views match "${searchTerm}".`}
            data-testid="column-policy-search-table"
          />
        )}
      </div>

      <div className="flex flex-col gap-2">
        <h2 className="text-[13px] font-semibold">Restricted columns</h2>
        <PagedTable<RestrictedColumn>
          queryKey={OVERVIEW_LIST_KEY}
          fetchPage={(page, pageSize) => columnPolicyApi.list({ page, pageSize })}
          columns={overviewColumns}
          rowKey={(row) => `${row.objectKey}::${row.columnName}`}
          emptyMessage="No columns are currently restricted."
          data-testid="column-policy-overview-table"
        />
      </div>

      {selected !== null && (
        <ManageColumnsSheet
          objectKey={selected.key}
          objectLabel={selected.label}
          onClose={() => setSelected(null)}
        />
      )}
    </Page>
  );
}

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Eye, Trash2, Upload } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { semanticLayerApi } from "../../api/endpoints";
import type { SemanticReportSpec } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { DataTable, type Column } from "../../components/DataTable";
import { RelativeTime } from "../../components/RelativeTime";
import { ReportUploadDialog } from "./ReportUploadDialog";
import { describeReport, errorText, refreshSemanticLayer, SEMANTIC_ROOT } from "./shared";

function OriginBadge({ report }: { report: SemanticReportSpec }) {
  const [label, hint] = report.origin === "upload"
    ? ["uploaded", "Added by a person. It stays until someone deletes it."]
    : ["kept by sync", "A sync extracted this report from the .pbix the repository declares and kept a copy, so a "
      + "sync that cannot extract it (the control plane never can) still serves it. The next sync that can extract "
      + "the report refreshes it."];
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="inline-flex"><Badge variant="outline" className="text-[11px]">{label}</Badge></span>
      </TooltipTrigger>
      <TooltipContent className="max-w-sm">{hint}</TooltipContent>
    </Tooltip>
  );
}

function ReportDetailDialog({ id, onClose }: { id: number; onClose: () => void }) {
  const detail = useQuery({
    queryKey: [SEMANTIC_ROOT, "reports", id],
    queryFn: () => semanticLayerApi.report(id),
  });

  return (
    <Dialog open onOpenChange={(next) => !next && onClose()}>
      <DialogContent className="sm:max-w-4xl" data-testid="semantic-report-detail">
        <DialogHeader>
          <DialogTitle>{detail.data?.report.reportFile ?? "Report"}</DialogTitle>
          <DialogDescription>
            {detail.data === undefined
              ? "Loading the stored specification."
              : `${detail.data.report.subscriberName} in ${detail.data.report.repoName}: ${describeReport(detail.data.summary)}. `
                + `${detail.data.summary.resolvedTables} of its model tables load from a warehouse table.`}
          </DialogDescription>
        </DialogHeader>
        {detail.isError && <p className="text-[13px] text-destructive">{errorText(detail.error)}</p>}
        {detail.data === undefined ? <Skeleton className="h-96 w-full" /> : (
          <div className="flex flex-col gap-3">
            {detail.data.summary.warnings.length > 0 && (
              <ul className="list-disc rounded-md border border-warning/60 p-2 pl-6 text-[12px] text-warning">
                {detail.data.summary.warnings.map((warning) => <li key={warning}>{warning}</li>)}
              </ul>
            )}
            <CodeView value={detail.data.spec} language="yaml" lsp={false} height={480} />
          </div>
        )}
        <DialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose}>Close</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/**
 * The Power BI reports the semantic layer holds. A report's pages, visuals and model reach the catalog through its
 * specification; this is where a person adds one the repository does not carry, by uploading the .pbix (read by the
 * isolated extractor, never by the control plane) or a specification made with `sqlflow powerbi extract`. The copies a
 * sync kept of reports the repository declares are listed too, so it is visible what the assistant is served.
 */
export function ReportsPanel() {
  const queryClient = useQueryClient();
  const [uploading, setUploading] = useState(false);
  const [viewing, setViewing] = useState<number | null>(null);
  const [pendingDelete, setPendingDelete] = useState<SemanticReportSpec | null>(null);

  const reports = useQuery({ queryKey: [SEMANTIC_ROOT, "reports"], queryFn: () => semanticLayerApi.reports() });

  const remove = useMutation({
    mutationFn: (id: number) => semanticLayerApi.deleteReport(id),
    onSuccess: (result, id) => {
      const removed = reports.data?.find((report) => report.id === id);
      toast.success(`Report '${removed?.reportFile ?? id}' removed.`, {
        description: result.syncQueued
          ? "A sync was queued to drop what it contributed."
          : "Run a sync of the repo to drop what it contributed.",
      });
      setPendingDelete(null);
      refreshSemanticLayer(queryClient);
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const columns: Column<SemanticReportSpec>[] = [
    {
      id: "report",
      header: "Report",
      render: (row) => <span className="break-all font-mono text-[12px] font-medium">{row.reportFile}</span>,
    },
    {
      id: "subscriber",
      header: "Subscriber",
      render: (row) => (
        <div className="flex flex-col">
          <span className="text-[13px]">{row.subscriberName}</span>
          <span className="text-[11px] text-muted-foreground">{row.repoName}</span>
        </div>
      ),
    },
    { id: "origin", header: "Origin", width: 120, render: (row) => <OriginBadge report={row} /> },
    {
      id: "contents",
      header: "Contents",
      render: (row) => <span className="text-[12px] text-muted-foreground">{describeReport(row)}</span>,
    },
    {
      id: "updated",
      header: "Stored",
      width: 170,
      render: (row) => (
        <span className="text-[12px]">
          {row.updatedBy ?? "sync"}{" "}
          <span className="text-muted-foreground"><RelativeTime value={row.updatedUtc} /></span>
        </span>
      ),
    },
    {
      id: "status",
      header: "Status",
      width: 110,
      render: (row) => row.subscriberDeclared
        ? <Badge variant="secondary" className="text-[11px]">served</Badge>
        : (
          <Tooltip>
            <TooltipTrigger asChild>
              <span className="inline-flex">
                <Badge variant="outline" className="border-warning/60 text-[11px] text-warning">not served</Badge>
              </span>
            </TooltipTrigger>
            <TooltipContent className="max-w-sm">
              The repository no longer declares this subscriber, so the report is not read by any sync. Declare it again
              in subscribers.yaml, or delete the report.
            </TooltipContent>
          </Tooltip>
        ),
    },
    {
      id: "actions",
      header: "Actions",
      align: "right",
      width: 100,
      render: (row) => (
        <div className="flex justify-end gap-1">
          <Button
            variant="ghost"
            size="icon"
            className="size-7"
            aria-label={`View the specification of ${row.reportFile}`}
            onClick={(event) => {
              event.stopPropagation();
              setViewing(row.id);
            }}
            data-testid="semantic-report-view"
          >
            <Eye />
          </Button>
          <Button
            variant="ghost"
            size="icon"
            className="size-7"
            aria-label={`Remove ${row.reportFile}`}
            onClick={(event) => {
              event.stopPropagation();
              setPendingDelete(row);
            }}
            data-testid="semantic-report-delete"
          >
            <Trash2 />
          </Button>
        </div>
      ),
    },
  ];

  return (
    <div className="flex flex-col gap-3" data-testid="semantic-reports-panel">
      <div className="flex items-start gap-3">
        <p className="mr-auto max-w-3xl text-[13px] text-muted-foreground">
          Power BI reports behind the repositories' Power BI subscribers. Their measures, relationships and visuals are
          what the assistant learns the business vocabulary from. Add a report here when it is not committed to the
          repository; one that is committed as a <span className="font-mono">.pbix.yaml</span> specification is read
          from there and needs no upload.
        </p>
        <Button size="sm" onClick={() => setUploading(true)} data-testid="semantic-report-upload">
          <Upload />
          Upload report
        </Button>
      </div>
      {reports.isError && <p className="text-[13px] text-destructive">{errorText(reports.error)}</p>}
      <DataTable<SemanticReportSpec>
        columns={columns}
        rows={reports.data}
        rowKey={(row) => row.id}
        onRowClick={(row) => setViewing(row.id)}
        minWidth={900}
        emptyMessage="No report is held here yet. Upload one, or commit a specification next to subscribers.yaml."
        data-testid="semantic-reports-table"
      />
      {uploading && <ReportUploadDialog onClose={() => setUploading(false)} />}
      {viewing !== null && <ReportDetailDialog id={viewing} onClose={() => setViewing(null)} />}
      <ConfirmDialog
        open={pendingDelete !== null}
        title={`Remove ${pendingDelete?.reportFile ?? "this report"}?`}
        message={pendingDelete?.origin === "extracted"
          ? "The next sync that cannot extract this report will stop serving it. A sync that can extract it stores it again."
          : "The report's pages, visuals and model stop being served once the repo syncs."}
        confirmLabel="Remove"
        danger
        busy={remove.isPending}
        onConfirm={() => pendingDelete !== null && remove.mutate(pendingDelete.id)}
        onClose={() => setPendingDelete(null)}
      />
    </div>
  );
}

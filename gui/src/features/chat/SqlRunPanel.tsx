// The chat's "Run" affordance on a finished SQL block: pick a datasource, run the query through the same
// human-in-the-loop DataOps path prepare_query/run_query use over MCP (prepare mints a token, run redeems
// it), then show the result as a fitting chart or a table (QueryResultView). The SQL is already fully
// visible in the block above this panel, so clicking Run is the person's approval; there is no second
// confirmation step, the same way running a confirmed example needs no re-approval (POWERAI.md Section 6).
//
// Exposed as a hook rather than one component because the button belongs INSIDE CodeView's own toolbar row
// (its `extraActions` slot, beside format/copy) while the result panel is a sibling below CodeView; both
// need the same run state, so one hook owns it and hands back the two pieces to place separately.

import { useContext, useMemo, useState, type ReactNode } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { Play } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { ComboBoxField } from "../../components/ComboBoxField";
import { CorrelationError } from "../../components/CorrelationError";
import { isApiError } from "../../api/client";
import { datasourceApi, executeQueryRun } from "../../api/endpoints";
import type { Datasource, RunQueryResult } from "../../api/types";
import { ThreadComponentsContext } from "../../components/assistant-ui/thread";
import { QueryResultView } from "./QueryResultView";

type Phase = "idle" | "picking" | "running" | "done" | "failed";

export function useSqlRun(sql: string): { button: ReactNode; panel: ReactNode } {
  const dataOpsRunQuery = useContext(ThreadComponentsContext).dataOpsRunQuery ?? false;
  const [phase, setPhase] = useState<Phase>("idle");
  const [sourceRef, setSourceRef] = useState<string | null>(null);
  const [result, setResult] = useState<RunQueryResult | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  const datasources = useQuery({
    queryKey: ["datasources"],
    queryFn: () => datasourceApi.list(),
    enabled: phase !== "idle",
  });

  // Only a datasource a worker can actually reach is offered: preparing against one that cannot resolve
  // would just fail at run time with a less useful error than not offering it at all.
  const resolvable = useMemo(
    () => (datasources.data ?? []).filter((d) => d.resolvable),
    [datasources.data],
  );

  const run = useMutation({
    mutationFn: async (reference: string) => {
      const task = await executeQueryRun({ sql, reference });
      if (task.status !== "succeeded") {
        throw new Error(task.error ?? `The query did not complete (status: ${task.status}).`);
      }
      return task.result as RunQueryResult;
    },
    onMutate: () => {
      setPhase("running");
      setErrorMessage(null);
    },
    onSuccess: (data) => {
      setResult(data);
      setPhase("done");
    },
    onError: (error) => {
      setErrorMessage(isApiError(error) ? error.message : error instanceof Error ? error.message : String(error));
      setPhase("failed");
    },
  });

  if (!dataOpsRunQuery) {
    return { button: null, panel: null };
  }

  const button = (
    <Button
      variant="ghost"
      size="icon-xs"
      aria-label="Run query"
      onClick={() => setPhase("picking")}
      disabled={phase === "running"}
      data-testid="sql-run-button"
    >
      <Play />
    </Button>
  );

  if (phase === "idle") {
    return { button, panel: null };
  }

  const panel = (
    <div className="rounded-md border border-border bg-muted/30 p-3" data-testid="sql-run-panel">
      {phase === "picking" && (
        <div className="flex flex-wrap items-end gap-2">
          <ComboBoxField<Datasource>
            options={resolvable}
            optionValue={(d) => d.reference}
            optionLabel={(d) => d.reference}
            value={sourceRef}
            onChange={(value) => setSourceRef(value)}
            label="Run against"
            placeholder="Choose a datasource"
            loading={datasources.isLoading}
            loadingMessage="Loading datasources..."
            emptyMessage="No datasource the estate declares can be reached from here."
            testId="sql-run-source"
            className="max-w-sm"
          />
          <Button
            size="sm"
            disabled={sourceRef === null || run.isPending}
            onClick={() => sourceRef !== null && run.mutate(sourceRef)}
            data-testid="sql-run-confirm"
          >
            Run
          </Button>
          <Button variant="ghost" size="sm" onClick={() => setPhase("idle")}>
            Cancel
          </Button>
        </div>
      )}
      {phase === "running" && (
        <div className="flex flex-col gap-2">
          <span className="text-xs text-muted-foreground">Running against {sourceRef}...</span>
          <Skeleton className="h-[160px] w-full rounded-md" />
        </div>
      )}
      {phase === "failed" && (
        <div className="flex flex-col gap-2">
          {isApiError(run.error)
            ? <CorrelationError error={run.error} />
            : <p className="text-xs text-destructive" data-testid="sql-run-error">{errorMessage}</p>}
          <div>
            <Button variant="ghost" size="sm" onClick={() => setPhase("picking")}>Try again</Button>
          </div>
        </div>
      )}
      {phase === "done" && result !== null && (
        <div className="flex flex-col gap-2">
          <div className="flex items-center justify-between text-xs text-muted-foreground">
            <span>
              {result.rowCount} row{result.rowCount === 1 ? "" : "s"}
              {result.truncated && " (truncated)"} from {sourceRef}
            </span>
            <Button variant="ghost" size="xs" onClick={() => setPhase("picking")}>Run again</Button>
          </div>
          <QueryResultView columns={result.columns} rows={result.rows} />
        </div>
      )}
    </div>
  );

  return { button, panel };
}

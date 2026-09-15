import { useEffect, useState } from "react";
import type { QueryClient } from "@tanstack/react-query";
import { Badge } from "@/components/ui/badge";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { isApiError } from "../../api/client";

/** The query-key root of every semantic layer read, so one save refreshes the tree, the object, and the lists. */
export const SEMANTIC_ROOT = "semantic-layer";

/** The query-key root of the column policy reads (the blocked-columns audit). */
export const COLUMN_POLICY_ROOT = "column-policies";

/** One error-to-text mapping for every toast and inline error on these pages (the API's detail wins). */
export function errorText(error: unknown): string {
  if (isApiError(error)) {
    return error.detail ?? error.title;
  }

  return error instanceof Error ? error.message : String(error);
}

/** Refreshes everything the semantic layer pages read: any change can move an object in or out of the layer,
 * which changes coverage counts, served status, and the blocked list at once. */
export function refreshSemanticLayer(queryClient: QueryClient): void {
  void queryClient.invalidateQueries({ queryKey: [SEMANTIC_ROOT] });
  void queryClient.invalidateQueries({ queryKey: [COLUMN_POLICY_ROOT] });
}

/** A comma- or line-separated list typed by a person, as its trimmed, non-empty entries. */
export function parseList(text: string): string[] {
  return text
    .split(/[,\n]/)
    .map((part) => part.trim())
    .filter((part) => part.length > 0);
}

/** A list as the comma-separated text an input edits. */
export function formatList(values: readonly string[]): string {
  return values.join(", ");
}

/** Blank text as null, anything else trimmed. */
export function textOrNull(value: string): string | null {
  const trimmed = value.trim();
  return trimmed === "" ? null : trimmed;
}

/** database.schema.name, skipping the parts that are unknown. */
export function qualifiedName(database: string | null, schema: string | null, name: string): string {
  return [database, schema, name].filter((part): part is string => part !== null).join(".");
}

/** A value that settles `delayMs` after it stops changing, so a search fires per pause rather than per keystroke. */
export function useDebouncedValue<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(handle);
  }, [value, delayMs]);
  return debounced;
}

/** Whether an annotation is currently served to the assistant, and if not, why (on hover). */
export function ServedBadge({ problem, testId }: { problem: string | null; testId?: string }) {
  if (problem === null) {
    return <Badge variant="secondary" className="text-[11px]" data-testid={testId}>served</Badge>;
  }

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="inline-flex">
          <Badge variant="outline" className="border-warning/60 text-[11px] text-warning" data-testid={testId}>
            withheld
          </Badge>
        </span>
      </TooltipTrigger>
      <TooltipContent className="max-w-sm">{problem}</TooltipContent>
    </Tooltip>
  );
}

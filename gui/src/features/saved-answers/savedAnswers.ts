import type { QueryClient } from "@tanstack/react-query";
import { SEMANTIC_ROOT } from "../semantic-layer/shared";

/** The query-key root of every saved-answer read. */
export const SAVED_ANSWERS_ROOT = "saved-answers";

/** Refreshes everything a saved-answer change is visible in: the list itself, and the semantic layer editor, which
 * shows a saved answer as an example of each table it reads. */
export function refreshSavedAnswers(queryClient: QueryClient): void {
  void queryClient.invalidateQueries({ queryKey: [SAVED_ANSWERS_ROOT] });
  void queryClient.invalidateQueries({ queryKey: [SEMANTIC_ROOT] });
}

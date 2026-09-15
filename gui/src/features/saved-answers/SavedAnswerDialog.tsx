import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { questionExampleApi } from "../../api/endpoints";
import type { QuestionExampleAdmin } from "../../api/types";
import { errorText, textOrNull } from "../semantic-layer/shared";
import { refreshSavedAnswers } from "./savedAnswers";

/** The longest question the store accepts, matching the column and the endpoint's own limit. */
const MAX_QUESTION_LENGTH = 1000;

/**
 * Edits one saved answer: the question people ask, the query that answers it, and the datasource it runs against.
 * The query is edited as plain text rather than in the SQL editor, because the editor pretty-prints what it shows and
 * a saved answer is stored exactly as written. Whether the query is acceptable (one read-only SELECT over allow-listed
 * columns, not a duplicate of another saved answer) is decided by the server, and its refusal is shown here verbatim.
 */
export function SavedAnswerDialog({ answer, onClose }: { answer: QuestionExampleAdmin; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [question, setQuestion] = useState(answer.question);
  const [sql, setSql] = useState(answer.sql);
  const [sourceRef, setSourceRef] = useState(answer.sourceRef ?? "");

  const save = useMutation({
    mutationFn: () => questionExampleApi.update(answer.id, {
      question: question.trim(),
      sql: sql.trim(),
      sourceRef: textOrNull(sourceRef),
    }),
    onSuccess: () => {
      toast.success("Saved answer updated.");
      refreshSavedAnswers(queryClient);
      onClose();
    },
  });

  const trimmedQuestion = question.trim();
  const tooLong = trimmedQuestion.length > MAX_QUESTION_LENGTH;
  const unchanged = trimmedQuestion === answer.question
    && sql.trim() === answer.sql
    && textOrNull(sourceRef) === answer.sourceRef;
  const canSave = trimmedQuestion !== "" && !tooLong && sql.trim() !== "" && !unchanged && !save.isPending;

  return (
    <Dialog
      open
      onOpenChange={(next) => {
        if (!next && !save.isPending) {
          onClose();
        }
      }}
    >
      <DialogContent className="sm:max-w-2xl" data-testid="saved-answer-dialog">
        <DialogHeader>
          <DialogTitle>Edit saved answer</DialogTitle>
          <DialogDescription>
            The assistant reuses this query for questions that mean the same thing, and runs it without asking when a
            new question matches closely. Saving puts your name on it as the person who checked it.
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-4">
          {answer.problem !== null && (
            <p className="rounded-md border border-warning/60 p-2 text-[13px] text-warning" data-testid="saved-answer-problem">
              The assistant is not offered this answer right now: {answer.problem}
            </p>
          )}
          {save.isError && (
            <p className="text-[13px] text-destructive" data-testid="saved-answer-error">{errorText(save.error)}</p>
          )}
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="saved-answer-question">Question</Label>
            <Input
              id="saved-answer-question"
              value={question}
              onChange={(event) => setQuestion(event.target.value)}
              aria-invalid={tooLong || undefined}
              className="h-8"
              data-testid="saved-answer-question"
            />
            {tooLong && (
              <p className="text-xs text-destructive">
                This question is {trimmedQuestion.length} characters, over the {MAX_QUESTION_LENGTH}-character limit.
              </p>
            )}
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="saved-answer-sql">Query</Label>
            <Textarea
              id="saved-answer-sql"
              value={sql}
              onChange={(event) => setSql(event.target.value)}
              spellCheck={false}
              className="min-h-40 font-mono text-[12px]"
              data-testid="saved-answer-sql"
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="saved-answer-source">Datasource</Label>
            <Input
              id="saved-answer-source"
              value={sourceRef}
              onChange={(event) => setSourceRef(event.target.value)}
              placeholder="${env:...} or @alias"
              className="h-8 font-mono text-[12px]"
              data-testid="saved-answer-source"
            />
            <p className="text-xs text-muted-foreground">
              Leave blank to work it out from the tables the query reads.
            </p>
          </div>
        </div>

        <DialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={save.isPending}>Cancel</Button>
          <Button size="sm" onClick={() => save.mutate()} disabled={!canSave} data-testid="saved-answer-save">
            {save.isPending && <Loader2 className="animate-spin" />}
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

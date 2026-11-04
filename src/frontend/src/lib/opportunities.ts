import { useQuery } from "@tanstack/react-query";
import { getJson, sendJson } from "./api";

export type Stage = { key: string; label: string; sortOrder: number; isTerminal: boolean; outcome: string | null; active: boolean };

export type Opportunity = {
  id: string;
  name: string;
  accountId: string;
  stageKey: string;
  amount: { amount: string; currency: string; amountSEK: string | null; rateDate: string | null } | null;
  hourlyRate: string | null;
  estHours: string | null;
  nextStep: string | null;
  nextStepDate: string | null;
  expectedCloseDate: string | null;
  forecastCategory: string | null;
  ownerUserId: string;
  updatedAt: string;
};

/** What the form edits, all as text. */
export type FormValues = {
  name: string;
  accountId: string;
  stageKey: string;
  amount: string;
  currency: string;
  hourlyRate: string;
  estHours: string;
  nextStep: string;
  nextStepDate: string;
  expectedCloseDate: string;
  forecastCategory: string;
};

export const forecastCategories = ["pipeline", "best_case", "commit", "omitted"] as const;

const optionalFields = ["hourlyRate", "estHours", "nextStep", "nextStepDate", "expectedCloseDate", "forecastCategory"] as const;

/**
 * GET /api/v1/stages. Stage labels come only from here, never from code, so a label renamed in the database shows
 * once this cache refreshes, with no deploy.
 */
export function useStages() {
  return useQuery({
    queryKey: ["stages"],
    queryFn: ({ signal }) => getJson<Stage[]>("/api/v1/stages", signal),
    staleTime: 60_000,
  });
}

/** The label of a stage key; the key itself while the list is loading or when the stage is unknown. */
export function stageLabel(stages: Stage[] | undefined, key: string): string {
  return stages?.find((stage) => stage.key === key)?.label ?? key;
}

/**
 * The request body for a form. On create an empty optional field is left out; on edit it is sent as null so the
 * user can clear it (merge-patch). The stage is sent on create only: PATCH refuses it (use change-stage).
 */
export function toPayload(values: FormValues, mode: "create" | "edit"): Record<string, unknown> {
  const body: Record<string, unknown> = { name: values.name.trim(), accountId: values.accountId };
  if (mode === "create") {
    body.stageKey = values.stageKey;
  }
  for (const field of optionalFields) {
    const text = values[field].trim();
    if (text !== "" || mode === "edit") {
      body[field] = text === "" ? null : text;
    }
  }
  const amount = values.amount.trim();
  if (amount !== "" || mode === "edit") {
    body.amount = amount === "" ? null : { amount, currency: values.currency };
  }
  return body;
}

export function createOpportunity(body: Record<string, unknown>) {
  return sendJson<Opportunity>("POST", "/api/v1/opportunities", body);
}

/** `etag` is the ETag of the version being edited: 412 when someone changed the opportunity since, 428 without it. */
export function updateOpportunity(id: string, etag: string, body: Record<string, unknown>) {
  return sendJson<Opportunity>("PATCH", `/api/v1/opportunities/${encodeURIComponent(id)}`, body, etag);
}

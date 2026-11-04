import { ApiError, sendJson } from "./api";

export type Lead = {
  id: string;
  firstName: string | null;
  lastName: string;
  companyName: string;
  jobTitle: string | null;
  phone: string | null;
  mobile: string | null;
  email: string | null;
  website: string | null;
  leadSource: string;
  countryCode: string | null;
  callTries: number;
  lastCallOutcome: string | null;
  nextStep: string | null;
  nextStepDate: string | null;
  accountId: string | null;
  ownerUserId: string;
  status: "open" | "converted" | "disqualified";
  updatedAt: string;
};

/** The fields the form edits, as the text typed into each input. callTries, lastCallOutcome and status are the server's, so they are not here. */
export type LeadFormValues = {
  firstName: string;
  lastName: string;
  companyName: string;
  jobTitle: string;
  phone: string;
  mobile: string;
  email: string;
  website: string;
  leadSource: string;
  countryCode: string;
  nextStep: string;
};

export const emptyLead: LeadFormValues = {
  firstName: "",
  lastName: "",
  companyName: "",
  jobTitle: "",
  phone: "",
  mobile: "",
  email: "",
  website: "",
  leadSource: "",
  countryCode: "",
  nextStep: "",
};

export const requiredFields: (keyof LeadFormValues)[] = ["lastName", "companyName", "leadSource"];

export function toFormValues(lead: Lead): LeadFormValues {
  return {
    firstName: lead.firstName ?? "",
    lastName: lead.lastName,
    companyName: lead.companyName,
    jobTitle: lead.jobTitle ?? "",
    phone: lead.phone ?? "",
    mobile: lead.mobile ?? "",
    email: lead.email ?? "",
    website: lead.website ?? "",
    leadSource: lead.leadSource,
    countryCode: lead.countryCode ?? "",
    nextStep: lead.nextStep ?? "",
  };
}

/**
 * The JSON body for POST or PATCH. A blank optional field is sent as null (it clears the value on PATCH); a blank required field
 * is sent as typed, so the API answers with its own message for that field.
 */
export function toBody(values: LeadFormValues): Record<string, string | null> {
  return Object.fromEntries(
    Object.entries(values).map(([name, value]) => [
      name,
      value.trim() === "" && !requiredFields.includes(name as keyof LeadFormValues) ? null : value.trim(),
    ]),
  );
}

/** Creates the lead, or updates `existing` with the ETag it was read with (the API answers 412 when it changed meanwhile). */
export async function saveLead(values: LeadFormValues, existing?: { id: string; etag: string }): Promise<{ lead: Lead; etag: string | null }> {
  const body = toBody(values);
  const saved = existing
    ? await sendJson<Lead>("PATCH", `/api/v1/leads/${encodeURIComponent(existing.id)}`, body, { "If-Match": existing.etag })
    : await sendJson<Lead>("POST", "/api/v1/leads", body);
  return { lead: saved.data, etag: saved.etag };
}

/** The message of each failing field of an API problem, by field name; empty when the error has none. */
export function fieldErrors(error: unknown): Record<string, string> {
  const result: Record<string, string> = {};
  if (error instanceof ApiError) {
    for (const { field, message } of error.problem?.errors ?? []) {
      result[field] ??= message;
    }
  }
  return result;
}

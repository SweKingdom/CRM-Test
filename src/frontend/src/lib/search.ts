import { useQuery } from "@tanstack/react-query";
import { strings } from "../strings";
import { getJson } from "./api";

export const MIN_QUERY_LENGTH = 2;

export type SearchHit = { type: string; id: string; title: string; snippet: string; score: number };
export type HitGroup = { type: string; label: string; hits: SearchHit[] };

/** The record types in display order, with the route segment of their screens. */
const recordTypes = [
  { type: "account", segment: "accounts" },
  { type: "contact", segment: "contacts" },
  { type: "lead", segment: "leads" },
  { type: "opportunity", segment: "opportunities" },
  { type: "project", segment: "projects" },
] as const;

/** Where a hit opens: Account 360 and Contact 360, or the record's own route (a placeholder until that screen exists). */
export function hitPath(hit: SearchHit): string {
  const known = recordTypes.find((t) => t.type === hit.type);
  return `/${known?.segment ?? "search"}/${encodeURIComponent(hit.id)}`;
}

/** Groups hits by record type in display order, at most `max` per group; types without hits are left out. */
export function groupHits(hits: SearchHit[], max = Infinity): HitGroup[] {
  return recordTypes
    .map(({ type }) => ({
      type,
      label: strings.search.groups[type],
      hits: hits.filter((hit) => hit.type === type).slice(0, max),
    }))
    .filter((group) => group.hits.length > 0);
}

/** GET /api/v1/search. A query that changes cancels the request still running for the old one. Errors are shown, not retried. */
export function useSearch(q: string, limit: number) {
  return useQuery({
    queryKey: ["search", q, limit],
    queryFn: ({ signal }) =>
      getJson<{ items: SearchHit[]; fallback: boolean }>(`/api/v1/search?q=${encodeURIComponent(q)}&limit=${limit}`, signal),
    enabled: q.length >= MIN_QUERY_LENGTH,
    retry: false,
    staleTime: 30_000,
  });
}

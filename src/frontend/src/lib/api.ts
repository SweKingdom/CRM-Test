import { apiFetch } from "../permissions";

/** An RFC 9457 problem body (`application/problem+json`) as the API sends it. */
export type Problem = { title?: string; detail?: string; status?: number; errors?: { field: string; code: string; message: string }[] };

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem?: Problem,
  ) {
    super(problem?.detail ?? problem?.title ?? `The API answered ${status}.`);
  }
}

async function failed(response: Response): Promise<ApiError> {
  const problem = await response.json().then(
    (body) => body as Problem,
    () => undefined,
  );
  return new ApiError(response.status, problem);
}

/** GET a JSON resource. A non-2xx answer throws an ApiError carrying the parsed problem body, when there is one. */
export async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const response = await apiFetch(url, { signal, headers: { Accept: "application/json" } });
  if (!response.ok) {
    throw await failed(response);
  }
  return (await response.json()) as T;
}

/** POST or PATCH a JSON body. Returns the record and its ETag (send it back as `ifMatch` to change the record). A non-2xx answer throws an ApiError. */
export async function sendJson<T>(
  method: "POST" | "PATCH",
  url: string,
  body: unknown,
  ifMatch?: string,
): Promise<{ data: T; etag: string | null }> {
  const headers: Record<string, string> = { Accept: "application/json", "Content-Type": method === "PATCH" ? "application/merge-patch+json" : "application/json" };
  if (ifMatch) {
    headers["If-Match"] = ifMatch;
  }
  const response = await apiFetch(url, { method, headers, body: JSON.stringify(body) });
  if (!response.ok) {
    throw await failed(response);
  }
  return { data: (await response.json()) as T, etag: response.headers.get("ETag") };
}

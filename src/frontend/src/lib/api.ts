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

/** An ApiError carrying the parsed problem body of a non-2xx answer, when there is one. */
async function failure(response: Response): Promise<ApiError> {
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
    throw await failure(response);
  }
  return (await response.json()) as T;
}

/** POST a JSON body, or PATCH it as a merge-patch (send `If-Match` in `headers`). Returns the answer and its ETag; a non-2xx answer throws like getJson. */
export async function sendJson<T>(
  method: "POST" | "PATCH",
  url: string,
  body: unknown,
  headers: Record<string, string> = {},
): Promise<{ data: T; etag: string | null }> {
  const response = await apiFetch(url, {
    method,
    headers: {
      Accept: "application/json",
      "Content-Type": method === "PATCH" ? "application/merge-patch+json" : "application/json",
      ...headers,
    },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw await failure(response);
  }
  return { data: (await response.json()) as T, etag: response.headers.get("ETag") };
}

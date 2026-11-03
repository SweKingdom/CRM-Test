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

/** GET a JSON resource. A non-2xx answer throws an ApiError carrying the parsed problem body, when there is one. */
export async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const response = await apiFetch(url, { signal, headers: { Accept: "application/json" } });
  if (!response.ok) {
    const problem = await response.json().then(
      (body) => body as Problem,
      () => undefined,
    );
    throw new ApiError(response.status, problem);
  }
  return (await response.json()) as T;
}

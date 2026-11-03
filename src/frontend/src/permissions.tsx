import { useEffect, useState, type ReactNode } from "react";
import { strings } from "./strings";

// The route guard is UX only: it hides and blocks screens the API would refuse anyway. The API is authoritative.

export type Permissions = {
  roles: string[];
  permissions: { entity: string; action: string; scope: string }[];
  routes: string[];
};

type State =
  | { status: "loading" }
  | { status: "signed-out" }
  | { status: "failed" }
  | { status: "ready"; permissions: Permissions };

/** Screens every signed-in user may open; every other screen must be in the `routes` the API returns. */
const openPaths = ["/", "/access-denied"];

let current: State = { status: "loading" };
let request: Promise<void> | null = null;
const listeners = new Set<() => void>();

function publish(next: State): void {
  current = next;
  listeners.forEach((listener) => listener());
}

/** Reads GET /api/v1/me/permissions once per page load; `force` reads it again (after a 401 or 403). */
export function loadPermissions(force = false): Promise<void> {
  if (request && !force) {
    return request;
  }
  request = fetch("/api/v1/me/permissions", { credentials: "same-origin", headers: { Accept: "application/json" } })
    .then(async (response) => {
      if (response.status === 401) {
        publish({ status: "signed-out" });
      } else if (response.ok) {
        publish({ status: "ready", permissions: (await response.json()) as Permissions });
      } else {
        publish({ status: "failed" });
      }
    })
    .catch(() => publish({ status: "failed" }));
  return request;
}

/**
 * fetch for the app's own API calls: same-origin, with the CSRF header the API requires for cookie writes. A 401 or 403
 * means the user's permissions may have changed, so they are read again and the guard reacts.
 */
export async function apiFetch(input: string, init?: RequestInit): Promise<Response> {
  const headers = new Headers(init?.headers);
  headers.set("X-Zephyr-Csrf", "1");
  const response = await fetch(input, { ...init, credentials: "same-origin", headers });
  if (response.status === 401 || response.status === 403) {
    void loadPermissions(true);
  }
  return response;
}

/** A path is open when it is a public screen or is, or lies below, a route the API offered. */
export function canOpen(path: string, routes: string[]): boolean {
  return openPaths.includes(path) || routes.some((route) => path === route || path.startsWith(`${route}/`));
}

function usePermissionState(): State {
  const [, rerender] = useState(0);
  useEffect(() => {
    const listener = () => rerender((count) => count + 1);
    listeners.add(listener);
    void loadPermissions();
    return () => {
      listeners.delete(listener);
    };
  }, []);
  return current;
}

export function AccessDenied() {
  return (
    <main>
      <h1>{strings.accessDeniedTitle}</h1>
      <p>{strings.accessDeniedBody}</p>
      <a href="/">{strings.goHome}</a>
    </main>
  );
}

function SignIn({ returnUrl }: { returnUrl: string }) {
  useEffect(() => {
    window.location.assign(`/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`);
  }, [returnUrl]);
  return <p>{strings.signingIn}</p>;
}

/** Shows `children` when the user may open `path`, the access-denied page when not, and sign-in when nobody is signed in. */
export function RouteGuard({ path, children }: { path: string; children: ReactNode }) {
  const state = usePermissionState();
  switch (state.status) {
    case "loading":
      return <p>{strings.loading}</p>;
    case "signed-out":
      return <SignIn returnUrl={path + window.location.search} />;
    case "failed":
      return <p role="alert">{strings.permissionsFailed}</p>;
    case "ready":
      return canOpen(path, state.permissions.routes) ? <>{children}</> : <AccessDenied />;
  }
}

import { useEffect, useState } from "react";
import { Link, NavLink, Outlet, useLocation } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { getJson } from "../lib/api";
import { AccessDenied, SignedIn, apiFetch, canOpen, type Permissions } from "../permissions";
import { strings } from "../strings";
import { SearchBox } from "./SearchBox";

/**
 * Navigation items, in order. An item shows when the API's `routes` (GET /api/v1/me/permissions, built from the same
 * entity and action the API checks) offer its path, so this list never repeats the permission mapping.
 */
const navigation = [
  { path: "/accounts", label: strings.nav.accounts },
  { path: "/contacts", label: strings.nav.contacts },
  { path: "/leads", label: strings.nav.leads },
  { path: "/opportunities", label: strings.nav.opportunities },
  { path: "/projects", label: strings.nav.projects },
  { path: "/service-requests", label: strings.nav.serviceRequests },
  { path: "/time", label: strings.nav.time },
  { path: "/approvals", label: strings.nav.approvals },
  { path: "/admin/users", label: strings.nav.users },
  { path: "/admin/api-tokens", label: strings.nav.apiTokens },
  { path: "/admin/reference-data", label: strings.nav.referenceData },
  { path: "/admin/audit", label: strings.nav.audit },
  { path: "/admin/retention", label: strings.nav.retention },
  { path: "/admin/imports", label: strings.nav.imports },
  { path: "/admin/dsar", label: strings.nav.dsar },
];

/** The app shell: header with search and sign-in state, permission-driven navigation, and the page. Signed-out users are sent to sign-in. */
export function Layout() {
  return <SignedIn>{(permissions) => <Shell permissions={permissions} />}</SignedIn>;
}

function Shell({ permissions }: { permissions: Permissions }) {
  const { pathname } = useLocation();
  const [menuOpen, setMenuOpen] = useState(false);
  const me = useQuery({ queryKey: ["me"], queryFn: ({ signal }) => getJson<{ name: string }>("/auth/me", signal), retry: false });

  // The phone menu closes when a page is chosen.
  useEffect(() => setMenuOpen(false), [pathname]);

  const signOut = async () => {
    await apiFetch("/auth/logout", { method: "POST" });
    window.location.assign("/");
  };

  return (
    <>
      <a className="skip-link" href="#main">
        {strings.skipToContent}
      </a>
      <header className="app-header">
        <Link to="/" className="brand">
          {strings.appName}
        </Link>
        <button type="button" className="menu-button" aria-expanded={menuOpen} aria-controls="main-nav" onClick={() => setMenuOpen((open) => !open)}>
          {strings.menu}
        </button>
        <SearchBox />
        <div className="user">
          {me.data && <span>{me.data.name}</span>}
          <button type="button" onClick={() => void signOut()}>
            {strings.signOut}
          </button>
        </div>
      </header>
      <nav id="main-nav" className={menuOpen ? "app-nav open" : "app-nav"} aria-label={strings.mainNavigation}>
        <ul>
          {navigation
            .filter((item) => canOpen(item.path, permissions.routes))
            .map((item) => (
              <li key={item.path}>
                <NavLink to={item.path}>{item.label}</NavLink>
              </li>
            ))}
        </ul>
      </nav>
      <main id="main" tabIndex={-1}>
        {canOpen(pathname, permissions.routes) ? <Outlet /> : <AccessDenied />}
      </main>
    </>
  );
}

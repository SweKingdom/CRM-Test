import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { AccessDenied, RouteGuard } from "./permissions";
import { strings } from "./strings";

const path = window.location.pathname;

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    {path === "/access-denied" ? (
      <AccessDenied />
    ) : (
      <RouteGuard path={path}>
        <h1>{strings.appName}</h1>
        <p>{strings.tagline}</p>
      </RouteGuard>
    )}
  </StrictMode>,
);

import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { strings } from "./strings";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <h1>{strings.appName}</h1>
    <p>{strings.tagline}</p>
  </StrictMode>,
);

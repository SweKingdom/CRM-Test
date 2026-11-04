import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { BrowserRouter, Route, Routes } from "react-router-dom";
import { SearchResults } from "./pages/SearchResults";
import { AccessDenied } from "./permissions";
import { Layout } from "./shell/Layout";
import { strings } from "./strings";

const queryClient = new QueryClient();

function Home() {
  return (
    <>
      <h1>{strings.appName}</h1>
      <p>{strings.tagline}</p>
      <p>{strings.homeHint}</p>
    </>
  );
}

/** Every screen that has no page yet (record screens and the other list screens) lands here. */
function NotAvailable() {
  return (
    <>
      <h1>{strings.notAvailableTitle}</h1>
      <p>{strings.notAvailableBody}</p>
    </>
  );
}

/** Router and data cache. The sign-in check is in Layout: nothing renders for a signed-out user except the redirect to sign-in. */
export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <Routes>
          <Route element={<Layout />}>
            <Route index element={<Home />} />
            <Route path="search" element={<SearchResults />} />
            <Route path="access-denied" element={<AccessDenied />} />
            <Route path="*" element={<NotAvailable />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>
  );
}

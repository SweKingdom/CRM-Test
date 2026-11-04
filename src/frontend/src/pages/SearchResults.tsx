import { Link, useSearchParams } from "react-router-dom";
import { MIN_QUERY_LENGTH, groupHits, hitPath, useSearch } from "../lib/search";
import { strings } from "../strings";

/** `/search?q=`: every result for the query (up to the API's limit of 50), grouped by record type. */
export function SearchResults() {
  const [params] = useSearchParams();
  const q = (params.get("q") ?? "").trim();
  const search = useSearch(q, 50);
  const groups = groupHits(search.data?.items ?? []);

  return (
    <>
      <h1>{q ? strings.search.pageTitle(q) : strings.search.label}</h1>
      <div aria-busy={q.length >= MIN_QUERY_LENGTH && search.isPending}>
        {q.length < MIN_QUERY_LENGTH && <p>{strings.search.tooShort}</p>}
        {q.length >= MIN_QUERY_LENGTH && search.isPending && <p role="status">{strings.search.searching}</p>}
        {search.isError && (
          <p role="alert">
            {strings.search.unavailable}{" "}
            <button type="button" onClick={() => void search.refetch()}>
              {strings.search.retry}
            </button>
          </p>
        )}
        {search.isSuccess && groups.length === 0 && <p>{strings.search.noResults(q)}</p>}
        {groups.map((group) => (
          <section key={group.type} aria-labelledby={`group-${group.type}`}>
            <h2 id={`group-${group.type}`}>{group.label}</h2>
            <ul className="result-list">
              {group.hits.map((hit) => (
                <li key={hit.id}>
                  <Link to={hitPath(hit)}>{hit.title}</Link>
                  <span className="hit-snippet">{hit.snippet}</span>
                </li>
              ))}
            </ul>
          </section>
        ))}
      </div>
    </>
  );
}

import { useEffect, useId, useRef, useState, type FocusEvent, type KeyboardEvent } from "react";
import { Link, useNavigate } from "react-router-dom";
import { MIN_QUERY_LENGTH, groupHits, hitPath, useSearch } from "../lib/search";
import { strings } from "../strings";

const DEBOUNCE_MS = 250;
const MAX_PER_GROUP = 5;

function useDebounced(value: string, ms: number): string {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const timer = window.setTimeout(() => setDebounced(value), ms);
    return () => window.clearTimeout(timer);
  }, [value, ms]);
  return debounced;
}

/** Global search: a labelled combobox (ARIA list autocomplete) with a dropdown grouped by record type. Keyboard: `/` focuses, arrows move, Enter opens, Esc closes. */
export function SearchBox() {
  const id = useId();
  const popupId = `${id}-popup`;
  const optionId = (index: number) => `${id}-option-${index}`;
  const navigate = useNavigate();
  const input = useRef<HTMLInputElement>(null);
  const [text, setText] = useState("");
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(-1);

  const typed = text.trim();
  const debounced = useDebounced(typed, DEBOUNCE_MS);
  const search = useSearch(debounced, 20);
  const groups = groupHits(search.data?.items ?? [], MAX_PER_GROUP);
  const hits = groups.flatMap((group) => group.hits);

  const view =
    typed.length < MIN_QUERY_LENGTH
      ? "idle"
      : typed !== debounced || search.isPending
        ? "loading"
        : search.isError
          ? "error"
          : hits.length === 0
            ? "empty"
            : "results";
  const visible = open && view !== "idle";
  const announcement = {
    idle: "",
    loading: strings.search.searching,
    error: strings.search.unavailable,
    empty: strings.search.noResults(typed),
    results: strings.search.count(hits.length),
  }[view];

  useEffect(() => setActive(-1), [debounced, search.data]);

  // Focus stays in the input, so the highlighted option is scrolled into view by hand.
  useEffect(() => {
    if (active >= 0) {
      document.getElementById(optionId(active))?.scrollIntoView?.({ block: "nearest" });
    }
  });

  // `/` focuses the search box, unless the user is typing in a field.
  useEffect(() => {
    const onKey = (event: globalThis.KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      const typing = target?.matches("input, textarea, select, [contenteditable]");
      if (event.key === "/" && !typing && !event.ctrlKey && !event.metaKey && !event.altKey) {
        event.preventDefault();
        input.current?.focus();
      }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, []);

  const go = (path: string) => {
    setOpen(false);
    setActive(-1);
    navigate(path);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    switch (event.key) {
      case "ArrowDown":
        event.preventDefault();
        if (!visible) {
          setOpen(true);
        } else if (hits.length > 0) {
          setActive((index) => (index + 1) % hits.length);
        }
        break;
      case "ArrowUp":
        event.preventDefault();
        if (visible && hits.length > 0) {
          setActive((index) => (index <= 0 ? hits.length - 1 : index - 1));
        }
        break;
      case "Enter":
        event.preventDefault();
        if (visible && hits[active]) {
          go(hitPath(hits[active]));
        } else if (typed.length >= MIN_QUERY_LENGTH) {
          go(`/search?q=${encodeURIComponent(typed)}`);
        }
        break;
      case "Escape":
        if (visible) {
          event.preventDefault();
          setOpen(false);
          setActive(-1);
        }
        break;
    }
  };

  // The dropdown closes when focus leaves the search area (the retry button counts as inside).
  const onBlur = (event: FocusEvent<HTMLDivElement>) => {
    if (!event.currentTarget.contains(event.relatedTarget)) {
      setOpen(false);
    }
  };

  return (
    <div className="search" role="search" onBlur={onBlur}>
      <label htmlFor={`${id}-input`} className="sr-only">
        {strings.search.label}
      </label>
      <input
        id={`${id}-input`}
        ref={input}
        type="text"
        role="combobox"
        autoComplete="off"
        placeholder={strings.search.placeholder}
        value={text}
        aria-autocomplete="list"
        aria-expanded={visible}
        aria-controls={popupId}
        aria-activedescendant={visible && active >= 0 ? optionId(active) : undefined}
        onChange={(event) => {
          setText(event.target.value);
          setOpen(true);
        }}
        onKeyDown={onKeyDown}
      />
      <div role="status" className="sr-only">
        {visible ? announcement : ""}
      </div>
      {visible && (
        <div
          id={popupId}
          className="search-popup"
          role={view === "results" ? "listbox" : undefined}
          aria-label={view === "results" ? strings.search.resultsLabel : undefined}
          aria-busy={view === "loading"}
        >
          {view === "loading" && <p className="search-message">{strings.search.searching}</p>}
          {view === "empty" && <p className="search-message">{strings.search.noResults(typed)}</p>}
          {view === "error" && (
            <p className="search-message">
              {strings.search.unavailable}{" "}
              <button type="button" onClick={() => void search.refetch()}>
                {strings.search.retry}
              </button>
            </p>
          )}
          {view === "results" &&
            groups.map((group) => (
              <div key={group.type} role="group" aria-labelledby={`${id}-group-${group.type}`}>
                <div id={`${id}-group-${group.type}`} className="search-group" role="presentation">
                  {group.label}
                </div>
                {group.hits.map((hit) => {
                  const index = hits.indexOf(hit);
                  return (
                    <Link
                      key={hit.id}
                      id={optionId(index)}
                      role="option"
                      aria-selected={index === active}
                      tabIndex={-1}
                      to={hitPath(hit)}
                      className={index === active ? "search-option active" : "search-option"}
                      onMouseDown={(event) => event.preventDefault()}
                      onClick={() => setOpen(false)}
                    >
                      <span className="hit-title">{hit.title}</span>
                      <span className="hit-snippet">{hit.snippet}</span>
                    </Link>
                  );
                })}
              </div>
            ))}
        </div>
      )}
    </div>
  );
}

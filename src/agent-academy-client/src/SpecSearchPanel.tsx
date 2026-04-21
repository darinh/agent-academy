import { useCallback, useRef, useState } from "react";
import { makeStyles, shorthands, Spinner, Input } from "@fluentui/react-components";
import { SearchRegular } from "@fluentui/react-icons";
import V3Badge from "./V3Badge";
import EmptyState from "./EmptyState";
import { searchSpecs, type SpecSearchResult } from "./api";

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    height: "100%",
    overflow: "hidden",
  },
  searchBar: {
    ...shorthands.padding("14px", "20px"),
    borderBottom: "1px solid var(--aa-border)",
    display: "flex",
    alignItems: "center",
    gap: "10px",
  },
  searchInput: {
    flex: 1,
  },
  body: {
    flex: 1,
    overflowY: "auto",
    ...shorthands.padding("14px", "20px"),
    display: "flex",
    flexDirection: "column",
    gap: "8px",
  },
  resultCard: {
    ...shorthands.padding("12px", "16px"),
    ...shorthands.borderRadius("8px"),
    background: "rgba(0,0,0,0.15)",
    ...shorthands.border("1px", "solid", "var(--aa-hairline)"),
    display: "flex",
    flexDirection: "column",
    gap: "6px",
    ":hover": {
      background: "rgba(255,255,255,0.03)",
    },
  },
  resultHeader: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: "8px",
  },
  heading: {
    fontWeight: 600,
    fontSize: "13px",
    color: "var(--aa-text-strong)",
  },
  summary: {
    fontSize: "12px",
    color: "var(--aa-text)",
    lineHeight: "1.5",
  },
  filePath: {
    fontSize: "11px",
    fontFamily: "var(--mono)",
    color: "var(--aa-muted)",
  },
  matchedTerms: {
    fontSize: "11px",
    color: "var(--aa-soft)",
    fontStyle: "italic",
  },
});

const DEBOUNCE_MS = 400;

export default function SpecSearchPanel() {
  const s = useLocalStyles();
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<SpecSearchResult[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [hasSearched, setHasSearched] = useState(false);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(undefined);

  const doSearch = useCallback(async (q: string) => {
    if (!q.trim()) {
      setResults([]);
      setHasSearched(false);
      return;
    }
    setLoading(true);
    setError(null);
    try {
      const data = await searchSpecs(q.trim(), 20);
      setResults(data);
      setHasSearched(true);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Search failed");
    } finally {
      setLoading(false);
    }
  }, []);

  const handleChange = useCallback((val: string) => {
    setQuery(val);
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => doSearch(val), DEBOUNCE_MS);
  }, [doSearch]);

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (e.key === "Enter") {
      if (debounceRef.current) clearTimeout(debounceRef.current);
      doSearch(query);
    }
  }, [query, doSearch]);

  return (
    <div className={s.root}>
      <div className={s.searchBar}>
        <SearchRegular fontSize={16} />
        <Input
          className={s.searchInput}
          placeholder="Search specifications…"
          value={query}
          onChange={(_, data) => handleChange(data.value)}
          onKeyDown={handleKeyDown}
          appearance="underline"
          aria-label="Search specifications"
        />
        {loading && <Spinner size="tiny" />}
      </div>

      <div className={s.body}>
        {error && (
          <div style={{ color: "var(--aa-copper)", fontSize: 12 }}>⚠ {error}</div>
        )}

        {!hasSearched && !loading && (
          <EmptyState
            icon={<span style={{ fontSize: 48 }}>📜</span>}
            title="Search specifications"
            detail="Type a query to search across all spec sections by heading, summary, and content."
          />
        )}

        {hasSearched && results.length === 0 && !loading && (
          <EmptyState
            icon={<span style={{ fontSize: 48 }}>🔍</span>}
            title="No results"
            detail={`No spec sections matched "${query}".`}
          />
        )}

        {results.map((r) => (
          <div key={r.id} className={s.resultCard}>
            <div className={s.resultHeader}>
              <span className={s.heading}>{r.heading}</span>
              <V3Badge color="info">{Math.round(r.score * 100)}%</V3Badge>
            </div>
            <div className={s.summary}>{r.summary}</div>
            <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
              <span className={s.filePath}>{r.filePath}</span>
              {r.matchedTerms && (
                <span className={s.matchedTerms}>matched: {r.matchedTerms}</span>
              )}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

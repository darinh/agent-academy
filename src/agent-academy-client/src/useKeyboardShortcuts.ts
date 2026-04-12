import { useEffect, useRef } from "react";

export interface KeyboardShortcutActions {
  onTogglePalette: () => void;
  onSearch: () => void;
  onToggleShortcuts: () => void;
}

/**
 * Registers global keyboard shortcuts (Cmd/Ctrl+K, /, ?).
 * Skips activation when focus is inside an input, textarea, or contentEditable.
 * Uses refs internally to avoid re-binding listeners on callback changes.
 */
export function useKeyboardShortcuts(actions: KeyboardShortcutActions): void {
  const actionsRef = useRef(actions);
  actionsRef.current = actions;

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement)?.tagName;
      const editable = (e.target as HTMLElement)?.isContentEditable;
      const inInput = tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || editable;

      if ((e.metaKey || e.ctrlKey) && e.key === "k") {
        if (inInput) return;
        e.preventDefault();
        actionsRef.current.onTogglePalette();
      }
      if (e.key === "/" && !e.metaKey && !e.ctrlKey && !e.altKey && !inInput) {
        e.preventDefault();
        actionsRef.current.onSearch();
      }
      if (e.key === "?" && !e.metaKey && !e.ctrlKey && !e.altKey && !inInput) {
        e.preventDefault();
        actionsRef.current.onToggleShortcuts();
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, []);
}

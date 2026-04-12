import {
  Dialog,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  shorthands,
  Text,
} from "@fluentui/react-components";
import { DismissRegular } from "@fluentui/react-icons";

const useLocalStyles = makeStyles({
  surface: {
    background:
      "linear-gradient(180deg, rgba(18, 23, 33, 0.98), rgba(9, 12, 18, 0.98))",
    border: "1px solid var(--aa-border)",
    borderRadius: "20px",
    color: "var(--aa-text)",
    maxWidth: "480px",
    width: "min(480px, 90vw)",
  },
  title: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    color: "var(--aa-text-strong)",
    fontFamily: "var(--heading)",
  },
  dismiss: {
    cursor: "pointer",
    color: "var(--aa-soft)",
    "&:hover": { color: "var(--aa-text)" },
  },
  content: {
    ...shorthands.padding("4px", "0", "8px"),
  },
  section: {
    marginBottom: "16px",
  },
  sectionLabel: {
    fontSize: "11px",
    fontWeight: 600,
    textTransform: "uppercase" as const,
    letterSpacing: "0.08em",
    color: "var(--aa-soft)",
    ...shorthands.padding("0", "0", "8px"),
    display: "block",
  },
  row: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    ...shorthands.padding("6px", "8px"),
    ...shorthands.borderRadius("8px"),
    "&:hover": { backgroundColor: "rgba(91, 141, 239, 0.04)" },
  },
  label: {
    color: "var(--aa-text)",
    fontSize: "14px",
  },
  keys: {
    display: "flex",
    gap: "4px",
    alignItems: "center",
  },
  kbd: {
    display: "inline-flex",
    alignItems: "center",
    justifyContent: "center",
    minWidth: "24px",
    height: "24px",
    ...shorthands.padding("2px", "6px"),
    ...shorthands.borderRadius("6px"),
    backgroundColor: "rgba(255, 255, 255, 0.06)",
    border: "1px solid rgba(255, 255, 255, 0.1)",
    color: "var(--aa-muted)",
    fontSize: "12px",
    fontFamily: "var(--monospace, monospace)",
    fontWeight: 500,
    lineHeight: 1,
  },
  separator: {
    color: "var(--aa-soft)",
    fontSize: "11px",
    ...shorthands.padding("0", "2px"),
  },
});

interface Shortcut {
  label: string;
  keys: string[][];
}

interface ShortcutGroup {
  title: string;
  shortcuts: Shortcut[];
}

const isMac = typeof navigator !== "undefined" && /Mac|iPod|iPhone|iPad/.test(navigator.userAgent);
const MOD = isMac ? "⌘" : "Ctrl";

const SHORTCUT_GROUPS: ShortcutGroup[] = [
  {
    title: "Navigation",
    shortcuts: [
      { label: "Command palette", keys: [[MOD, "K"]] },
      { label: "Search", keys: [["/"]] },
      { label: "Show keyboard shortcuts", keys: [["?"]] },
    ],
  },
  {
    title: "Chat & Messages",
    shortcuts: [
      { label: "Send message", keys: [["Enter"]] },
      { label: "New line in message", keys: [["Shift", "Enter"]] },
    ],
  },
  {
    title: "Panels",
    shortcuts: [
      { label: "Close settings / palette", keys: [["Esc"]] },
    ],
  },
];

interface KeyboardShortcutsDialogProps {
  open: boolean;
  onClose: () => void;
}

export default function KeyboardShortcutsDialog({ open, onClose }: KeyboardShortcutsDialogProps) {
  const s = useLocalStyles();

  return (
    <Dialog open={open} onOpenChange={(_, data) => { if (!data.open) onClose(); }}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle className={s.title}>
            Keyboard Shortcuts
            <DismissRegular
              className={s.dismiss}
              onClick={onClose}
              role="button"
              tabIndex={0}
              aria-label="Close"
              onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") onClose(); }}
            />
          </DialogTitle>
          <DialogContent className={s.content}>
            {SHORTCUT_GROUPS.map((group) => (
              <div key={group.title} className={s.section}>
                <Text className={s.sectionLabel}>{group.title}</Text>
                {group.shortcuts.map((shortcut) => (
                  <div key={shortcut.label} className={s.row}>
                    <Text className={s.label}>{shortcut.label}</Text>
                    <div className={s.keys}>
                      {shortcut.keys.map((combo, ci) => (
                        <span key={ci} style={{ display: "inline-flex", gap: "4px", alignItems: "center" }}>
                          {ci > 0 && <Text className={s.separator}>or</Text>}
                          {combo.map((key, ki) => (
                            <span key={ki} style={{ display: "inline-flex", gap: "2px", alignItems: "center" }}>
                              {ki > 0 && <Text className={s.separator}>+</Text>}
                              <kbd className={s.kbd}>{key}</kbd>
                            </span>
                          ))}
                        </span>
                      ))}
                    </div>
                  </div>
                ))}
              </div>
            ))}
          </DialogContent>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

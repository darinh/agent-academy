import { useEffect, useState } from "react";
import {
  Button,
  Textarea,
  Spinner,
  Dialog,
  DialogSurface,
  DialogTitle,
  DialogBody,
  DialogActions,
  DialogTrigger,
  makeStyles,
  shorthands,
  tokens,
} from "@fluentui/react-components";
import {
  EditRegular,
  SaveRegular,
  DeleteRegular,
  DismissRegular,
  DocumentRegular,
  ErrorCircleRegular,
} from "@fluentui/react-icons";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { getPlan, setPlan, deletePlan } from "./api";

// ── Styles ──

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    height: "100%",
    overflow: "hidden",
  },
  toolbar: {
    display: "none",
  },
  toolbarTitle: { display: "none" },
  spacer: { flex: 1 },
  errorText: { color: tokens.colorPaletteRedForeground1, fontSize: "13px" },
  errorBanner: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    ...shorthands.padding("8px", "16px"),
    ...shorthands.margin("0", "24px", "0"),
    ...shorthands.borderRadius("6px"),
    backgroundColor: "rgba(220, 53, 69, 0.08)",
    border: "1px solid rgba(220, 53, 69, 0.2)",
    color: "var(--aa-copper, #dc3545)",
    fontSize: "13px",
  },
  content: {
    flex: 1,
    overflow: "auto",
    maxWidth: "800px",
    ...shorthands.padding("20px", "24px"),
  },
  markdown: {
    textAlign: "left",
    fontFamily: "var(--mono)",
    fontSize: "13px",
    lineHeight: 1.7,
    whiteSpace: "pre-wrap" as const,
    "& h1, & h2, & h3": { color: "var(--aa-text-strong)", fontFamily: "var(--sans)", fontWeight: 700 },
    "& h1": { fontSize: "16px", ...shorthands.margin("16px", "0", "8px") },
    "& h2": { fontSize: "16px", ...shorthands.margin("16px", "0", "8px") },
    "& h3": { fontSize: "14px", fontWeight: 600, ...shorthands.margin("12px", "0", "6px") },
    "& p, & li": { color: "var(--aa-text)", fontSize: "13px", lineHeight: 1.7 },
    "& code": {
      backgroundColor: "rgba(255,255,255,0.06)",
      ...shorthands.padding("2px", "6px"),
      ...shorthands.borderRadius("4px"),
      fontFamily: tokens.fontFamilyMonospace,
      fontSize: "13px",
    },
    "& pre": {
      backgroundColor: "rgba(255,255,255,0.04)",
      ...shorthands.padding("12px"),
      ...shorthands.borderRadius("6px"),
      overflow: "auto",
      border: "1px solid rgba(255,255,255,0.06)",
    },
    "& pre code": {
      backgroundColor: "transparent",
      ...shorthands.padding("0"),
    },
    "& a": { color: "var(--aa-cyan)" },
    "& table": { borderCollapse: "collapse", width: "100%" },
    "& th, & td": {
      border: "1px solid var(--aa-border)",
      ...shorthands.padding("6px", "10px"),
      textAlign: "left",
      fontSize: "13px",
    },
    "& th": { color: "var(--aa-muted)", fontWeight: 600 },
    "& ul, & ol": { paddingLeft: "20px" },
    "& blockquote": {
      borderLeft: "3px solid rgba(91, 141, 239, 0.4)",
      marginLeft: 0,
      ...shorthands.padding("4px", "16px"),
      color: "var(--aa-soft)",
    },
  },
  textarea: { width: "100%", minHeight: "400px" },
  empty: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "center",
    height: "100%",
    gap: "12px",
    color: "var(--aa-soft)",
  },
  emptyIcon: { fontSize: "26px" },
});

// ── Component ──

interface PlanPanelProps {
  roomId: string | null;
}

export default function PlanPanel({ roomId }: PlanPanelProps) {
  const s = useLocalStyles();

  const [content, setContent] = useState("");
  const [draft, setDraft] = useState("");
  const [editing, setEditing] = useState(false);
  const [loadedRoomId, setLoadedRoomId] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);

  const loading = roomId !== null && loadedRoomId !== roomId;

  useEffect(() => {
    if (!roomId) return;
    let cancelled = false;
    getPlan(roomId)
      .then((plan) => {
        if (!cancelled) {
          setContent(plan?.content ?? "");
          setError(null);
        }
      })
      .catch((e: unknown) => {
        if (!cancelled) {
          setContent("");
          setError(e instanceof Error ? e.message : String(e));
        }
      })
      .finally(() => {
        if (!cancelled) setLoadedRoomId(roomId);
      });
    return () => { cancelled = true; };
  }, [roomId]);

  const handleEdit = () => {
    setDraft(content);
    setEditing(true);
  };

  const handleCancel = () => setEditing(false);

  const handleSave = () => {
    if (!roomId) return;
    setSaving(true);
    setPlan(roomId, draft)
      .then(() => {
        setContent(draft);
        setEditing(false);
        setError(null);
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setSaving(false));
  };

  const handleDelete = () => {
    if (!roomId) return;
    deletePlan(roomId)
      .then(() => {
        setContent("");
        setEditing(false);
        setConfirmOpen(false);
        setError(null);
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)));
  };

  if (!roomId) {
    return (
      <div className={s.empty}>
        <DocumentRegular className={s.emptyIcon} />
        <span>Select a room to view its plan</span>
      </div>
    );
  }

  if (loading) {
    return (
      <div className={s.empty}>
        <Spinner size="medium" label="Loading plan…" />
      </div>
    );
  }

  return (
    <div className={s.root}>
      <div className={s.toolbar}>
        <span className={s.toolbarTitle}>Collaboration Plan</span>
        <span className={s.spacer} />

        {editing ? (
          <>
            <Button icon={<SaveRegular />} appearance="primary" disabled={saving} onClick={handleSave}>
              {saving ? "Saving…" : "Save"}
            </Button>
            <Button icon={<DismissRegular />} appearance="subtle" onClick={handleCancel}>
              Cancel
            </Button>
          </>
        ) : (
          <>
            <Button icon={<EditRegular />} appearance="subtle" onClick={handleEdit}>
              Edit
            </Button>
            {content && (
              <Dialog open={confirmOpen} onOpenChange={(_e, data) => setConfirmOpen(data.open)}>
                <DialogTrigger disableButtonEnhancement>
                  <Button icon={<DeleteRegular />} appearance="subtle">Delete</Button>
                </DialogTrigger>
                <DialogSurface>
                  <DialogBody>
                    <DialogTitle>Delete plan?</DialogTitle>
                    This action cannot be undone.
                    <DialogActions>
                      <DialogTrigger disableButtonEnhancement>
                        <Button appearance="secondary">Cancel</Button>
                      </DialogTrigger>
                      <Button appearance="primary" onClick={handleDelete}>Delete</Button>
                    </DialogActions>
                  </DialogBody>
                </DialogSurface>
              </Dialog>
            )}
          </>
        )}
      </div>

      {error && (
        <div className={s.errorBanner}>
          <ErrorCircleRegular fontSize={16} />
          <span style={{ flex: 1 }}>{error}</span>
          <Button appearance="subtle" size="small" onClick={() => setError(null)}>Dismiss</Button>
        </div>
      )}

      <div className={s.content}>
        {editing ? (
          <Textarea
            className={s.textarea}
            resize="vertical"
            value={draft}
            onChange={(_e, d) => setDraft(d.value)}
          />
        ) : content ? (
          <div className={s.markdown}>
            <Markdown remarkPlugins={[remarkGfm]}>{content}</Markdown>
          </div>
        ) : (
          <div className={s.empty}>
            <DocumentRegular className={s.emptyIcon} />
            <span>No plan yet</span>
            <Button icon={<EditRegular />} appearance="primary" onClick={handleEdit}>
              Create plan
            </Button>
          </div>
        )}
      </div>
    </div>
  );
}

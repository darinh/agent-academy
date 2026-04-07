import { useCallback, useEffect, useState } from "react";
import {
  Button,
  Spinner,
  Input,
  Textarea,
  makeStyles,
  shorthands,
  Dialog,
  DialogSurface,
  DialogBody,
  DialogActions,
  DialogContent,
  DialogTitle,
} from "@fluentui/react-components";
import {
  ChevronDownRegular,
  ChevronUpRegular,
  SaveRegular,
  DeleteRegular,
  AddRegular,
} from "@fluentui/react-icons";
import {
  createInstructionTemplate,
  updateInstructionTemplate,
  deleteInstructionTemplate,
  type InstructionTemplate,
} from "./api";

// ── Styles ──────────────────────────────────────────────────────────────

const useLocalStyles = makeStyles({
  card: {
    ...shorthands.padding("16px"),
    ...shorthands.borderRadius("12px"),
    border: "1px solid var(--aa-hairline)",
    backgroundColor: "rgba(255, 255, 255, 0.03)",
    marginBottom: "12px",
  },
  cardHeader: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    cursor: "pointer",
    gap: "12px",
    userSelect: "none",
  },
  templateInfo: {
    display: "flex",
    flexDirection: "column",
    minWidth: 0,
  },
  templateName: {
    fontSize: "14px",
    fontWeight: 600,
    color: "var(--aa-text-strong)",
  },
  templateDesc: {
    fontSize: "12px",
    color: "var(--aa-muted)",
    overflow: "hidden",
    textOverflow: "ellipsis",
    maxWidth: "400px",
  },
  formContainer: {
    marginTop: "16px",
    ...shorthands.borderTop("1px", "solid", "var(--aa-hairline)"),
    paddingTop: "16px",
    display: "flex",
    flexDirection: "column",
    gap: "16px",
  },
  fieldGroup: {
    display: "flex",
    flexDirection: "column",
    gap: "4px",
  },
  fieldLabel: {
    fontSize: "12px",
    fontWeight: 600,
    color: "var(--aa-soft)",
    textTransform: "uppercase" as const,
    letterSpacing: "0.5px",
  },
  actions: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    justifyContent: "flex-end",
    marginTop: "4px",
  },
  error: {
    fontSize: "13px",
    color: "var(--aa-copper)",
  },
  createButton: {
    marginBottom: "12px",
  },
});

// ── Component ───────────────────────────────────────────────────────────

interface TemplateCardProps {
  template?: InstructionTemplate;
  isNew?: boolean;
  expanded: boolean;
  onToggle: () => void;
  onSaved: () => void;
  onCancelNew?: () => void;
}

export default function TemplateCard({
  template,
  isNew,
  expanded,
  onToggle,
  onSaved,
  onCancelNew,
}: TemplateCardProps) {
  const s = useLocalStyles();

  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showDeleteDialog, setShowDeleteDialog] = useState(false);

  // Form state — re-sync when template prop changes (component is keyed by id, not remounted)
  const [name, setName] = useState(template?.name ?? "");
  const [description, setDescription] = useState(template?.description ?? "");
  const [content, setContent] = useState(template?.content ?? "");

  useEffect(() => {
    setName(template?.name ?? "");
    setDescription(template?.description ?? "");
    setContent(template?.content ?? "");
  }, [template]);

  const canSave = name.trim() && content.trim();

  const handleSave = useCallback(async () => {
    if (!canSave) return;
    setSaving(true);
    setError(null);
    try {
      if (isNew) {
        await createInstructionTemplate({
          name: name.trim(),
          description: description.trim() || null,
          content: content,
        });
      } else if (template) {
        await updateInstructionTemplate(template.id, {
          name: name.trim(),
          description: description.trim() || null,
          content: content,
        });
      }
      onSaved();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save template");
    } finally {
      setSaving(false);
    }
  }, [isNew, template, name, description, content, canSave, onSaved]);

  const handleDelete = useCallback(async () => {
    if (!template) return;
    setShowDeleteDialog(false);
    setSaving(true);
    setError(null);
    try {
      await deleteInstructionTemplate(template.id);
      onSaved();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete template");
    } finally {
      setSaving(false);
    }
  }, [template, onSaved]);

  // New template card — always expanded
  if (isNew) {
    return (
      <div className={s.card}>
        <div className={s.cardHeader}>
          <div className={s.templateInfo}>
            <span className={s.templateName}>
              <AddRegular /> New Template
            </span>
          </div>
        </div>
        <div className={s.formContainer}>
          <div className={s.fieldGroup}>
            <label className={s.fieldLabel}>Name</label>
            <Input
              placeholder="Template name (must be unique)"
              value={name}
              onChange={(_, data) => setName(data.value)}
            />
          </div>
          <div className={s.fieldGroup}>
            <label className={s.fieldLabel}>Description</label>
            <Input
              placeholder="Short description (optional)"
              value={description}
              onChange={(_, data) => setDescription(data.value)}
            />
          </div>
          <div className={s.fieldGroup}>
            <label className={s.fieldLabel}>Content</label>
            <Textarea
              placeholder="Instruction template content — appended to agent prompts when assigned"
              value={content}
              onChange={(_, data) => setContent(data.value)}
              resize="vertical"
              rows={6}
            />
          </div>
          {error && <div className={s.error}>{error}</div>}
          <div className={s.actions}>
            <Button
              appearance="subtle"
              size="small"
              disabled={saving}
              onClick={onCancelNew}
            >
              Cancel
            </Button>
            <Button
              appearance="primary"
              size="small"
              icon={<SaveRegular />}
              disabled={saving || !canSave}
              onClick={handleSave}
            >
              {saving ? <Spinner size="tiny" /> : "Create"}
            </Button>
          </div>
        </div>
      </div>
    );
  }

  if (!template) return null;

  return (
    <div className={s.card}>
      <div className={s.cardHeader} onClick={onToggle}>
        <div className={s.templateInfo}>
          <span className={s.templateName}>{template.name}</span>
          {template.description && (
            <span className={s.templateDesc}>{template.description}</span>
          )}
        </div>
        {expanded ? <ChevronUpRegular /> : <ChevronDownRegular />}
      </div>

      {expanded && (
        <div className={s.formContainer}>
          <div className={s.fieldGroup}>
            <label className={s.fieldLabel}>Name</label>
            <Input
              value={name}
              onChange={(_, data) => setName(data.value)}
            />
          </div>
          <div className={s.fieldGroup}>
            <label className={s.fieldLabel}>Description</label>
            <Input
              placeholder="Short description (optional)"
              value={description}
              onChange={(_, data) => setDescription(data.value)}
            />
          </div>
          <div className={s.fieldGroup}>
            <label className={s.fieldLabel}>Content</label>
            <Textarea
              value={content}
              onChange={(_, data) => setContent(data.value)}
              resize="vertical"
              rows={6}
            />
          </div>
          {error && <div className={s.error}>{error}</div>}
          <div className={s.actions}>
            <Button
              appearance="subtle"
              size="small"
              icon={<DeleteRegular />}
              disabled={saving}
              onClick={() => setShowDeleteDialog(true)}
            >
              Delete
            </Button>
            <Button
              appearance="primary"
              size="small"
              icon={<SaveRegular />}
              disabled={saving || !canSave}
              onClick={handleSave}
            >
              {saving ? <Spinner size="tiny" /> : "Save"}
            </Button>
          </div>
        </div>
      )}

      {/* Delete confirmation dialog */}
      <Dialog open={showDeleteDialog} onOpenChange={(_, data) => setShowDeleteDialog(data.open)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Delete "{template.name}"?</DialogTitle>
            <DialogContent>
              This will permanently delete the template. Any agents using this template
              will have their template assignment cleared (reverts to no template).
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setShowDeleteDialog(false)}>
                Cancel
              </Button>
              <Button appearance="primary" onClick={handleDelete}>
                Delete
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}

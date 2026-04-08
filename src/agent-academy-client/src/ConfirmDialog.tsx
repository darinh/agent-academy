import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  makeStyles,
} from "@fluentui/react-components";
import type { ReactNode } from "react";

const useLocalStyles = makeStyles({
  surface: {
    background:
      "linear-gradient(180deg, rgba(18, 23, 33, 0.98), rgba(9, 12, 18, 0.98))",
    border: "1px solid var(--aa-border)",
    borderRadius: "20px",
    color: "var(--aa-text)",
    maxWidth: "420px",
  },
  title: {
    color: "var(--aa-text-strong)",
    fontFamily: "var(--heading)",
  },
  content: {
    color: "var(--aa-muted)",
    fontSize: "14px",
    lineHeight: 1.6,
  },
});

interface ConfirmDialogProps {
  open: boolean;
  onConfirm: () => void;
  onCancel: () => void;
  title: string;
  message: string;
  confirmLabel?: string;
  confirmAppearance?: "primary" | "subtle";
  cancelLabel?: string;
  icon?: ReactNode;
}

export default function ConfirmDialog({
  open,
  onConfirm,
  onCancel,
  title,
  message,
  confirmLabel = "Confirm",
  confirmAppearance = "primary",
  cancelLabel = "Cancel",
}: ConfirmDialogProps) {
  const s = useLocalStyles();

  return (
    <Dialog open={open} onOpenChange={(_, data) => { if (!data.open) onCancel(); }}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle className={s.title}>{title}</DialogTitle>
          <DialogContent className={s.content}>{message}</DialogContent>
          <DialogActions>
            <DialogTrigger disableButtonEnhancement>
              <Button appearance="subtle" onClick={onCancel}>
                {cancelLabel}
              </Button>
            </DialogTrigger>
            <Button appearance={confirmAppearance} onClick={onConfirm}>
              {confirmLabel}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

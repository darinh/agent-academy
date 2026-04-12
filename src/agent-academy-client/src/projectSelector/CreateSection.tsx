import { useCallback, useState } from "react";
import { Button, Input, Spinner } from "@fluentui/react-components";
import { onboardProject } from "../api";
import type { OnboardResult } from "../api";
import { useProjectSelectorStyles } from "./projectSelectorStyles";

interface CreateSectionProps {
  onProjectOnboarded: (result: OnboardResult) => void;
}

export default function CreateSection({ onProjectOnboarded }: CreateSectionProps) {
  const classes = useProjectSelectorStyles();
  const [dirPath, setDirPath] = useState("");
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);

  const handleCreate = useCallback(async () => {
    const trimmed = dirPath.trim();
    if (!trimmed) return;
    setCreating(true);
    setCreateError(null);
    try {
      const result = await onboardProject(trimmed);
      onProjectOnboarded(result);
    } catch (err) {
      setCreateError(err instanceof Error ? err.message : String(err));
    } finally {
      setCreating(false);
    }
  }, [dirPath, onProjectOnboarded]);

  return (
    <div className={classes.form}>
      <div>
        <div className={classes.fieldLabel}>Directory path</div>
        <Input
          className={classes.fieldInput}
          placeholder="/home/user/projects/my-awesome-project"
          aria-label="Directory path"
          value={dirPath}
          onChange={(_, data) => setDirPath(data.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") void handleCreate();
          }}
        />
      </div>
      <div className={classes.actionRow}>
        <Button
          appearance="primary"
          icon={creating ? <Spinner size="tiny" /> : undefined}
          onClick={() => void handleCreate()}
          disabled={!dirPath.trim() || creating}
        >
          {creating ? "Creating…" : "Create & open"}
        </Button>
      </div>
      {createError && <div className={classes.errorText}>{createError}</div>}
    </div>
  );
}

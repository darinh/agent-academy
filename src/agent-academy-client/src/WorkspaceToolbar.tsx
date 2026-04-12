import {
  Button,
  Menu,
  MenuTrigger,
  MenuPopover,
  MenuList,
  MenuItemCheckbox,
} from "@fluentui/react-components";
import type { MenuCheckedValueChangeData } from "@fluentui/react-components";
import V3Badge from "./V3Badge";
import type { CollaborationPhase } from "./api";

export interface ToolbarModel {
  tab: string;
  chatToolbar: {
    currentPhase: string;
    onPhaseChange: (phase: CollaborationPhase) => void;
    disabled: boolean;
    filterChecked: Record<string, string[]>;
    hiddenFilterCount: number;
    onFilterChange: (_: unknown, data: MenuCheckedValueChangeData) => void;
  } | null;
}

export interface WorkspaceToolbarProps {
  model: ToolbarModel;
  styles: Record<string, string>;
}

const TOOLBAR_META: Record<string, string> = {
  tasks: "Sorted by newest",
  commands: "Command Deck",
  sprint: "Active iteration",
  timeline: "All events",
  dashboard: "System telemetry",
};

export default function WorkspaceToolbar({ model, styles: s }: WorkspaceToolbarProps) {
  const metaText = TOOLBAR_META[model.tab];

  return (
    <div className={s.tabBar}>
      <div className={s.tabStrip}>
        {model.chatToolbar && (<>
          <select
            className={s.toolbarSelect}
            value={model.chatToolbar.currentPhase}
            onChange={(e) => model.chatToolbar!.onPhaseChange(e.target.value as CollaborationPhase)}
            disabled={model.chatToolbar.disabled}
            title="Change room phase"
          >
            <option value="Intake">Intake</option>
            <option value="Planning">Planning</option>
            <option value="Discussion">Discussion</option>
            <option value="Implementation">Implementation</option>
            <option value="Validation">Validation</option>
            <option value="FinalSynthesis">Final Synthesis</option>
          </select>
          <Menu checkedValues={model.chatToolbar.filterChecked} onCheckedValueChange={model.chatToolbar.onFilterChange}>
            <MenuTrigger disableButtonEnhancement>
              <Button size="small" appearance="subtle" className={s.filterMenuButton}>
                ▾ Filter
                {model.chatToolbar.hiddenFilterCount > 0 && (
                  <V3Badge color="info" className={s.filterBadge}>
                    {model.chatToolbar.hiddenFilterCount}
                  </V3Badge>
                )}
              </Button>
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                <MenuItemCheckbox name="show" value="system">System messages</MenuItemCheckbox>
                <MenuItemCheckbox name="show" value="commands">Command results</MenuItemCheckbox>
              </MenuList>
            </MenuPopover>
          </Menu>
        </>)}
        {metaText && !model.chatToolbar && (
          <span className={s.workspaceMetaText}>{metaText}</span>
        )}
      </div>
    </div>
  );
}

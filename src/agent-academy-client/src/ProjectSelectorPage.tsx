import { useCallback, useState } from "react";
import { Tab, TabList } from "@fluentui/react-components";
import type { AuthUser, OnboardResult } from "./api";
import UserBadge from "./UserBadge";
import {
  LoadExistingSection,
  OnboardSection,
  CreateSection,
  useProjectSelectorStyles,
  TAB_COPY,
  RAIL_POINTS,
} from "./projectSelector";
import type { SelectorTab } from "./projectSelector";

interface ProjectSelectorPageProps {
  onProjectSelected: (workspacePath: string) => void;
  onProjectOnboarded?: (result: OnboardResult) => void;
  user?: AuthUser | null;
  onLogout?: () => void;
}

export default function ProjectSelectorPage({ onProjectSelected, onProjectOnboarded, user, onLogout }: ProjectSelectorPageProps) {
  const classes = useProjectSelectorStyles();
  const [tab, setTab] = useState<SelectorTab>("onboard");
  const tabCopy = TAB_COPY[tab];
  const userName = user?.name ?? user?.login;

  const handleOnboarded = useCallback((result: OnboardResult) => {
    if (onProjectOnboarded) {
      onProjectOnboarded(result);
    } else {
      onProjectSelected(result.workspace.path);
    }
  }, [onProjectOnboarded, onProjectSelected]);

  return (
    <div className={classes.root}>
      <div className={classes.backdrop} />
      <div className={classes.container}>
        <section className={classes.rail}>
          <div className={classes.railHeader}>
            <div className={classes.railKicker}>Workspace staging</div>
            <h1 className={classes.railTitle}>Choose the project with intent.</h1>
            <p className={classes.railBody}>
              {userName
                ? `Welcome back, ${userName}. Bring an existing repository forward or onboard a new one without leaving the client.`
                : "Move from directory discovery into collaboration without a clunky handoff between tools."}
            </p>
          </div>

          <div className={classes.railGrid}>
            {RAIL_POINTS.map((point) => (
              <div key={point.label} className={classes.railCard}>
                <div className={classes.railCardLabel}>{point.label}</div>
                <div className={classes.railCardValue}>{point.value}</div>
                <div className={classes.railCardBody}>{point.body}</div>
              </div>
            ))}
          </div>

          <div className={classes.railFootnote}>
            The frontend now treats Copilot availability as a first-class state, so onboarding and workspace entry feel
            consistent with the rest of the application instead of bolted on.
          </div>
        </section>

        <section className={classes.deck}>
          <div className={classes.deckTop}>
            <div className={classes.deckHeader}>
              <div className={classes.deckKicker}>{tabCopy.kicker}</div>
              <div className={classes.deckTitle}>{tabCopy.title}</div>
              <div className={classes.deckDescription}>{tabCopy.description}</div>
            </div>
            {user && onLogout && (
              <div className={classes.userWrap}>
                <UserBadge user={user} onLogout={onLogout} />
              </div>
            )}
          </div>

          <TabList
            className={classes.tabList}
            selectedValue={tab}
            onTabSelect={(_, data) => setTab(data.value as SelectorTab)}
          >
            <Tab value="existing">Existing</Tab>
            <Tab value="onboard">Onboard</Tab>
            <Tab value="create">Create</Tab>
          </TabList>

          <div className={classes.panel}>
            {tab === "existing" && <LoadExistingSection onProjectSelected={onProjectSelected} />}
            {tab === "onboard" && <OnboardSection onProjectOnboarded={handleOnboarded} />}
            {tab === "create" && <CreateSection onProjectOnboarded={handleOnboarded} />}
          </div>
        </section>
      </div>
    </div>
  );
}

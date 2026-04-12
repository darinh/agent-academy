export type SelectorTab = "existing" | "onboard" | "create";

export const TAB_COPY: Record<SelectorTab, { kicker: string; title: string; description: string }> = {
  existing: {
    kicker: "Resume work",
    title: "Return to an active workspace",
    description: "Jump back into a project already known to Agent Academy and keep the current room context intact.",
  },
  onboard: {
    kicker: "Inspect before entering",
    title: "Scan a repository and onboard it cleanly",
    description: "Review the project shape first, then activate it with the right spec expectations and workspace metadata.",
  },
  create: {
    kicker: "Start from a fresh directory",
    title: "Open a new project path directly",
    description: "Point Agent Academy at a destination folder and create the workspace without leaving the client.",
  },
};

export const RAIL_POINTS = [
  {
    label: "Collaboration",
    value: "Six specialists, one room",
    body: "Planning, implementation, review, validation, and specs stay visible in the same interface.",
  },
  {
    label: "Branch flow",
    value: "Task branches by default",
    body: "Work happens off develop so breakout rounds can ship incrementally without trashing the main line.",
  },
  {
    label: "Spec discipline",
    value: "Reality over aspiration",
    body: "Projects with missing specs get surfaced early so the system can onboard them deliberately.",
  },
];

export function relativeTime(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime();
  if (diff < 0) return "just now";
  const seconds = Math.floor(diff / 1000);
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

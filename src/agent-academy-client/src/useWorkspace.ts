import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  getOverview,
  sendHumanMessage,
  transitionPhase,
  submitTask,
} from "./api";
import type {
  ActivityEvent,
  AgentPresence,
  CollaborationPhase,
  RoomSnapshot,
  TaskAssignmentResult,
  WorkspaceOverview,
} from "./api";
import { workspaceChanged, sameActivityFeed } from "./utils";

export type ThinkingAgent = { id: string; name: string; role: string };

export interface TaskDraft {
  title: string;
  description: string;
  successCriteria: string;
  roomId?: string;
}

const BACKGROUND_POLL_MS = 30_000;
const EVENT_REFRESH_DEBOUNCE_MS = 1_000;

const TAB_STORAGE_KEY = "aa-active-tab";
const SIDEBAR_STORAGE_KEY = "aa-sidebar-open";

function loadTab(): string {
  try { return localStorage.getItem(TAB_STORAGE_KEY) ?? "chat"; } catch { return "chat"; }
}
function loadSidebar(): boolean {
  try { return localStorage.getItem(SIDEBAR_STORAGE_KEY) !== "false"; } catch { return true; }
}

const empty: WorkspaceOverview = {
  configuredAgents: [],
  rooms: [],
  recentActivity: [],
  agentLocations: [],
  breakoutRooms: [],
  generatedAt: "",
};

export function useWorkspace() {
  const refreshTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const refreshInFlight = useRef(false);

  const [ov, setOv] = useState<WorkspaceOverview>(empty);
  const [recentActivity, setRecentActivity] = useState<ActivityEvent[]>([]);
  const [roomId, setRoomId] = useState("");
  const [err, setErr] = useState("");
  const [busy, setBusy] = useState(true);
  const [tab, setTabRaw] = useState<string>(loadTab);
  const [sidebarOpen, setSidebarOpen] = useState(loadSidebar);
  const [thinkingAgents, setThinkingAgents] = useState<Map<string, { name: string; role: string }>>(new Map());

  const setTab = useCallback((value: string) => {
    setTabRaw(value);
    try { localStorage.setItem(TAB_STORAGE_KEY, value); } catch { /* quota */ }
  }, []);

  const room: RoomSnapshot | null = useMemo(
    () => ov.rooms.find((r) => r.id === roomId) ?? ov.rooms[0] ?? null,
    [ov.rooms, roomId],
  );

  const roster: AgentPresence[] = useMemo(() => {
    if (room?.participants.length) return room.participants;
    return ov.configuredAgents.map((agent) => ({
      agentId: agent.id,
      name: agent.name,
      role: agent.role,
      availability: "Ready" as const,
      isPreferred: false,
      lastActivityAt: ov.generatedAt,
      activeCapabilities: agent.capabilityTags,
    }));
  }, [ov.configuredAgents, ov.generatedAt, room?.participants]);

  const activity = useMemo(() => {
    if (!room) return recentActivity.slice(0, 20);
    return recentActivity
      .filter((e) => !e.roomId || e.roomId === room.id)
      .slice(0, 20);
  }, [recentActivity, room]);

  const thinkingAgentList = useMemo<ThinkingAgent[]>(
    () => Array.from(thinkingAgents.entries(), ([id, info]) => ({ id, ...info })),
    [thinkingAgents],
  );

  const refresh = useCallback(async (opts: { showBusy?: boolean } = {}) => {
    const showBusy = opts.showBusy ?? true;
    if (refreshInFlight.current) return;
    refreshInFlight.current = true;
    if (showBusy) { setBusy(true); setErr(""); }

    try {
      const next = await getOverview();
      setOv((cur) => (workspaceChanged(cur, next) ? next : cur));
      setRecentActivity((cur) =>
        sameActivityFeed(cur, next.recentActivity) ? cur : next.recentActivity,
      );
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Load failed");
    } finally {
      refreshInFlight.current = false;
      if (showBusy) setBusy(false);
    }
  }, []);

  const scheduleRefresh = useCallback(() => {
    if (refreshTimer.current) clearTimeout(refreshTimer.current);
    refreshTimer.current = setTimeout(() => {
      void refresh({ showBusy: false });
    }, EVENT_REFRESH_DEBOUNCE_MS);
  }, [refresh]);

  useEffect(() => {
    void refresh();

    const poll = setInterval(() => {
      if (document.visibilityState === "visible") void refresh({ showBusy: false });
    }, BACKGROUND_POLL_MS);

    return () => {
      clearInterval(poll);
      if (refreshTimer.current) clearTimeout(refreshTimer.current);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (ov.rooms.length && !ov.rooms.some((r) => r.id === roomId)) {
      setRoomId(ov.rooms[0].id);
    }
  }, [ov.rooms, roomId]);

  const handleRoomSelect = useCallback((id: string) => setRoomId(id), []);

  const handlePhaseTransition = useCallback(async (phase: CollaborationPhase) => {
    if (!roomId) return;
    try {
      await transitionPhase(roomId, phase, "Manual transition from UI");
      await refresh({ showBusy: false });
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Phase transition failed");
    }
  }, [roomId, refresh]);

  const handleManualRefresh = useCallback(() => { void refresh(); }, [refresh]);

  const handleToggleSidebar = useCallback(() => {
    setSidebarOpen((cur) => {
      const next = !cur;
      try { localStorage.setItem(SIDEBAR_STORAGE_KEY, String(next)); } catch { /* quota */ }
      return next;
    });
  }, []);

  const handleTaskSubmit = useCallback(async (draft: TaskDraft) => {
    setErr("");
    try {
      const result: TaskAssignmentResult = await submitTask({
        title: draft.title,
        description: draft.description,
        successCriteria: draft.successCriteria,
        preferredRoles: [],
        roomId: draft.roomId,
      });
      await refresh({ showBusy: false });
      setRoomId(result.room.id);
      setTab("chat");
      return true;
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Submit failed");
      return false;
    }
  }, [refresh, setTab]);

  const handleSendMessage = useCallback(async (targetRoomId: string, content: string) => {
    setErr("");
    try {
      await sendHumanMessage(targetRoomId, content);
      void refresh({ showBusy: false });
      return true;
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Send failed");
      return false;
    }
  }, [refresh]);

  const roomSummary =
    room?.activeTask?.description ??
    "Select a room to inspect the conversation, room state, and timeline.";

  return {
    ov,
    room,
    roomId,
    roster,
    activity,
    thinkingAgentList,
    agentLocations: ov.agentLocations ?? [],
    breakoutRooms: ov.breakoutRooms ?? [],
    err,
    busy,
    tab,
    setTab,
    sidebarOpen,
    roomSummary,
    handleRoomSelect,
    handlePhaseTransition,
    handleManualRefresh,
    handleToggleSidebar,
    handleTaskSubmit,
    handleSendMessage,
    scheduleRefresh,
    setThinkingAgents,
  };
}

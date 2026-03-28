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
import { useActivityHub, type ConnectionStatus } from "./useActivityHub";

export type ThinkingAgent = { id: string; name: string; role: string };

export interface TaskDraft {
  title: string;
  description: string;
  successCriteria: string;
  roomId?: string;
}

const FALLBACK_POLL_MS = 120_000;

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
  // Thinking state keyed by roomId → Map<agentId, info>
  const [thinkingByRoom, setThinkingByRoom] = useState<Map<string, Map<string, { name: string; role: string }>>>(new Map());

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

  const thinkingAgentList = useMemo<ThinkingAgent[]>(() => {
    const roomMap = thinkingByRoom.get(room?.id ?? "");
    if (!roomMap?.size) return [];
    return Array.from(roomMap.entries(), ([id, info]) => ({ id, ...info }));
  }, [thinkingByRoom, room?.id]);

  // Ref so the SignalR callback always sees current configuredAgents without re-subscribing
  const agentsRef = useRef(ov.configuredAgents);
  agentsRef.current = ov.configuredAgents;

  const refreshRef = useRef<(opts?: { showBusy?: boolean }) => Promise<void>>(undefined);

  const handleActivityEvent = useCallback((evt: ActivityEvent) => {
    switch (evt.type) {
      case "AgentThinking": {
        if (!evt.roomId || !evt.actorId) break;
        const agent = agentsRef.current.find((a) => a.id === evt.actorId);
        if (agent) {
          setThinkingByRoom((prev) => {
            const next = new Map(prev);
            const roomMap = new Map(prev.get(evt.roomId!) ?? []);
            roomMap.set(agent.id, { name: agent.name, role: agent.role });
            next.set(evt.roomId!, roomMap);
            return next;
          });
        }
        break;
      }
      case "AgentFinished":
        if (evt.roomId && evt.actorId) {
          setThinkingByRoom((prev) => {
            const roomMap = prev.get(evt.roomId!);
            if (!roomMap?.has(evt.actorId!)) return prev;
            const next = new Map(prev);
            const newRoomMap = new Map(roomMap);
            newRoomMap.delete(evt.actorId!);
            if (newRoomMap.size === 0) next.delete(evt.roomId!);
            else next.set(evt.roomId!, newRoomMap);
            return next;
          });
        }
        break;
      case "MessagePosted":
      case "RoomCreated":
      case "TaskCreated":
      case "PhaseChanged":
      case "PresenceUpdated":
        void refreshRef.current?.({ showBusy: false });
        break;
    }
  }, []);

  const connectionStatus: ConnectionStatus = useActivityHub(handleActivityEvent);

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
      // Reconcile thinking state: clear entries for agents no longer in Working state
      setThinkingByRoom((prev) => {
        if (prev.size === 0) return prev;
        const workingIds = new Set(
          next.agentLocations
            .filter((loc) => loc.state === "Working")
            .map((loc) => loc.agentId),
        );
        let changed = false;
        const cleaned = new Map(prev);
        for (const [rid, roomMap] of cleaned) {
          for (const agentId of roomMap.keys()) {
            if (!workingIds.has(agentId)) {
              roomMap.delete(agentId);
              changed = true;
            }
          }
          if (roomMap.size === 0) { cleaned.delete(rid); changed = true; }
        }
        return changed ? cleaned : prev;
      });
      setErr("");
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Load failed");
    } finally {
      refreshInFlight.current = false;
      if (showBusy) setBusy(false);
    }
  }, []);

  refreshRef.current = refresh;

  useEffect(() => {
    void refresh();

    const poll = setInterval(() => {
      if (document.visibilityState === "visible") void refresh({ showBusy: false });
    }, FALLBACK_POLL_MS);

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
    thinkingByRoom,
    connectionStatus,
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
  };
}

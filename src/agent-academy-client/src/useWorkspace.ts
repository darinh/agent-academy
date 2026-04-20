import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  getOverview,
  getInstanceHealth,
  sendHumanMessage,
  transitionPhase,
  submitTask,
  getRoomContextUsage,
} from "./api";
import type {
  ActivityEvent,
  AgentContextUsage,
  AgentPresence,
  CollaborationPhase,
  RoomSnapshot,
  SprintRealtimeEvent,
  TaskAssignmentResult,
  WorkspaceOverview,
} from "./api";
import { workspaceChanged, sameActivityFeed } from "./utils";
import { useActivityHub } from "./useActivityHub";
import { useActivitySSE } from "./useActivitySSE";
import type { ConnectionStatus } from "./useActivityHub";
import type { RecoveryBannerState } from "./RecoveryBanner";
import { evaluateReconnect, RECONNECTING_BANNER } from "./healthCheck";

export type ActivityTransport = "signalr" | "sse";

const TRANSPORT_STORAGE_KEY = "aa-transport";

export type ThinkingAgent = { id: string; name: string; role: string };

export interface TaskDraft {
  title: string;
  description: string;
  successCriteria: string;
  roomId?: string;
  priority?: "Critical" | "High" | "Medium" | "Low";
}

const FALLBACK_POLL_MS = 120_000;
const RECOVERY_BANNER_DISMISS_MS = 4_000;

const TAB_STORAGE_KEY = "aa-active-tab";
const SIDEBAR_STORAGE_KEY = "aa-sidebar-open";
const SIDEBAR_PIN_KEY = "aa-sidebar-pinned";
const NARROW_VIEWPORT_PX = 900;

const VALID_TABS = new Set(["chat", "tasks", "plan", "commands", "timeline", "dashboard", "overview", "directMessages", "search", "sprint", "memories", "digests", "retrospectives", "artifacts"]);

function loadTab(): string {
  try {
    const saved = localStorage.getItem(TAB_STORAGE_KEY) ?? "chat";
    return VALID_TABS.has(saved) ? saved : "chat";
  } catch { return "chat"; }
}
function loadSidebar(): boolean {
  try { return localStorage.getItem(SIDEBAR_STORAGE_KEY) !== "false"; } catch { return true; }
}
function loadSidebarPin(): boolean {
  try { return localStorage.getItem(SIDEBAR_PIN_KEY) !== "false"; } catch { return true; }
}
function loadTransport(): ActivityTransport {
  try {
    const v = localStorage.getItem(TRANSPORT_STORAGE_KEY);
    return v === "sse" ? "sse" : "signalr";
  } catch { return "signalr"; }
}

const empty: WorkspaceOverview = {
  configuredAgents: [],
  rooms: [],
  recentActivity: [],
  agentLocations: [],
  breakoutRooms: [],
  generatedAt: "",
};

export interface UseWorkspaceOptions {
  onActivityEvent?: (evt: ActivityEvent) => void;
}

export function useWorkspace(options?: UseWorkspaceOptions) {
  const onActivityEventRef = useRef(options?.onActivityEvent);
  onActivityEventRef.current = options?.onActivityEvent;
  const recoveryTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const refreshInFlight = useRef(false);

  const [ov, setOv] = useState<WorkspaceOverview>(empty);
  const [recentActivity, setRecentActivity] = useState<ActivityEvent[]>([]);
  const [roomId, setRoomId] = useState("");
  const [err, setErr] = useState("");
  const [busy, setBusy] = useState(true);
  const [instanceId, setInstanceId] = useState<string | null>(null);
  const [recoveryBanner, setRecoveryBanner] = useState<RecoveryBannerState | null>(null);
  const [tab, setTabRaw] = useState<string>(loadTab);
  const [sidebarOpen, setSidebarOpen] = useState(loadSidebar);
  const [sidebarPinned, setSidebarPinned] = useState(loadSidebarPin);

  // Auto-collapse on narrow viewport when unpinned
  useEffect(() => {
    if (typeof window === "undefined" || !window.matchMedia) return;
    const mq = window.matchMedia(`(max-width: ${NARROW_VIEWPORT_PX}px)`);
    const handler = (e: MediaQueryListEvent | MediaQueryList) => {
      if (e.matches && !loadSidebarPin()) {
        setSidebarOpen(false);
        try { localStorage.setItem(SIDEBAR_STORAGE_KEY, "false"); } catch { /* */ }
      }
    };
    handler(mq);
    mq.addEventListener("change", handler);
    return () => mq.removeEventListener("change", handler);
  }, []); // eslint-disable-line react-hooks/exhaustive-deps
  // Thinking state keyed by roomId → Map<agentId, info>
  const [thinkingByRoom, setThinkingByRoom] = useState<Map<string, Map<string, { name: string; role: string }>>>(new Map());
  // Context usage keyed by roomId → Map<agentId, AgentContextUsage>
  const [contextByRoom, setContextByRoom] = useState<Map<string, Map<string, import("./api").AgentContextUsage>>>(new Map());
  const [sprintVersion, setSprintVersion] = useState(0);
  const [retroVersion, setRetroVersion] = useState(0);
  const [digestVersion, setDigestVersion] = useState(0);
  const [memoryVersion, setMemoryVersion] = useState(0);
  const [artifactVersion, setArtifactVersion] = useState(0);
  const [lastSprintEvent, setLastSprintEvent] = useState<SprintRealtimeEvent | null>(null);
  const processedSprintEventIds = useRef(new Set<string>());

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

  const roomContextUsage = useMemo(() => {
    return contextByRoom.get(room?.id ?? "");
  }, [contextByRoom, room?.id]);

  // Ref so the SignalR callback always sees current configuredAgents without re-subscribing
  const agentsRef = useRef(ov.configuredAgents);
  agentsRef.current = ov.configuredAgents;
  const instanceIdRef = useRef<string | null>(instanceId);
  instanceIdRef.current = instanceId;
  const connectionStatusRef = useRef<ConnectionStatus>("disconnected");

  const refreshRef = useRef<(opts?: { showBusy?: boolean }) => Promise<boolean>>(undefined);

  const handleActivityEvent = useCallback((evt: ActivityEvent) => {
    onActivityEventRef.current?.(evt);
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
      case "ContextUsageUpdated": {
        const rid = evt.roomId;
        const aid = evt.actorId;
        const meta = evt.metadata;
        if (rid && aid && meta) {
          setContextByRoom((prev) => {
            const next = new Map(prev);
            const roomMap = new Map(prev.get(rid) ?? []);
            roomMap.set(aid, {
              agentId: aid,
              roomId: rid,
              model: (meta.model as string) ?? null,
              currentTokens: (meta.currentTokens as number) ?? 0,
              maxTokens: (meta.maxTokens as number) ?? 0,
              percentage: (meta.percentage as number) ?? 0,
              updatedAt: evt.occurredAt,
            });
            next.set(rid, roomMap);
            return next;
          });
        }
        break;
      }
      case "MessagePosted":
      case "RoomCreated":
      case "TaskCreated":
      case "PhaseChanged":
      case "PresenceUpdated":
      case "DirectMessageSent":
      case "TaskPrStatusChanged":
      case "TaskUnblocked":
      case "AgentErrorOccurred":
      case "AgentWarningOccurred":
      case "SubagentFailed":
      case "SubagentCompleted":
        void refreshRef.current?.({ showBusy: false });
        break;
      case "SprintStarted":
      case "SprintStageAdvanced":
      case "SprintArtifactStored":
      case "SprintCompleted":
      case "SprintCancelled": {
        // Deduplicate events (SSE reconnect can replay recent events)
        if (processedSprintEventIds.current.has(evt.id)) break;
        processedSprintEventIds.current.add(evt.id);
        // Cap the set size to prevent memory leaks
        if (processedSprintEventIds.current.size > 200) {
          const entries = [...processedSprintEventIds.current];
          processedSprintEventIds.current = new Set(entries.slice(-100));
        }

        const sprintId = (evt.metadata?.sprintId as string) ?? undefined;
        if (sprintId && evt.metadata) {
          setLastSprintEvent({
            eventId: evt.id,
            type: evt.type,
            sprintId,
            metadata: evt.metadata,
            receivedAt: Date.now(),
          });
        }
        setSprintVersion((v) => v + 1);
        break;
      }
      case "TaskRetrospectiveCompleted":
        setRetroVersion((v) => v + 1);
        break;
      case "LearningDigestCompleted":
        setDigestVersion((v) => v + 1);
        setMemoryVersion((v) => v + 1);
        break;
      case "ArtifactEvaluated":
        setArtifactVersion((v) => v + 1);
        break;
    }
  }, []);

  const [transport] = useState<ActivityTransport>(loadTransport);
  const useSignalR = transport === "signalr";
  const hubStatus = useActivityHub(handleActivityEvent, useSignalR);
  const sseStatus = useActivitySSE(handleActivityEvent, !useSignalR);
  const connectionStatus: ConnectionStatus = useSignalR ? hubStatus : sseStatus;

  const refresh = useCallback(async (opts: { showBusy?: boolean } = {}) => {
    const showBusy = opts.showBusy ?? true;
    if (refreshInFlight.current) return false;
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
      return true;
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Load failed");
      return false;
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
      if (recoveryTimer.current) clearTimeout(recoveryTimer.current);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Fetch context usage when room changes
  useEffect(() => {
    if (!room?.id) return;
    let cancelled = false;
    getRoomContextUsage(room.id).then((usages) => {
      if (cancelled) return;
      if (usages.length === 0) return;
      setContextByRoom((prev) => {
        const next = new Map(prev);
        const existing = prev.get(room.id);
        const merged = new Map<string, AgentContextUsage>(existing ?? []);
        for (const u of usages) {
          const cur = merged.get(u.agentId);
          // Only seed from fetch if no realtime data exists or fetch is newer
          if (!cur || u.updatedAt > cur.updatedAt) {
            merged.set(u.agentId, u);
          }
        }
        next.set(room.id, merged);
        return next;
      });
    }).catch(() => { /* context fetch is best-effort */ });
    return () => { cancelled = true; };
  }, [room?.id]);

  useEffect(() => {
    let cancelled = false;

    async function loadInstanceHealth() {
      try {
        const health = await getInstanceHealth();
        if (!cancelled) {
          setInstanceId(health.instanceId);
        }
      } catch {
        // Best-effort initialization; reconnect flow will re-check.
      }
    }

    void loadInstanceHealth();

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    const previousStatus = connectionStatusRef.current;
    connectionStatusRef.current = connectionStatus;

    // Show reconnecting banner when connection drops
    if (connectionStatus === "reconnecting" && previousStatus === "connected") {
      // Clear any pending auto-dismiss timer from a prior recovery cycle
      if (recoveryTimer.current) {
        clearTimeout(recoveryTimer.current);
        recoveryTimer.current = null;
      }
      setRecoveryBanner(RECONNECTING_BANNER);
      return;
    }

    // Connection permanently lost after reconnect attempts exhausted
    if (connectionStatus === "disconnected" && previousStatus === "reconnecting") {
      if (recoveryTimer.current) {
        clearTimeout(recoveryTimer.current);
        recoveryTimer.current = null;
      }
      setRecoveryBanner({
        tone: "error",
        message: "Connection lost — unable to reconnect",
        detail: "Live updates are paused. Use manual refresh to check for new activity.",
      });
      return;
    }

    // Clear reconnecting banner if connection was never established (initial connect)
    if (connectionStatus === "connected" && previousStatus === "connecting") {
      return;
    }

    if (!useSignalR || previousStatus !== "reconnecting" || connectionStatus !== "connected") {
      return;
    }

    let cancelled = false;

    async function handleReconnect() {
      // Clear any pending auto-dismiss timer from a prior recovery cycle
      if (recoveryTimer.current) {
        clearTimeout(recoveryTimer.current);
        recoveryTimer.current = null;
      }

      const result = await evaluateReconnect(instanceIdRef.current);
      if (cancelled) return;

      if (result.state === "refresh-failed") {
        setRecoveryBanner(result.banner);
        return;
      }

      if (result.state === "resume-success") {
        // Same instance, no crash — dismiss banner and continue
        setRecoveryBanner(null);
        if (result.health && instanceIdRef.current === null) {
          setInstanceId(result.health.instanceId);
        }
        return;
      }

      // crash-recovered or instance-mismatch — clear stale state and refresh
      setThinkingByRoom(new Map());
      setRecoveryBanner(result.banner);

      const refreshed = await refresh({ showBusy: false });
      if (cancelled) return;

      if (refreshed && result.health) {
        setInstanceId(result.health.instanceId);

        // For crash recovery, keep the banner longer so the user notices
        const dismissMs = result.state === "crash-recovered"
          ? RECOVERY_BANNER_DISMISS_MS * 2
          : RECOVERY_BANNER_DISMISS_MS;

        recoveryTimer.current = setTimeout(() => {
          setRecoveryBanner(null);
          recoveryTimer.current = null;
        }, dismissMs);
        return;
      }

      setRecoveryBanner({
        tone: "error",
        message: "Server restarted, but workspace refresh did not complete.",
        detail: "Your draft message is still preserved locally. Use manual refresh to retry sync.",
      });
    }

    void handleReconnect();

    return () => {
      cancelled = true;
    };
  }, [connectionStatus, refresh, useSignalR]);

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

  const handleToggleSidebarPin = useCallback(() => {
    setSidebarPinned((cur) => {
      const next = !cur;
      try { localStorage.setItem(SIDEBAR_PIN_KEY, String(next)); } catch { /* quota */ }
      // When unpinning on a narrow viewport, auto-collapse
      if (!next && typeof window !== "undefined" && window.innerWidth <= NARROW_VIEWPORT_PX) {
        setSidebarOpen(false);
        try { localStorage.setItem(SIDEBAR_STORAGE_KEY, "false"); } catch { /* */ }
      }
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
        priority: draft.priority,
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
    roomContextUsage,
    recoveryBanner,
    connectionStatus,
    activityTransport: transport,
    agentLocations: ov.agentLocations ?? [],
    breakoutRooms: ov.breakoutRooms ?? [],
    sprintVersion,
    lastSprintEvent,
    retroVersion,
    digestVersion,
    memoryVersion,
    artifactVersion,
    err,
    busy,
    tab,
    setTab,
    sidebarOpen,
    sidebarPinned,
    roomSummary,
    handleRoomSelect,
    handlePhaseTransition,
    handleManualRefresh,
    handleToggleSidebar,
    handleToggleSidebarPin,
    handleTaskSubmit,
    handleSendMessage,
  };
}

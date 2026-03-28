/**
 * Client-side API functions for Agent Academy.
 *
 * Covers workspace, collaboration, activity, plan, filesystem, and notification endpoints.
 */

// ── Core types ─────────────────────────────────────────────────────────

export type CollaborationPhase =
  | "Intake" | "Planning" | "Discussion"
  | "Validation" | "Implementation" | "FinalSynthesis";

export type RoomStatus = "Idle" | "Active" | "AttentionRequired" | "Completed" | "Archived";
export type MessageSenderKind = "System" | "Agent" | "User";
export type TaskStatus =
  | "Queued" | "Active" | "Blocked" | "AwaitingValidation"
  | "InReview" | "ChangesRequested" | "Approved" | "Merging"
  | "Completed" | "Cancelled";
export type TaskItemStatus = "Pending" | "Active" | "Done" | "Rejected";
export type TaskSize = "XS" | "S" | "M" | "L" | "XL";
export type PullRequestStatus =
  | "Open" | "ReviewRequested" | "ChangesRequested"
  | "Approved" | "Merged" | "Closed";
export type ActivityEventType =
  | "AgentLoaded" | "AgentThinking" | "AgentFinished"
  | "RoomCreated" | "RoomClosed" | "TaskCreated"
  | "PhaseChanged" | "MessagePosted" | "MessageSent"
  | "PresenceUpdated" | "RoomStatusChanged"
  | "ArtifactEvaluated" | "QualityGateChecked" | "IterationRetried"
  | "CheckpointCreated" | "AgentErrorOccurred" | "AgentWarningOccurred"
  | "SubagentStarted" | "SubagentCompleted" | "SubagentFailed"
  | "AgentPlanChanged" | "AgentSnapshotRewound" | "ToolIntercepted";

export interface ActivityEvent {
  id: string;
  type: ActivityEventType;
  severity: "Info" | "Warning" | "Error";
  roomId?: string | null;
  actorId?: string | null;
  taskId?: string | null;
  message: string;
  correlationId?: string | null;
  occurredAt: string;
}

export interface AgentGitIdentity {
  authorName: string;
  authorEmail: string;
}

export interface AgentDefinition {
  id: string;
  name: string;
  role: string;
  summary: string;
  startupPrompt: string;
  model?: string | null;
  capabilityTags: string[];
  enabledTools: string[];
  autoJoinDefaultRoom: boolean;
  gitIdentity?: AgentGitIdentity | null;
}

export interface AgentPresence {
  agentId: string;
  name: string;
  role: string;
  availability: string;
  isPreferred: boolean;
  lastActivityAt: string;
  activeCapabilities: string[];
}

export interface AgentLocation {
  agentId: string;
  roomId: string;
  state: string;
  breakoutRoomId?: string | null;
  updatedAt: string;
}

export interface ChatEnvelope {
  id: string;
  roomId: string;
  senderId: string;
  senderName: string;
  senderRole?: string | null;
  senderKind: MessageSenderKind;
  kind: string;
  content: string;
  sentAt: string;
  correlationId?: string | null;
  replyToMessageId?: string | null;
}

export interface TaskSnapshot {
  id: string;
  title: string;
  description: string;
  successCriteria: string;
  status: TaskStatus;
  currentPhase: CollaborationPhase;
  currentPlan: string;
  validationStatus: string;
  validationSummary: string;
  implementationStatus: string;
  implementationSummary: string;
  preferredRoles: string[];
  createdAt: string;
  updatedAt: string;
  size?: TaskSize | null;
  startedAt?: string | null;
  completedAt?: string | null;
  assignedAgentId?: string | null;
  assignedAgentName?: string | null;
  usedFleet?: boolean;
  fleetModels?: string[];
  branchName?: string | null;
  pullRequestUrl?: string | null;
  pullRequestNumber?: number | null;
  pullRequestStatus?: PullRequestStatus | null;
  reviewerAgentId?: string | null;
  reviewRounds?: number;
  testsCreated?: string[];
  commitCount?: number;
}

export interface TaskItem {
  id: string;
  title: string;
  description: string;
  status: TaskItemStatus;
  assignedTo: string;
  roomId: string;
  breakoutRoomId?: string | null;
  evidence?: string | null;
  feedback?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface RoomSnapshot {
  id: string;
  name: string;
  status: RoomStatus;
  currentPhase: CollaborationPhase;
  activeTask?: TaskSnapshot | null;
  participants: AgentPresence[];
  recentMessages: ChatEnvelope[];
  createdAt: string;
  updatedAt: string;
}

export interface BreakoutRoom {
  id: string;
  name: string;
  parentRoomId: string;
  assignedAgentId: string;
  tasks: TaskItem[];
  status: RoomStatus;
  recentMessages: ChatEnvelope[];
  createdAt: string;
  updatedAt: string;
}

export interface WorkspaceOverview {
  configuredAgents: AgentDefinition[];
  rooms: RoomSnapshot[];
  recentActivity: ActivityEvent[];
  agentLocations: AgentLocation[];
  breakoutRooms: BreakoutRoom[];
  generatedAt: string;
}

export interface PlanContent {
  content: string;
}

export interface ProjectScanResult {
  path: string;
  projectName?: string | null;
  techStack: string[];
  hasSpecs: boolean;
  hasReadme: boolean;
  isGitRepo: boolean;
  gitBranch?: string | null;
  detectedFiles: string[];
}

export interface WorkspaceMeta {
  path: string;
  projectName?: string | null;
  lastAccessedAt?: string | null;
}

export interface OnboardResult {
  scan: ProjectScanResult;
  workspace: WorkspaceMeta;
  specTaskCreated?: boolean;
  roomId?: string | null;
}

export interface HealthResult {
  status: string;
  uptime: string;
  timestamp: string;
}

export interface TaskAssignmentRequest {
  title: string;
  description: string;
  successCriteria: string;
  roomId?: string | null;
  preferredRoles: string[];
  correlationId?: string | null;
}

export interface TaskAssignmentResult {
  correlationId: string;
  room: RoomSnapshot;
  task: TaskSnapshot;
  activity: ActivityEvent;
}

export interface DirectoryEntry {
  name: string;
  path: string;
  isDirectory: boolean;
}

export interface BrowseResult {
  current: string;
  parent: string | null;
  entries: DirectoryEntry[];
}

export interface ApiError {
  error: string;
}

// ── Helpers ────────────────────────────────────────────────────────────

const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL as string | undefined)?.replace(/\/$/, "") ?? "";

function apiUrl(path: string) {
  return `${apiBaseUrl}${path}`;
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, {
    headers: { "Content-Type": "application/json", ...init?.headers },
    ...init,
  });

  if (!res.ok) {
    const body = (await res.json().catch(() => null)) as ApiError | null;
    throw new Error(body?.error ?? `Request failed: ${res.status}`);
  }

  return (await res.json()) as T;
}

// ── System / Overview ──────────────────────────────────────────────────

export function getOverview(): Promise<WorkspaceOverview> {
  return request<WorkspaceOverview>(apiUrl("/api/overview"));
}

export function getHealth(): Promise<HealthResult> {
  return request<HealthResult>(apiUrl("/healthz"));
}

export function getConfiguredAgents(): Promise<AgentDefinition[]> {
  return request<AgentDefinition[]>(apiUrl("/api/agents/configured"));
}

// ── Activity ───────────────────────────────────────────────────────────

export function getRecentActivity(): Promise<ActivityEvent[]> {
  return request<ActivityEvent[]>(apiUrl("/api/activity/recent"));
}

// ── Rooms ──────────────────────────────────────────────────────────────

export function getRooms(): Promise<RoomSnapshot[]> {
  return request<RoomSnapshot[]>(apiUrl("/api/rooms"));
}

export function getRoom(roomId: string): Promise<RoomSnapshot> {
  return request<RoomSnapshot>(apiUrl(`/api/rooms/${roomId}`));
}

// ── Collaboration ──────────────────────────────────────────────────────

export function submitTask(req: TaskAssignmentRequest): Promise<TaskAssignmentResult> {
  return request<TaskAssignmentResult>(apiUrl("/api/tasks"), {
    method: "POST",
    body: JSON.stringify(req),
  });
}

export function getTasks(): Promise<TaskSnapshot[]> {
  return request<TaskSnapshot[]>(apiUrl("/api/tasks"));
}

export function getTask(taskId: string): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${taskId}`));
}

export function assignTask(taskId: string, agentId: string, agentName: string): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${taskId}/assign`), {
    method: "PUT",
    body: JSON.stringify({ agentId, agentName }),
  });
}

export function updateTaskStatus(taskId: string, status: TaskStatus): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${taskId}/status`), {
    method: "PUT",
    body: JSON.stringify({ status }),
  });
}

export function updateTaskBranch(taskId: string, branchName: string): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${taskId}/branch`), {
    method: "PUT",
    body: JSON.stringify({ branchName }),
  });
}

export function updateTaskPr(
  taskId: string, url: string, number: number, status: PullRequestStatus,
): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${taskId}/pr`), {
    method: "PUT",
    body: JSON.stringify({ url, number, status }),
  });
}

export function completeTask(
  taskId: string, commitCount: number, testsCreated?: string[],
): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${taskId}/complete`), {
    method: "PUT",
    body: JSON.stringify({ commitCount, testsCreated }),
  });
}

export function sendHumanMessage(roomId: string, content: string): Promise<ChatEnvelope> {
  return request<ChatEnvelope>(apiUrl(`/api/rooms/${roomId}/human`), {
    method: "POST",
    body: JSON.stringify({ content }),
  });
}

export function transitionPhase(
  roomId: string,
  targetPhase: CollaborationPhase,
  reason?: string,
): Promise<RoomSnapshot> {
  return request<RoomSnapshot>(apiUrl(`/api/rooms/${roomId}/phase`), {
    method: "POST",
    body: JSON.stringify({ roomId, targetPhase, reason: reason ?? "" }),
  });
}

// ── Plan ───────────────────────────────────────────────────────────────

export function getPlan(roomId: string): Promise<PlanContent | null> {
  return fetch(apiUrl(`/api/rooms/${roomId}/plan`))
    .then(async (res) => {
      if (res.status === 404) return null;
      if (!res.ok) throw new Error("Failed to load plan");
      return (await res.json()) as PlanContent;
    });
}

export function setPlan(roomId: string, content: string): Promise<void> {
  return request(apiUrl(`/api/rooms/${roomId}/plan`), {
    method: "PUT",
    body: JSON.stringify({ content }),
  });
}

export function deletePlan(roomId: string): Promise<void> {
  return request(apiUrl(`/api/rooms/${roomId}/plan`), {
    method: "DELETE",
  });
}

// ── Workspace / Project ────────────────────────────────────────────────

export function getActiveWorkspace(): Promise<{ active: WorkspaceMeta | null; dataDir: string | null }> {
  return fetch(apiUrl("/api/workspace"))
    .then(async (res) => {
      if (!res.ok) return { active: null, dataDir: null };
      return (await res.json()) as { active: WorkspaceMeta | null; dataDir: string | null };
    });
}

export function listWorkspaces(): Promise<WorkspaceMeta[]> {
  return fetch(apiUrl("/api/workspaces"))
    .then(async (res) => {
      if (!res.ok) return [];
      return (await res.json()) as WorkspaceMeta[];
    });
}

export function switchWorkspace(wsPath: string): Promise<WorkspaceMeta> {
  return request<WorkspaceMeta>(apiUrl("/api/workspace"), {
    method: "PUT",
    body: JSON.stringify({ path: wsPath }),
  });
}

export function scanProject(dirPath: string): Promise<ProjectScanResult> {
  return request<ProjectScanResult>(apiUrl("/api/workspaces/scan"), {
    method: "POST",
    body: JSON.stringify({ path: dirPath }),
  });
}

export function onboardProject(dirPath: string): Promise<OnboardResult> {
  return request<OnboardResult>(apiUrl("/api/workspaces/onboard"), {
    method: "POST",
    body: JSON.stringify({ path: dirPath }),
  });
}

export function browseDirectory(dirPath?: string): Promise<BrowseResult> {
  const params = new URLSearchParams();
  if (dirPath) params.set("path", dirPath);
  const qs = params.toString();
  return request<BrowseResult>(apiUrl(`/api/filesystem/browse${qs ? `?${qs}` : ""}`));
}

// ── Notification provider endpoints ────────────────────────────────────

export interface ProviderStatus {
  providerId: string;
  displayName: string;
  isConfigured: boolean;
  isConnected: boolean;
}

export interface ConfigField {
  key: string;
  label: string;
  type: string;
  required: boolean;
  description?: string;
  placeholder?: string;
}

export interface ProviderConfigSchema {
  providerId: string;
  displayName: string;
  description: string;
  fields: ConfigField[];
}

export interface ConfigureResponse {
  status: string;
  providerId: string;
}

export interface ConnectResponse {
  status: string;
  providerId: string;
}

export interface TestNotificationResponse {
  sent: number;
  totalConnected: number;
}

const NOTIF_BASE = "/api/notifications";

export function getNotificationProviders(): Promise<ProviderStatus[]> {
  return request<ProviderStatus[]>(apiUrl(`${NOTIF_BASE}/providers`));
}

export function getProviderSchema(id: string): Promise<ProviderConfigSchema> {
  return request<ProviderConfigSchema>(apiUrl(`${NOTIF_BASE}/providers/${encodeURIComponent(id)}/schema`));
}

export function configureProvider(
  id: string,
  settings: Record<string, string>,
): Promise<ConfigureResponse> {
  return request<ConfigureResponse>(
    apiUrl(`${NOTIF_BASE}/providers/${encodeURIComponent(id)}/configure`),
    { method: "POST", body: JSON.stringify(settings) },
  );
}

export function connectProvider(id: string): Promise<ConnectResponse> {
  return request<ConnectResponse>(
    apiUrl(`${NOTIF_BASE}/providers/${encodeURIComponent(id)}/connect`),
    { method: "POST" },
  );
}

export function disconnectProvider(id: string): Promise<ConnectResponse> {
  return request<ConnectResponse>(
    apiUrl(`${NOTIF_BASE}/providers/${encodeURIComponent(id)}/disconnect`),
    { method: "POST" },
  );
}

export function testNotification(): Promise<TestNotificationResponse> {
  return request<TestNotificationResponse>(apiUrl(`${NOTIF_BASE}/test`), { method: "POST" });
}

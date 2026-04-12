/**
 * Client-side API functions for Agent Academy.
 *
 * Covers auth, workspace, collaboration, activity, plan, filesystem, and notification endpoints.
 */

// ── Auth types ─────────────────────────────────────────────────────────

export interface AuthUser {
  login: string;
  name?: string | null;
  avatarUrl?: string | null;
}

export type CopilotStatus = "operational" | "degraded" | "unavailable";

export interface AuthStatus {
  authEnabled: boolean;
  authenticated: boolean;
  copilotStatus: CopilotStatus;
  user?: AuthUser | null;
}

export type HumanCommandName =
  | "READ_FILE"
  | "SEARCH_CODE"
  | "LIST_ROOMS"
  | "LIST_AGENTS"
  | "LIST_TASKS"
  | "SHOW_DIFF"
  | "GIT_LOG"
  | "SHOW_REVIEW_QUEUE"
  | "ROOM_HISTORY"
  | "RUN_BUILD"
  | "RUN_TESTS";

export type CommandExecutionStatus = "pending" | "completed" | "failed" | "denied";
export type CommandArgScalar = string | number | boolean | null;

export interface ExecuteCommandRequest {
  command: string;
  args?: Record<string, CommandArgScalar>;
}

export interface CommandExecutionResponse {
  command: string;
  status: CommandExecutionStatus;
  result: unknown;
  error: string | null;
  errorCode: string | null;
  correlationId: string;
  timestamp: string;
  executedBy: string;
}

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
  | "AgentPlanChanged" | "AgentSnapshotRewound" | "ToolIntercepted"
  | "DirectMessageSent" | "TaskPrStatusChanged"
  | "SprintStarted" | "SprintStageAdvanced" | "SprintArtifactStored" | "SprintCompleted" | "SprintCancelled";

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
  metadata?: Record<string, unknown> | null;
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
  mergeCommitSha?: string | null;
  commentCount?: number;
  type?: "Feature" | "Bug" | "Chore" | "Spike";
  sprintId?: string | null;
}

export type TaskCommentType = "Comment" | "Finding" | "Evidence" | "Blocker";

export interface TaskComment {
  id: string;
  taskId: string;
  agentId: string;
  agentName: string;
  commentType: TaskCommentType;
  content: string;
  createdAt: string;
}

export type SpecLinkType = "Implements" | "Modifies" | "Fixes" | "References";

export interface SpecTaskLink {
  id: string;
  taskId: string;
  specSectionId: string;
  linkType: SpecLinkType;
  linkedByAgentId: string;
  linkedByAgentName: string;
  note?: string | null;
  createdAt: string;
}

export type EvidencePhase = "Baseline" | "After" | "Review";

// Matches the row shape returned by QUERY_EVIDENCE command result
export interface EvidenceRow {
  id: string;
  phase: string;
  checkName: string;
  tool: string;
  command?: string | null;
  exitCode?: number | null;
  output?: string | null;
  passed: boolean;
  agentName: string;
  createdAt: string;
}

// Matches the full QUERY_EVIDENCE command result
export interface EvidenceQueryResult {
  taskId: string;
  phase: string;
  total: number;
  passed: number;
  failed: number;
  evidence: EvidenceRow[];
}

// Matches the evidence summary shape in CHECK_GATES result
export interface GateEvidenceSummary {
  phase: string;
  checkName: string;
  passed: boolean;
  agentName: string;
}

// Matches the full CHECK_GATES command result
export interface GateCheckResult {
  taskId: string;
  currentPhase: string;
  targetPhase: string;
  met: boolean;
  requiredChecks: number;
  passedChecks: number;
  missingChecks: string[];
  evidence: GateEvidenceSummary[];
  message: string;
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
  topic?: string | null;
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

export interface InstanceHealthResult {
  instanceId: string;
  startedAt: string;
  version: string;
  crashDetected: boolean;
  executorOperational: boolean;
  authFailed: boolean;
  circuitBreakerState?: string;
}

export interface ServerInstanceDto {
  id: string;
  startedAt: string;
  shutdownAt: string | null;
  exitCode: number | null;
  crashDetected: boolean;
  version: string;
  shutdownReason: string;
}

export interface RestartHistoryResponse {
  instances: ServerInstanceDto[];
  total: number;
  limit: number;
  offset: number;
}

export interface RestartStatsDto {
  totalInstances: number;
  crashRestarts: number;
  intentionalRestarts: number;
  cleanShutdowns: number;
  stillRunning: number;
  windowHours: number;
  maxRestartsPerWindow: number;
  restartWindowHours: number;
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

export const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL as string | undefined)?.replace(/\/$/, "") ?? "";

function apiUrl(path: string) {
  return `${apiBaseUrl}${path}`;
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, {
    credentials: "include",
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

export function getInstanceHealth(): Promise<InstanceHealthResult> {
  return request<InstanceHealthResult>(apiUrl("/api/health/instance"));
}

// ── System / Restart History ───────────────────────────────────────────

export function getRestartHistory(limit = 20, offset = 0): Promise<RestartHistoryResponse> {
  return request<RestartHistoryResponse>(apiUrl(`/api/system/restarts?limit=${limit}&offset=${offset}`));
}

export function getRestartStats(hours = 24): Promise<RestartStatsDto> {
  return request<RestartStatsDto>(apiUrl(`/api/system/restarts/stats?hours=${hours}`));
}

// ── Usage / LLM Tracking ──────────────────────────────────────────────

export interface UsageSummary {
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCost: number;
  requestCount: number;
  models: string[];
}

export interface AgentUsageSummary {
  agentId: string;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCost: number;
  requestCount: number;
}

export interface LlmUsageRecord {
  id: string;
  agentId: string;
  roomId: string | null;
  model: string | null;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  cost: number | null;
  durationMs: number | null;
  reasoningEffort: string | null;
  recordedAt: string;
}

export function getGlobalUsage(hoursBack?: number): Promise<UsageSummary> {
  const qs = hoursBack != null ? `?hoursBack=${hoursBack}` : "";
  return request<UsageSummary>(apiUrl(`/api/usage${qs}`));
}

export function getGlobalUsageRecords(agentId?: string, limit = 50): Promise<LlmUsageRecord[]> {
  const params = new URLSearchParams();
  if (agentId) params.set("agentId", agentId);
  if (limit !== 50) params.set("limit", String(limit));
  const qs = params.toString();
  return request<LlmUsageRecord[]>(apiUrl(`/api/usage/records${qs ? `?${qs}` : ""}`));
}

export function getRoomUsage(roomId: string): Promise<UsageSummary> {
  return request<UsageSummary>(apiUrl(`/api/rooms/${roomId}/usage`));
}

export function getRoomUsageByAgent(roomId: string): Promise<AgentUsageSummary[]> {
  return request<AgentUsageSummary[]>(apiUrl(`/api/rooms/${roomId}/usage/agents`));
}

// ── Errors / Agent Error Tracking ─────────────────────────────────────

export interface ErrorRecord {
  agentId: string;
  roomId: string;
  errorType: string;
  message: string;
  recoverable: boolean;
  timestamp: string;
}

export interface ErrorCountByType {
  errorType: string;
  count: number;
}

export interface ErrorCountByAgent {
  agentId: string;
  count: number;
}

export interface ErrorSummary {
  totalErrors: number;
  recoverableErrors: number;
  unrecoverableErrors: number;
  byType: ErrorCountByType[];
  byAgent: ErrorCountByAgent[];
}

export function getGlobalErrorSummary(hoursBack?: number): Promise<ErrorSummary> {
  const qs = hoursBack != null ? `?hoursBack=${hoursBack}` : "";
  return request<ErrorSummary>(apiUrl(`/api/errors${qs}`));
}

export function getGlobalErrorRecords(agentId?: string, hoursBack?: number, limit = 50): Promise<ErrorRecord[]> {
  const params = new URLSearchParams();
  if (agentId) params.set("agentId", agentId);
  if (hoursBack != null) params.set("hoursBack", String(hoursBack));
  if (limit !== 50) params.set("limit", String(limit));
  const qs = params.toString();
  return request<ErrorRecord[]>(apiUrl(`/api/errors/records${qs ? `?${qs}` : ""}`));
}

export function getRoomErrors(roomId: string, limit = 50): Promise<ErrorRecord[]> {
  return request<ErrorRecord[]>(apiUrl(`/api/rooms/${roomId}/errors?limit=${limit}`));
}

// ── Auth ───────────────────────────────────────────────────────────────

export function getAuthStatus(): Promise<AuthStatus> {
  return request<AuthStatus>(apiUrl("/api/auth/status"));
}

export function logout(): Promise<void> {
  return request<void>(apiUrl("/api/auth/logout"), { method: "POST" });
}

// ── GitHub ─────────────────────────────────────────────────────────────

export type GitHubAuthSource = "oauth" | "cli" | "none";

export interface GitHubStatus {
  isConfigured: boolean;
  repository: string | null;
  authSource: GitHubAuthSource;
}

export function getGitHubStatus(): Promise<GitHubStatus> {
  return request<GitHubStatus>(apiUrl("/api/github/status"));
}

export function getConfiguredAgents(): Promise<AgentDefinition[]> {
  return request<AgentDefinition[]>(apiUrl("/api/agents/configured"));
}

export function createCustomAgent(req: {
  name: string;
  prompt: string;
  model?: string;
}): Promise<AgentDefinition> {
  return request<AgentDefinition>(apiUrl("/api/agents/custom"), {
    method: "POST",
    body: JSON.stringify(req),
  });
}

export function deleteCustomAgent(agentId: string): Promise<{ status: string; agentId: string }> {
  return request<{ status: string; agentId: string }>(
    apiUrl(`/api/agents/custom/${encodeURIComponent(agentId)}`),
    { method: "DELETE" },
  );
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

export function createRoom(name: string, description?: string): Promise<RoomSnapshot> {
  return request<RoomSnapshot>(apiUrl("/api/rooms"), {
    method: "POST",
    body: JSON.stringify({ name, description }),
  });
}

export function createRoomSession(roomId: string): Promise<ConversationSessionSnapshot> {
  return request<ConversationSessionSnapshot>(apiUrl(`/api/rooms/${roomId}/sessions`), {
    method: "POST",
  });
}

export function getRoomMessages(
  roomId: string,
  opts?: { after?: string; limit?: number; sessionId?: string },
): Promise<RoomMessagesResponse> {
  const params = new URLSearchParams();
  if (opts?.after) params.set("after", opts.after);
  if (opts?.limit) params.set("limit", String(opts.limit));
  if (opts?.sessionId) params.set("sessionId", opts.sessionId);
  const qs = params.toString();
  return request<RoomMessagesResponse>(apiUrl(`/api/rooms/${roomId}/messages${qs ? `?${qs}` : ""}`));
}

export function addAgentToRoom(roomId: string, agentId: string): Promise<AgentLocation> {
  return request<AgentLocation>(apiUrl(`/api/rooms/${roomId}/agents/${agentId}`), {
    method: "POST",
  });
}

export function removeAgentFromRoom(roomId: string, agentId: string): Promise<AgentLocation> {
  return request<AgentLocation>(apiUrl(`/api/rooms/${roomId}/agents/${agentId}`), {
    method: "DELETE",
  });
}

// ── Collaboration ──────────────────────────────────────────────────────

export function submitTask(req: TaskAssignmentRequest): Promise<TaskAssignmentResult> {
  return request<TaskAssignmentResult>(apiUrl("/api/tasks"), {
    method: "POST",
    body: JSON.stringify(req),
  });
}

export function getTasks(sprintId?: string): Promise<TaskSnapshot[]> {
  const params = sprintId ? `?sprintId=${encodeURIComponent(sprintId)}` : "";
  return request<TaskSnapshot[]>(apiUrl(`/api/tasks${params}`));
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

export function getTaskComments(taskId: string): Promise<TaskComment[]> {
  return request<TaskComment[]>(apiUrl(`/api/tasks/${taskId}/comments`));
}

export function getTaskSpecLinks(taskId: string): Promise<SpecTaskLink[]> {
  return request<SpecTaskLink[]>(apiUrl(`/api/tasks/${taskId}/specs`));
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

export function executeCommand(req: ExecuteCommandRequest): Promise<CommandExecutionResponse> {
  return request<CommandExecutionResponse>(apiUrl("/api/commands/execute"), {
    method: "POST",
    body: JSON.stringify(req),
  });
}

export function getCommandExecution(correlationId: string): Promise<CommandExecutionResponse> {
  return request<CommandExecutionResponse>(apiUrl(`/api/commands/${correlationId}`));
}

export interface CommandFieldMetadata {
  name: string;
  label: string;
  kind: "text" | "textarea" | "number";
  description: string;
  placeholder?: string;
  required?: boolean;
  defaultValue?: string;
}

export interface CommandMetadata {
  command: string;
  title: string;
  category: "workspace" | "code" | "git" | "operations";
  description: string;
  detail: string;
  isAsync: boolean;
  fields: CommandFieldMetadata[];
  isDestructive?: boolean;
  destructiveWarning?: string;
}

export function getCommandMetadata(): Promise<CommandMetadata[]> {
  return request<CommandMetadata[]>(apiUrl("/api/commands/metadata"));
}

// ── Command Audit Log ──────────────────────────────────────────────────

export interface AuditLogEntry {
  id: string;
  correlationId: string;
  agentId: string;
  source: string | null;
  command: string;
  status: string;
  errorMessage: string | null;
  errorCode: string | null;
  roomId: string | null;
  timestamp: string;
}

export interface AuditLogResponse {
  records: AuditLogEntry[];
  total: number;
  limit: number;
  offset: number;
}

export interface AuditStatsResponse {
  totalCommands: number;
  byStatus: Record<string, number>;
  byAgent: Record<string, number>;
  byCommand: Record<string, number>;
  windowHours: number | null;
}

export function getAuditLog(opts: {
  agentId?: string;
  command?: string;
  status?: string;
  hoursBack?: number;
  limit?: number;
  offset?: number;
} = {}): Promise<AuditLogResponse> {
  const params = new URLSearchParams();
  if (opts.agentId) params.set("agentId", opts.agentId);
  if (opts.command) params.set("command", opts.command);
  if (opts.status) params.set("status", opts.status);
  if (opts.hoursBack != null) params.set("hoursBack", String(opts.hoursBack));
  if (opts.limit != null) params.set("limit", String(opts.limit));
  if (opts.offset != null) params.set("offset", String(opts.offset));
  const qs = params.toString();
  return request<AuditLogResponse>(apiUrl(`/api/commands/audit${qs ? `?${qs}` : ""}`));
}

export function getAuditStats(hoursBack?: number): Promise<AuditStatsResponse> {
  const qs = hoursBack != null ? `?hoursBack=${hoursBack}` : "";
  return request<AuditStatsResponse>(apiUrl(`/api/commands/audit/stats${qs}`));
}

export function renameRoom(roomId: string, name: string): Promise<RoomSnapshot> {
  return request<RoomSnapshot>(apiUrl(`/api/rooms/${roomId}/name`), {
    method: "PUT",
    body: JSON.stringify({ name }),
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
  lastError: string | null;
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

// ── Agent quota types ──────────────────────────────────────────────────

export interface ResourceQuota {
  maxRequestsPerHour?: number | null;
  maxTokensPerHour?: number | null;
  maxCostPerHour?: number | null;
}

export interface AgentUsageWindow {
  requestCount: number;
  totalTokens: number;
  totalCost: number;
}

export interface QuotaStatus {
  agentId: string;
  isAllowed: boolean;
  deniedReason?: string | null;
  retryAfterSeconds?: number | null;
  configuredQuota?: ResourceQuota | null;
  currentUsage?: AgentUsageWindow | null;
}

export interface UpdateQuotaRequest {
  maxRequestsPerHour?: number | null;
  maxTokensPerHour?: number | null;
  maxCostPerHour?: number | null;
}

// ── Agent config types ─────────────────────────────────────────────────

export interface AgentConfigOverride {
  startupPromptOverride?: string | null;
  modelOverride?: string | null;
  customInstructions?: string | null;
  instructionTemplateId?: string | null;
  instructionTemplateName?: string | null;
  updatedAt: string;
}

export interface AgentConfigResponse {
  agentId: string;
  effectiveModel: string;
  effectiveStartupPrompt: string;
  hasOverride: boolean;
  override?: AgentConfigOverride | null;
}

export interface UpsertAgentConfigRequest {
  startupPromptOverride?: string | null;
  modelOverride?: string | null;
  customInstructions?: string | null;
  instructionTemplateId?: string | null;
}

// ── Instruction template types ─────────────────────────────────────────

export interface InstructionTemplate {
  id: string;
  name: string;
  description?: string | null;
  content: string;
  createdAt: string;
  updatedAt: string;
}

export interface InstructionTemplateRequest {
  name: string;
  description?: string | null;
  content: string;
}

// ── Agent config endpoints ─────────────────────────────────────────────

export function getAgentConfig(agentId: string): Promise<AgentConfigResponse> {
  return request<AgentConfigResponse>(apiUrl(`/api/agents/${encodeURIComponent(agentId)}/config`));
}

export function upsertAgentConfig(
  agentId: string,
  req: UpsertAgentConfigRequest,
): Promise<AgentConfigResponse> {
  return request<AgentConfigResponse>(
    apiUrl(`/api/agents/${encodeURIComponent(agentId)}/config`),
    { method: "PUT", body: JSON.stringify(req) },
  );
}

export function resetAgentConfig(agentId: string): Promise<AgentConfigResponse> {
  return request<AgentConfigResponse>(
    apiUrl(`/api/agents/${encodeURIComponent(agentId)}/config/reset`),
    { method: "POST" },
  );
}

export function getAgentSessions(agentId: string): Promise<BreakoutRoom[]> {
  return request<BreakoutRoom[]>(
    apiUrl(`/api/agents/${encodeURIComponent(agentId)}/sessions`),
  );
}

// ── Agent quota endpoints ──────────────────────────────────────────────

export function getAgentQuota(agentId: string): Promise<QuotaStatus> {
  return request<QuotaStatus>(apiUrl(`/api/agents/${encodeURIComponent(agentId)}/quota`));
}

export function updateAgentQuota(
  agentId: string,
  req: UpdateQuotaRequest,
): Promise<QuotaStatus> {
  return request<QuotaStatus>(
    apiUrl(`/api/agents/${encodeURIComponent(agentId)}/quota`),
    { method: "PUT", body: JSON.stringify(req) },
  );
}

export function removeAgentQuota(agentId: string): Promise<{ status: string; agentId: string }> {
  return request<{ status: string; agentId: string }>(
    apiUrl(`/api/agents/${encodeURIComponent(agentId)}/quota`),
    { method: "DELETE" },
  );
}

// ── Instruction template endpoints ─────────────────────────────────────

export function getInstructionTemplates(): Promise<InstructionTemplate[]> {
  return request<InstructionTemplate[]>(apiUrl("/api/instruction-templates"));
}

export function getInstructionTemplate(id: string): Promise<InstructionTemplate> {
  return request<InstructionTemplate>(apiUrl(`/api/instruction-templates/${encodeURIComponent(id)}`));
}

export function createInstructionTemplate(
  req: InstructionTemplateRequest,
): Promise<InstructionTemplate> {
  return request<InstructionTemplate>(apiUrl("/api/instruction-templates"), {
    method: "POST",
    body: JSON.stringify(req),
  });
}

export function updateInstructionTemplate(
  id: string,
  req: InstructionTemplateRequest,
): Promise<InstructionTemplate> {
  return request<InstructionTemplate>(
    apiUrl(`/api/instruction-templates/${encodeURIComponent(id)}`),
    { method: "PUT", body: JSON.stringify(req) },
  );
}

export function deleteInstructionTemplate(id: string): Promise<{ status: string; id: string }> {
  return request<{ status: string; id: string }>(
    apiUrl(`/api/instruction-templates/${encodeURIComponent(id)}`),
    { method: "DELETE" },
  );
}

// ── Direct Messaging types ─────────────────────────────────────────────

export interface DmThreadSummary {
  agentId: string;
  agentName: string;
  agentRole: string;
  lastMessage: string;
  lastMessageAt: string;
  messageCount: number;
}

export interface DmMessage {
  id: string;
  senderId: string;
  senderName: string;
  senderRole?: string | null;
  content: string;
  sentAt: string;
  isFromHuman: boolean;
}

// ── Direct Messaging API ───────────────────────────────────────────────

export function getDmThreads(): Promise<DmThreadSummary[]> {
  return request<DmThreadSummary[]>(apiUrl("/api/dm/threads"));
}

export function getDmThreadMessages(agentId: string): Promise<DmMessage[]> {
  return request<DmMessage[]>(
    apiUrl(`/api/dm/threads/${encodeURIComponent(agentId)}`),
  );
}

export function sendDmToAgent(
  agentId: string,
  message: string,
): Promise<DmMessage> {
  return request<DmMessage>(
    apiUrl(`/api/dm/threads/${encodeURIComponent(agentId)}`),
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ message }),
    },
  );
}

// ── System Settings ────────────────────────────────────────────────────

export type SystemSettings = Record<string, string>;

export async function getSystemSettings(): Promise<SystemSettings> {
  return request<SystemSettings>(apiUrl("/api/settings"));
}

export async function updateSystemSettings(
  settings: SystemSettings,
): Promise<SystemSettings> {
  return request<SystemSettings>(apiUrl("/api/settings"), {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(settings),
  });
}

// ── Session History ────────────────────────────────────────────────────

export interface ConversationSessionSnapshot {
  id: string;
  roomId: string;
  roomType: string;
  sequenceNumber: number;
  status: string;
  summary: string | null;
  messageCount: number;
  createdAt: string;
  archivedAt: string | null;
}

export interface SessionListResponse {
  sessions: ConversationSessionSnapshot[];
  totalCount: number;
}

export interface RoomMessagesResponse {
  messages: ChatEnvelope[];
  hasMore: boolean;
}

export interface SessionStats {
  totalSessions: number;
  activeSessions: number;
  archivedSessions: number;
  totalMessages: number;
}

export function getSessions(
  status?: string,
  limit = 20,
  offset = 0,
  hoursBack?: number,
): Promise<SessionListResponse> {
  const params = new URLSearchParams();
  if (status) params.set("status", status);
  if (limit !== 20) params.set("limit", String(limit));
  if (offset > 0) params.set("offset", String(offset));
  if (hoursBack != null) params.set("hoursBack", String(hoursBack));
  const qs = params.toString();
  return request<SessionListResponse>(
    apiUrl(`/api/sessions${qs ? `?${qs}` : ""}`),
  );
}

export function getSessionStats(hoursBack?: number): Promise<SessionStats> {
  const qs = hoursBack != null ? `?hoursBack=${hoursBack}` : "";
  return request<SessionStats>(apiUrl(`/api/sessions/stats${qs}`));
}

export function getRoomSessions(
  roomId: string,
  status?: string,
  limit = 20,
  offset = 0,
): Promise<SessionListResponse> {
  const params = new URLSearchParams();
  if (status) params.set("status", status);
  if (limit !== 20) params.set("limit", String(limit));
  if (offset > 0) params.set("offset", String(offset));
  const qs = params.toString();
  return request<SessionListResponse>(
    apiUrl(
      `/api/rooms/${encodeURIComponent(roomId)}/sessions${qs ? `?${qs}` : ""}`,
    ),
  );
}

// ── Sprints ─────────────────────────────────────────────────────

export type SprintStage =
  | "Intake"
  | "Planning"
  | "Discussion"
  | "Validation"
  | "Implementation"
  | "FinalSynthesis";

export type SprintStatus = "Active" | "Completed" | "Cancelled";

export type ArtifactType =
  | "RequirementsDocument"
  | "SprintPlan"
  | "ValidationReport"
  | "SprintReport"
  | "OverflowRequirements";

export interface SprintSnapshot {
  id: string;
  number: number;
  status: SprintStatus;
  currentStage: SprintStage;
  overflowFromSprintId: string | null;
  awaitingSignOff: boolean;
  pendingStage: SprintStage | null;
  signOffRequestedAt: string | null;
  createdAt: string;
  completedAt: string | null;
}

export interface SprintArtifact {
  id: number;
  sprintId: string;
  stage: SprintStage;
  type: ArtifactType;
  content: string;
  createdByAgentId: string | null;
  createdAt: string;
  updatedAt: string | null;
}

export interface SprintDetailResponse {
  sprint: SprintSnapshot;
  artifacts: SprintArtifact[];
  stages: string[];
}

export interface SprintListResponse {
  sprints: SprintSnapshot[];
  totalCount: number;
}

// ── Sprint real-time event types ──────────────────────────────

export type SprintEventAction =
  | "advanced"
  | "signoff_requested"
  | "approved"
  | "rejected";

export interface SprintRealtimeEvent {
  eventId: string;
  type: ActivityEventType;
  sprintId: string;
  metadata: Record<string, unknown>;
  receivedAt: number;
}

export async function getActiveSprint(): Promise<SprintDetailResponse | null> {
  const res = await fetch(apiUrl("/api/sprints/active"), { credentials: "include" });
  if (res.status === 204) return null;
  if (!res.ok) throw new Error("Failed to fetch active sprint");
  return res.json() as Promise<SprintDetailResponse>;
}

export function getSprints(limit = 20, offset = 0): Promise<SprintListResponse> {
  const params = new URLSearchParams();
  if (limit !== 20) params.set("limit", String(limit));
  if (offset > 0) params.set("offset", String(offset));
  const qs = params.toString();
  return request<SprintListResponse>(apiUrl(`/api/sprints${qs ? `?${qs}` : ""}`));
}

export async function getSprintDetail(id: string): Promise<SprintDetailResponse | null> {
  const res = await fetch(apiUrl(`/api/sprints/${encodeURIComponent(id)}`), {
    credentials: "include",
  });
  if (res.status === 404) return null;
  if (!res.ok) throw new Error("Failed to fetch sprint");
  return res.json() as Promise<SprintDetailResponse>;
}

export function getSprintArtifacts(
  id: string,
  stage?: SprintStage,
): Promise<SprintArtifact[]> {
  const params = new URLSearchParams();
  if (stage) params.set("stage", stage);
  const qs = params.toString();
  return request<SprintArtifact[]>(
    apiUrl(`/api/sprints/${encodeURIComponent(id)}/artifacts${qs ? `?${qs}` : ""}`),
  );
}

export async function startSprint(): Promise<SprintDetailResponse> {
  const res = await fetch(apiUrl("/api/sprints"), {
    method: "POST",
    credentials: "include",
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? "Failed to start sprint");
  }
  return res.json() as Promise<SprintDetailResponse>;
}

export async function advanceSprint(id: string): Promise<SprintDetailResponse> {
  const res = await fetch(apiUrl(`/api/sprints/${encodeURIComponent(id)}/advance`), {
    method: "POST",
    credentials: "include",
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? "Failed to advance sprint");
  }
  return res.json() as Promise<SprintDetailResponse>;
}

export async function completeSprint(id: string, force = false): Promise<SprintSnapshot> {
  const qs = force ? "?force=true" : "";
  const res = await fetch(apiUrl(`/api/sprints/${encodeURIComponent(id)}/complete${qs}`), {
    method: "POST",
    credentials: "include",
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? "Failed to complete sprint");
  }
  return res.json() as Promise<SprintSnapshot>;
}

export async function cancelSprint(id: string): Promise<SprintSnapshot> {
  const res = await fetch(apiUrl(`/api/sprints/${encodeURIComponent(id)}/cancel`), {
    method: "POST",
    credentials: "include",
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? "Failed to cancel sprint");
  }
  return res.json() as Promise<SprintSnapshot>;
}

export async function approveSprintAdvance(id: string): Promise<SprintDetailResponse> {
  const res = await fetch(apiUrl(`/api/sprints/${encodeURIComponent(id)}/approve-advance`), {
    method: "POST",
    credentials: "include",
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? "Failed to approve sprint advance");
  }
  return res.json() as Promise<SprintDetailResponse>;
}

export async function rejectSprintAdvance(id: string): Promise<SprintSnapshot> {
  const res = await fetch(apiUrl(`/api/sprints/${encodeURIComponent(id)}/reject-advance`), {
    method: "POST",
    credentials: "include",
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? "Failed to reject sprint advance");
  }
  return res.json() as Promise<SprintSnapshot>;
}

// ── Agent Analytics ───────────────────────────────────────────

export interface AgentPerformanceMetrics {
  agentId: string;
  agentName: string;
  totalRequests: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCost: number;
  averageResponseTimeMs: number | null;
  totalErrors: number;
  recoverableErrors: number;
  unrecoverableErrors: number;
  tasksAssigned: number;
  tasksCompleted: number;
  tokenTrend: number[];
}

export interface AgentAnalyticsSummary {
  agents: AgentPerformanceMetrics[];
  windowStart: string;
  windowEnd: string;
  totalRequests: number;
  totalCost: number;
  totalErrors: number;
}

export function getAgentAnalytics(hoursBack?: number): Promise<AgentAnalyticsSummary> {
  const qs = hoursBack != null ? `?hoursBack=${hoursBack}` : "";
  return request<AgentAnalyticsSummary>(apiUrl(`/api/analytics/agents${qs}`));
}

// ── Agent analytics drill-down ──

export interface AgentUsageRecord {
  id: string;
  roomId: string | null;
  model: string | null;
  inputTokens: number;
  outputTokens: number;
  cost: number | null;
  durationMs: number | null;
  reasoningEffort: string | null;
  recordedAt: string;
}

export interface AgentErrorRecord {
  id: string;
  roomId: string | null;
  errorType: string;
  message: string;
  recoverable: boolean;
  retried: boolean;
  occurredAt: string;
}

export interface AgentTaskRecord {
  id: string;
  title: string;
  status: string;
  roomId: string | null;
  branchName: string | null;
  pullRequestUrl: string | null;
  pullRequestNumber: number | null;
  createdAt: string;
  completedAt: string | null;
}

export interface AgentModelBreakdown {
  model: string;
  requests: number;
  totalTokens: number;
  totalCost: number;
}

export interface AgentActivityBucket {
  bucketStart: string;
  bucketEnd: string;
  requests: number;
  tokens: number;
}

export interface AgentAnalyticsDetail {
  agent: AgentPerformanceMetrics;
  windowStart: string;
  windowEnd: string;
  recentRequests: AgentUsageRecord[];
  recentErrors: AgentErrorRecord[];
  tasks: AgentTaskRecord[];
  modelBreakdown: AgentModelBreakdown[];
  activityBuckets: AgentActivityBucket[];
}

export function getAgentAnalyticsDetail(agentId: string, hoursBack?: number): Promise<AgentAnalyticsDetail> {
  const qs = hoursBack != null ? `?hoursBack=${hoursBack}` : "";
  return request<AgentAnalyticsDetail>(apiUrl(`/api/analytics/agents/${encodeURIComponent(agentId)}${qs}`));
}

// ── Export / Download ──────────────────────────────────────────────────

/**
 * Triggers a browser file download from a fetch response.
 * Handles Content-Disposition and falls back to the provided filename.
 */
async function downloadFile(url: string, fallbackFilename: string): Promise<void> {
  const res = await fetch(url, { credentials: "include" });
  if (!res.ok) {
    const body = (await res.json().catch(() => null)) as ApiError | null;
    throw new Error(body?.error ?? `Export failed: ${res.status}`);
  }

  const blob = await res.blob();
  const disposition = res.headers.get("content-disposition");
  let filename = fallbackFilename;
  if (disposition) {
    const match = /filename[^;=\n]*=["']?([^"';\n]*)/.exec(disposition);
    if (match?.[1]) filename = match[1];
  }

  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  URL.revokeObjectURL(a.href);
  a.remove();
}

export function exportAgentAnalytics(hoursBack?: number, format: "csv" | "json" = "csv"): Promise<void> {
  const params = new URLSearchParams({ format });
  if (hoursBack != null) params.set("hoursBack", String(hoursBack));
  return downloadFile(apiUrl(`/api/export/agents?${params}`), `agent-analytics.${format}`);
}

export function exportUsageRecords(
  options?: { hoursBack?: number; agentId?: string; limit?: number; format?: "csv" | "json" },
): Promise<void> {
  const format = options?.format ?? "csv";
  const params = new URLSearchParams({ format });
  if (options?.hoursBack != null) params.set("hoursBack", String(options.hoursBack));
  if (options?.agentId) params.set("agentId", options.agentId);
  if (options?.limit != null) params.set("limit", String(options.limit));
  return downloadFile(apiUrl(`/api/export/usage?${params}`), `usage-records.${format}`);
}

// ── Workspace Search ────────────────────────────────────────────────────

export type SearchScope = "all" | "messages" | "tasks";

export interface MessageSearchResult {
  messageId: string;
  roomId: string;
  roomName: string;
  senderName: string;
  senderKind: MessageSenderKind;
  senderRole: string | null;
  snippet: string;
  sentAt: string;
  sessionId: string | null;
  source: "room" | "breakout";
}

export interface TaskSearchResult {
  taskId: string;
  title: string;
  status: string;
  assignedAgentName: string | null;
  snippet: string;
  createdAt: string;
  roomId: string | null;
}

export interface SearchResults {
  messages: MessageSearchResult[];
  tasks: TaskSearchResult[];
  totalCount: number;
  query: string;
}

export function searchWorkspace(
  query: string,
  options?: { scope?: SearchScope; messageLimit?: number; taskLimit?: number },
): Promise<SearchResults> {
  const params = new URLSearchParams({ q: query });
  if (options?.scope) params.set("scope", options.scope);
  if (options?.messageLimit != null) params.set("messageLimit", String(options.messageLimit));
  if (options?.taskLimit != null) params.set("taskLimit", String(options.taskLimit));
  return request<SearchResults>(apiUrl(`/api/search?${params}`));
}

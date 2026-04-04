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
  | "DirectMessageSent";

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
  mergeCommitSha?: string | null;
  commentCount?: number;
  type?: "Feature" | "Bug" | "Chore" | "Spike";
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

export function getTaskComments(taskId: string): Promise<TaskComment[]> {
  return request<TaskComment[]>(apiUrl(`/api/tasks/${taskId}/comments`));
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
}

export function getCommandMetadata(): Promise<CommandMetadata[]> {
  return request<CommandMetadata[]>(apiUrl("/api/commands/metadata"));
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

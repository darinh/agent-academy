/**
 * All shared types, interfaces, and enums for the Agent Academy API.
 * Domain modules import types from here; everything is re-exported via the barrel.
 */

// ── Auth ───────────────────────────────────────────────────────────────

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

export type GitHubAuthSource = "oauth" | "cli" | "none";

export interface GitHubStatus {
  isConfigured: boolean;
  repository: string | null;
  authSource: GitHubAuthSource;
}

// ── Commands ───────────────────────────────────────────────────────────

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

// ── Domain Model ───────────────────────────────────────────────────────

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

// ── Activity ───────────────────────────────────────────────────────────

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
  | "SprintStarted" | "SprintStageAdvanced" | "SprintArtifactStored" | "SprintCompleted" | "SprintCancelled"
  | "TaskUnblocked"
  | "TaskRetrospectiveCompleted";

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

// ── Agents ─────────────────────────────────────────────────────────────

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

// ── Messages ───────────────────────────────────────────────────────────

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

// ── Tasks ──────────────────────────────────────────────────────────────

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
  dependsOnTaskIds?: string[] | null;
  blockingTaskIds?: string[] | null;
}

export type TaskCommentType = "Comment" | "Finding" | "Evidence" | "Blocker" | "Retrospective";

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

export interface TaskDependencySummary {
  taskId: string;
  title: string;
  status: TaskStatus;
  isSatisfied: boolean;
}

export interface TaskDependencyInfo {
  taskId: string;
  dependsOn: TaskDependencySummary[];
  dependedOnBy: TaskDependencySummary[];
}

// ── Bulk Operations ────────────────────────────────────────────────────

export interface BulkOperationResult {
  requested: number;
  succeeded: number;
  failed: number;
  updated: TaskSnapshot[];
  errors: BulkOperationError[];
}

export interface BulkOperationError {
  taskId: string;
  code: string;
  error: string;
}

export type EvidencePhase = "Baseline" | "After" | "Review";

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

export interface EvidenceQueryResult {
  taskId: string;
  phase: string;
  total: number;
  passed: number;
  failed: number;
  evidence: EvidenceRow[];
}

export interface GateEvidenceSummary {
  phase: string;
  checkName: string;
  passed: boolean;
  agentName: string;
}

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

// ── Rooms ──────────────────────────────────────────────────────────────

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

export interface RoomMessagesResponse {
  messages: ChatEnvelope[];
  hasMore: boolean;
}

// ── Sessions ───────────────────────────────────────────────────────────

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

export interface SessionStats {
  totalSessions: number;
  activeSessions: number;
  archivedSessions: number;
  totalMessages: number;
}

// ── Workspace / Project ────────────────────────────────────────────────

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

// ── System / Health ────────────────────────────────────────────────────

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

export type SystemSettings = Record<string, string>;

// ── Usage / Analytics ──────────────────────────────────────────────────

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

// ── Task Cycle Analytics ──────────────────────────────────────────────

export interface TaskCycleAnalytics {
  overview: TaskCycleOverview;
  agentEffectiveness: AgentTaskEffectiveness[];
  throughputBuckets: TaskCycleBucket[];
  typeBreakdown: TaskTypeBreakdown;
  windowStart: string;
  windowEnd: string;
}

export interface TaskCycleOverview {
  totalTasks: number;
  statusCounts: TaskStatusCounts;
  completionRate: number;
  avgCycleTimeHours: number | null;
  avgQueueTimeHours: number | null;
  avgExecutionSpanHours: number | null;
  avgReviewRounds: number | null;
  reworkRate: number;
  totalCommits: number;
}

export interface TaskStatusCounts {
  queued: number;
  active: number;
  blocked: number;
  awaitingValidation: number;
  inReview: number;
  changesRequested: number;
  approved: number;
  merging: number;
  completed: number;
  cancelled: number;
}

export interface AgentTaskEffectiveness {
  agentId: string;
  agentName: string;
  assigned: number;
  completed: number;
  cancelled: number;
  completionRate: number;
  avgCycleTimeHours: number | null;
  avgQueueTimeHours: number | null;
  avgExecutionSpanHours: number | null;
  avgReviewRounds: number | null;
  avgCommitsPerTask: number | null;
  firstPassApprovalRate: number;
  reworkRate: number;
}

export interface TaskCycleBucket {
  bucketStart: string;
  bucketEnd: string;
  completed: number;
  created: number;
}

export interface TaskTypeBreakdown {
  feature: number;
  bug: number;
  chore: number;
  spike: number;
}

// ── Notifications ──────────────────────────────────────────────────────

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

// ── Direct Messaging ───────────────────────────────────────────────────

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

// ── Sprints ────────────────────────────────────────────────────────────

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

// ── Sprint Schedule ────────────────────────────────────────────────────

export interface SprintScheduleRequest {
  cronExpression: string;
  timeZoneId: string;
  enabled: boolean;
}

export interface SprintScheduleResponse {
  id: string;
  workspacePath: string;
  cronExpression: string;
  timeZoneId: string;
  enabled: boolean;
  nextRunAtUtc: string | null;
  lastTriggeredAt: string | null;
  lastEvaluatedAt: string | null;
  lastOutcome: string | null;
  createdAt: string;
  updatedAt: string;
}

// ── Search ─────────────────────────────────────────────────────────────

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

// ── Agent Memory ────────────────────────────────────────────────

export interface MemoryDto {
  agentId: string;
  category: string;
  key: string;
  value: string;
  createdAt: string;
  updatedAt: string | null;
  lastAccessedAt: string | null;
  expiresAt: string | null;
}

export interface BrowseMemoriesResponse {
  total: number;
  memories: MemoryDto[];
}

export interface MemoryCategoryStat {
  category: string;
  total: number;
  active: number;
  expired: number;
  lastUpdated: string;
}

export interface MemoryStatsResponse {
  agentId: string;
  totalMemories: number;
  activeMemories: number;
  expiredMemories: number;
  categories: MemoryCategoryStat[];
}

// ── Worktrees ──────────────────────────────────────────────────────────

export interface WorktreeStatusSnapshot {
  branch: string;
  relativePath: string;
  createdAt: string;
  statusAvailable: boolean;
  error: string | null;
  totalDirtyFiles: number;
  dirtyFilesPreview: string[];
  filesChanged: number;
  insertions: number;
  deletions: number;
  lastCommitSha: string | null;
  lastCommitMessage: string | null;
  lastCommitAuthor: string | null;
  lastCommitDate: string | null;
  taskId: string | null;
  taskTitle: string | null;
  taskStatus: string | null;
  agentId: string | null;
  agentName: string | null;
}

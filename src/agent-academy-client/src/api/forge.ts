import { apiUrl, request } from "./core";

// --- Types ---

export type ForgeJobStatus = "queued" | "running" | "completed" | "failed";
export type ForgeRunOutcome = "Pending" | "Running" | "Succeeded" | "Failed" | "Aborted";
export type ForgePhaseStatus = "Pending" | "Running" | "Succeeded" | "Failed" | "Skipped";
export type ForgeAttemptStatus = "Pending" | "Prompting" | "Generating" | "Validating" | "Accepted" | "Rejected" | "Errored";

export interface ForgeStatus {
  enabled: boolean;
  executionAvailable: boolean;
  runsDirectory: string;
  activeJobs: number;
  totalJobs: number;
  completedJobs: number;
  failedJobs: number;
}

export interface ForgeJobSummary {
  jobId: string;
  runId?: string;
  status: ForgeJobStatus;
  error?: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  taskId: string;
  taskTitle: string;
}

export interface ForgeRunSummary {
  runId: string;
  taskId: string;
  methodologyVersion: string;
  outcome: string;
  startedAt: string;
  endedAt?: string;
  pipelineCost?: number;
  phaseCount: number;
  fidelityOutcome?: string;
}

export interface TokenCount {
  in: number;
  out: number;
}

export interface ForgeRunTrace {
  runId: string;
  taskId: string;
  methodologyVersion: string;
  startedAt: string;
  endedAt?: string;
  outcome: string;
  controlOutcome?: string;
  pipelineTokens: TokenCount;
  controlTokens: TokenCount;
  pipelineCost?: number;
  controlCost?: number;
  costRatio?: number;
  controlArtifactHash?: string;
  abortReason?: string;
  fidelityOutcome?: string;
  fidelityArtifactHash?: string;
  sourceIntentArtifactHash?: string;
  driftCodes?: string[];
  fidelityTokens?: TokenCount;
  fidelityCost?: number;
  finalArtifactHashes: Record<string, string>;
}

export interface ValidatorResult {
  phase: "structural" | "semantic" | "cross-artifact";
  code: string;
  severity: "error" | "warning" | "info";
  blocking: boolean;
  path?: string;
  evidence?: string;
  attemptNumber: number;
  advisoryReason?: string;
  blockingReason?: string;
}

export interface ForgeAttemptTrace {
  attemptNumber: number;
  status: string;
  artifactHash?: string;
  validatorResults: ValidatorResult[];
  tokens: TokenCount;
  latencyMs: number;
  model: string;
  judgeTokens?: TokenCount;
  judgeModel?: string;
  cost?: number;
  startedAt: string;
  endedAt?: string;
}

export interface StateTransition {
  from?: string;
  to: string;
  at: string;
}

export interface ForgePhaseRunTrace {
  phaseId: string;
  artifactType: string;
  stateTransitions: StateTransition[];
  attempts: ForgeAttemptTrace[];
  inputArtifactHashes: string[];
  outputArtifactHashes: string[];
}

export interface ForgeArtifactEnvelope {
  artifactType: string;
  schemaVersion: string;
  producedByPhase: string;
  payload: unknown;
}

export interface ForgeArtifactMeta {
  derivedFrom: string[];
  inputHashes: string[];
  producedAt: string;
  attemptNumber: number;
}

export interface ForgeArtifactResponse {
  artifact: ForgeArtifactEnvelope;
  meta: ForgeArtifactMeta;
}

export interface ForgeSchemaInfo {
  id: string;
  artifactType: string;
  schemaVersion: string;
  status: string;
  semanticRuleCount: number;
}

export interface StartForgeRunRequest {
  taskId?: string;
  title: string;
  description: string;
  methodology: MethodologyDefinition;
}

export interface StartForgeRunResponse {
  jobId: string;
  status: string;
  createdAt: string;
  taskId: string;
}

export interface MethodologyDefinition {
  id: string;
  description?: string;
  max_attempts_default?: number;
  model_defaults?: { generation?: string; judge?: string };
  budget?: number;
  control?: { target_schema: string; model?: string };
  phases: MethodologyPhase[];
  fidelity?: { target_phase: string; model?: string; judge_model?: string; max_attempts?: number };
}

export interface MethodologyPhase {
  id: string;
  goal: string;
  inputs: string[];
  output_schema: string;
  instructions: string;
  max_attempts?: number;
  model?: string;
  judge_model?: string;
}

// --- API functions ---

function toBoolean(value: unknown, fallback = false): boolean {
  return typeof value === "boolean" ? value : fallback;
}

function toNumber(value: unknown, fallback = 0): number {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}

function toString(value: unknown, fallback = ""): string {
  return typeof value === "string" ? value : fallback;
}

export function getForgeStatus(): Promise<ForgeStatus> {
  return request<Record<string, unknown>>(apiUrl("/api/forge/status")).then((raw) => {
    const enabled = toBoolean(raw.enabled ?? raw.Enabled);
    const executionAvailable = toBoolean(
      raw.executionAvailable ?? raw.executionEnabled ?? raw.ExecutionAvailable ?? raw.ExecutionEnabled,
      enabled,
    );

    return {
      enabled,
      executionAvailable,
      runsDirectory: toString(raw.runsDirectory ?? raw.RunsDirectory),
      activeJobs: toNumber(raw.activeJobs ?? raw.ActiveJobs),
      totalJobs: toNumber(raw.totalJobs ?? raw.TotalJobs),
      completedJobs: toNumber(raw.completedJobs ?? raw.CompletedJobs),
      failedJobs: toNumber(raw.failedJobs ?? raw.FailedJobs),
    };
  });
}

export function startForgeRun(req: StartForgeRunRequest): Promise<StartForgeRunResponse> {
  return request<StartForgeRunResponse>(apiUrl("/api/forge/jobs"), {
    method: "POST",
    body: JSON.stringify(req),
  });
}

export function listForgeJobs(): Promise<ForgeJobSummary[]> {
  return request<ForgeJobSummary[]>(apiUrl("/api/forge/jobs"));
}

export function getForgeJob(jobId: string): Promise<ForgeJobSummary> {
  return request<ForgeJobSummary>(apiUrl(`/api/forge/jobs/${encodeURIComponent(jobId)}`));
}

export function listForgeRuns(): Promise<ForgeRunSummary[]> {
  return request<ForgeRunSummary[]>(apiUrl("/api/forge/runs"));
}

export function getForgeRun(runId: string): Promise<ForgeRunTrace> {
  return request<ForgeRunTrace>(apiUrl(`/api/forge/runs/${encodeURIComponent(runId)}`));
}

export function getForgeRunPhases(runId: string): Promise<ForgePhaseRunTrace[]> {
  return request<ForgePhaseRunTrace[]>(apiUrl(`/api/forge/runs/${encodeURIComponent(runId)}/phases`));
}

export function getForgeArtifact(hash: string): Promise<ForgeArtifactResponse> {
  return request<ForgeArtifactResponse>(apiUrl(`/api/forge/artifacts/${encodeURIComponent(hash)}`));
}

export function listForgeSchemas(): Promise<ForgeSchemaInfo[]> {
  return request<ForgeSchemaInfo[]>(apiUrl("/api/forge/schemas"));
}

// --- Methodology catalog ---

export interface MethodologySummary {
  id: string;
  description?: string;
  phaseCount: number;
  generationModel?: string;
  judgeModel?: string;
  hasBudget: boolean;
  hasFidelity: boolean;
  hasControl: boolean;
}

export function listMethodologies(): Promise<MethodologySummary[]> {
  return request<MethodologySummary[]>(apiUrl("/api/forge/methodologies"));
}

export function getMethodology(methodologyId: string): Promise<MethodologyDefinition> {
  return request<MethodologyDefinition>(apiUrl(`/api/forge/methodologies/${encodeURIComponent(methodologyId)}`));
}

export function saveMethodology(methodologyId: string, methodology: MethodologyDefinition): Promise<{ id: string; message: string }> {
  return request<{ id: string; message: string }>(apiUrl(`/api/forge/methodologies/${encodeURIComponent(methodologyId)}`), {
    method: "PUT",
    body: JSON.stringify(methodology),
  });
}

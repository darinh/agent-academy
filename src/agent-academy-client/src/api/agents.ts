import type {
  AgentDefinition,
  AgentConfigResponse,
  UpsertAgentConfigRequest,
  BreakoutRoom,
  QuotaStatus,
  UpdateQuotaRequest,
  InstructionTemplate,
  InstructionTemplateRequest,
  AgentLocation,
  AgentKnowledgeResponse,
  AllKnowledgeResponse,
  RunAgentResponse,
} from "./types";
import { apiUrl, request } from "./core";

// ── Agent Catalog ──────────────────────────────────────────────────────

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

// ── Agent Config ───────────────────────────────────────────────────────

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

// ── Agent Quotas ───────────────────────────────────────────────────────

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

// ── Instruction Templates ──────────────────────────────────────────────

export function getInstructionTemplates(): Promise<InstructionTemplate[]> {
  return request<InstructionTemplate[]>(apiUrl("/api/instruction-templates"));
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

// ── Agent Locations ────────────────────────────────────────────────────

export function getAgentLocations(): Promise<AgentLocation[]> {
  return request<AgentLocation[]>(apiUrl("/api/agents/locations"));
}

// ── Agent Knowledge ────────────────────────────────────────────────────

export function getAgentKnowledge(agentId: string): Promise<AgentKnowledgeResponse> {
  return request<AgentKnowledgeResponse>(
    apiUrl(`/api/agents/${encodeURIComponent(agentId)}/knowledge`),
  );
}

export function addAgentKnowledge(
  agentId: string,
  entry: string,
): Promise<{ added: number }> {
  return request<{ added: number }>(
    apiUrl(`/api/agents/${encodeURIComponent(agentId)}/knowledge`),
    { method: "POST", body: JSON.stringify({ entry }) },
  );
}

export function getAllKnowledge(): Promise<AllKnowledgeResponse> {
  return request<AllKnowledgeResponse>(apiUrl("/api/knowledge"));
}

export function runAgent(agentId: string, prompt: string): Promise<RunAgentResponse> {
  return request<RunAgentResponse>(
    apiUrl(`/api/agents/${encodeURIComponent(agentId)}/run`),
    {
      method: "POST",
      headers: { "Content-Type": "text/plain" },
      body: prompt,
    },
  );
}

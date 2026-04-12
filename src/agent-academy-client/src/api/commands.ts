import type {
  CommandExecutionResponse,
  ExecuteCommandRequest,
  CommandMetadata,
  AuditLogResponse,
  AuditStatsResponse,
} from "./types";
import { apiUrl, request } from "./core";

export function executeCommand(req: ExecuteCommandRequest): Promise<CommandExecutionResponse> {
  return request<CommandExecutionResponse>(apiUrl("/api/commands/execute"), {
    method: "POST",
    body: JSON.stringify(req),
  });
}

export function getCommandExecution(correlationId: string): Promise<CommandExecutionResponse> {
  return request<CommandExecutionResponse>(apiUrl(`/api/commands/${correlationId}`));
}

export function getCommandMetadata(): Promise<CommandMetadata[]> {
  return request<CommandMetadata[]>(apiUrl("/api/commands/metadata"));
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

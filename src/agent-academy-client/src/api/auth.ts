import type { AuthStatus, GitHubStatus } from "./types";
import { apiUrl, request } from "./core";

export function getAuthStatus(): Promise<AuthStatus> {
  return request<AuthStatus>(apiUrl("/api/auth/status"));
}

export function logout(): Promise<void> {
  return request<void>(apiUrl("/api/auth/logout"), { method: "POST" });
}

export function getGitHubStatus(): Promise<GitHubStatus> {
  return request<GitHubStatus>(apiUrl("/api/github/status"));
}

/**
 * Client-side API for Agent Academy.
 *
 * This barrel re-exports all types and functions from domain-specific modules.
 * Consumer imports (`from "./api"`) continue to work without changes.
 */

export { apiBaseUrl, csrfHeaders, extractApiError } from "./core";
export type { ApiError, ProblemDetails } from "./core";
export * from "./types";
export * from "./auth";
export * from "./workspace";
export * from "./rooms";
export * from "./tasks";
export * from "./agents";
export * from "./commands";
export * from "./analytics";
export * from "./sprints";
export * from "./system";
export * from "./memories";
export * from "./digests";
export * from "./retrospectives";
export * from "./goalCards";
export * from "./forge";

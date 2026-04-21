/**
 * Shared HTTP helpers for all API modules.
 * Domain modules import these directly; only `apiBaseUrl` is re-exported from the barrel.
 */

/** RFC 7807 ProblemDetails shape returned by the backend. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
}

/** Legacy error shape (kept for backward compatibility during migration). */
export interface ApiError {
  error?: string;
}

/** Extract a human-readable message from any error response body. */
export function extractApiError(
  body: (ProblemDetails & ApiError) | null,
  fallback: string,
): string {
  if (!body) return fallback;
  return body.detail ?? body.error ?? body.title ?? fallback;
}

export const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL as string | undefined)?.replace(/\/$/, "") ?? "";

export function apiUrl(path: string) {
  return `${apiBaseUrl}${path}`;
}

/**
 * CSRF protection header. Sent on every SPA request so that the server-side
 * CsrfProtectionMiddleware accepts cookie-authenticated mutations. The value
 * itself is not validated — its presence is what forces CORS preflight and
 * thereby blocks cross-origin form POSTs. See spec 015 §2.5.
 */
export const csrfHeaders = { "X-Requested-With": "XMLHttpRequest" } as const;

export async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, {
    ...init,
    credentials: "include",
    // Header merge must come AFTER spreading `init` — otherwise `init.headers`
    // clobbers our csrfHeaders and callers that pass a custom Content-Type
    // silently lose CSRF protection (regression found in adversarial review).
    headers: { "Content-Type": "application/json", ...init?.headers, ...csrfHeaders },
  });

  if (!res.ok) {
    const body = (await res.json().catch(() => null)) as (ProblemDetails & ApiError) | null;
    throw new Error(extractApiError(body, `Request failed: ${res.status}`));
  }

  return (await res.json()) as T;
}

/**
 * Triggers a browser file download from a fetch response.
 * Handles Content-Disposition and falls back to the provided filename.
 */
export async function downloadFile(url: string, fallbackFilename: string): Promise<void> {
  const res = await fetch(url, { credentials: "include", headers: { ...csrfHeaders } });
  if (!res.ok) {
    const body = (await res.json().catch(() => null)) as (ProblemDetails & ApiError) | null;
    throw new Error(extractApiError(body, `Export failed: ${res.status}`));
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

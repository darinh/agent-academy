/**
 * Shared HTTP helpers for all API modules.
 * Domain modules import these directly; only `apiBaseUrl` is re-exported from the barrel.
 */

export interface ApiError {
  error: string;
}

export const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL as string | undefined)?.replace(/\/$/, "") ?? "";

export function apiUrl(path: string) {
  return `${apiBaseUrl}${path}`;
}

export async function request<T>(url: string, init?: RequestInit): Promise<T> {
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

/**
 * Triggers a browser file download from a fetch response.
 * Handles Content-Disposition and falls back to the provided filename.
 */
export async function downloadFile(url: string, fallbackFilename: string): Promise<void> {
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

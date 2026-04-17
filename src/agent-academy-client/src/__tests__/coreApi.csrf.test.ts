// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { request, csrfHeaders, downloadFile } from "../api/core";

/**
 * Regression tests for #80 CSRF protection wiring in the shared `request()`
 * helper. The server-side middleware rejects mutating cookie-authed requests
 * that lack the X-Requested-With header, so `request()` must apply
 * `csrfHeaders` in a way that survives every caller's custom init.headers.
 * Earlier iterations had a spread-order bug where `...init` at the end of
 * the fetch options re-overrode `headers`, silently stripping CSRF.
 */
describe("request() CSRF header", () => {
  const originalFetch = globalThis.fetch;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({}),
    } as Response);
    globalThis.fetch = fetchMock as unknown as typeof fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  function capturedHeaders(): Record<string, string> {
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const init = fetchMock.mock.calls[0][1] as RequestInit;
    return init.headers as Record<string, string>;
  }

  it("sends X-Requested-With on a plain call", async () => {
    await request("http://test/api/any");
    expect(capturedHeaders()["X-Requested-With"]).toBe("XMLHttpRequest");
  });

  it("sends X-Requested-With on a POST call", async () => {
    await request("http://test/api/any", { method: "POST", body: "{}" });
    expect(capturedHeaders()["X-Requested-With"]).toBe("XMLHttpRequest");
  });

  it("preserves caller-supplied Content-Type AND includes X-Requested-With", async () => {
    // This is the regression: runAgent() passes text/plain and must not lose CSRF.
    await request("http://test/api/agents/x/run", {
      method: "POST",
      headers: { "Content-Type": "text/plain" },
      body: "hello",
    });
    const headers = capturedHeaders();
    expect(headers["Content-Type"]).toBe("text/plain");
    expect(headers["X-Requested-With"]).toBe("XMLHttpRequest");
  });

  it("preserves caller-supplied arbitrary header AND includes X-Requested-With", async () => {
    await request("http://test/api/any", {
      method: "PUT",
      headers: { "X-Custom": "yes" },
    });
    const headers = capturedHeaders();
    expect(headers["X-Custom"]).toBe("yes");
    expect(headers["X-Requested-With"]).toBe("XMLHttpRequest");
  });

  it("uses credentials: include", async () => {
    await request("http://test/api/any");
    const init = fetchMock.mock.calls[0][1] as RequestInit;
    expect(init.credentials).toBe("include");
  });

  it("csrfHeaders export carries the expected shape", () => {
    expect(csrfHeaders).toEqual({ "X-Requested-With": "XMLHttpRequest" });
  });
});

describe("downloadFile() CSRF header", () => {
  const originalFetch = globalThis.fetch;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      blob: async () => new Blob(["x"]),
      headers: new Headers(),
    } as unknown as Response);
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    // JSDOM provides createObjectURL/revokeObjectURL via a stub below
    (URL as unknown as { createObjectURL: () => string }).createObjectURL = () => "blob:x";
    (URL as unknown as { revokeObjectURL: () => void }).revokeObjectURL = () => { /* noop */ };
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it("sends X-Requested-With on download", async () => {
    await downloadFile("http://test/api/export", "file.json");
    const init = fetchMock.mock.calls[0][1] as RequestInit;
    const headers = init.headers as Record<string, string>;
    expect(headers["X-Requested-With"]).toBe("XMLHttpRequest");
  });
});

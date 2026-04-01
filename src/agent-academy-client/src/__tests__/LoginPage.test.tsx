import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import LoginPage from "../LoginPage";

describe("LoginPage", () => {
  it("renders degraded-state recovery guidance with the connected user identity", () => {
    const markup = renderToStaticMarkup(
      <LoginPage copilotStatus="degraded" user={{ login: "athena", name: "Athena" }} />,
    );

    expect(markup).toContain("Copilot SDK unavailable - agents cannot work");
    expect(markup).toContain("Connected as");
    expect(markup).toContain("Athena");
    expect(markup).toContain("Still connected");
    expect(markup).toContain("Paused until re-auth");
    expect(markup).toContain("Reconnect GitHub");
  });

  it("renders the standard sign-in path when Copilot is unavailable", () => {
    const markup = renderToStaticMarkup(
      <LoginPage copilotStatus="unavailable" user={null} />,
    );

    expect(markup).toContain("Sign in to enter the academy");
    expect(markup).toContain("Not connected");
    expect(markup).toContain("Sign-in required");
    expect(markup).toContain("Login with GitHub");
  });
});

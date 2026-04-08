import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import LoginPage from "../LoginPage";

describe("LoginPage", () => {
  it("renders the standard sign-in path when Copilot is unavailable", () => {
    const markup = renderToStaticMarkup(
      <LoginPage copilotStatus="unavailable" user={null} />,
    );

    expect(markup).toContain("Sign in to enter the academy");
    expect(markup).toContain("Not connected");
    expect(markup).toContain("Sign-in required");
    expect(markup).toContain("Login with GitHub");
  });

  it("renders the connected user identity whenever the backend includes a user payload", () => {
    const markup = renderToStaticMarkup(
      <LoginPage copilotStatus="degraded" user={{ login: "athena", name: "Athena" }} />,
    );

    expect(markup).toContain("Connected as");
    expect(markup).toContain("Athena");
  });
});

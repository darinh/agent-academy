# 018: Testing Strategy

## Purpose

Documents the testing strategy for Agent Academy based on existing test infrastructure. Covers the test pyramid (unit/integration/E2E layers), testing conventions for backend (xUnit) and frontend (Vitest/Playwright), coverage targets, and reporting setup.

## Status

**Implemented** — Documents existing testing infrastructure discovered during spec coverage survey (2026-04-14).

## Test Pyramid

Agent Academy follows a standard test pyramid with emphasis on unit tests:

```
        /\
       /E2E\          ~18 tests (Playwright)
      /------\
     / Integ  \       ~2 tests (ASP.NET Core integration)
    /----------\
   /    Unit    \     ~4,500+ tests (xUnit + Vitest)
  /--------------\
```

### Distribution

**Backend** (xUnit):
- **Unit Tests**: 170 test files (tests/AgentAcademy.Server.Tests/*.cs)
- **Integration Tests**: 2 files using in-memory SQLite and DI container
  - `SubServiceIntegrationTests.cs` — service layer integration
  - `PullRequestSyncServiceTests.cs` — GitHub API integration

**Frontend** (Vitest + Playwright):
- **Unit Tests**: 116 test files (src/agent-academy-client/src/**/*.test.{ts,tsx})
- **E2E Tests**: 18 Playwright specs (src/agent-academy-client/e2e/*.spec.ts)

**Total Test Count**: ~4,500+ tests across both stacks (exact count varies as tests are added).

## Backend Testing (xUnit)

### Framework & Tools

- **Test Runner**: xUnit 2.5.3 (`tests/AgentAcademy.Server.Tests/AgentAcademy.Server.Tests.csproj`)
- **Mocking**: NSubstitute 5.3.0
- **Database**: Microsoft.EntityFrameworkCore.Sqlite 8.* (in-memory for isolation)
- **Coverage**: coverlet.collector 6.0.0

### Test Organization

Tests live in `tests/AgentAcademy.Server.Tests/` and follow naming convention:
```
{ServiceOrController}Tests.cs
```

Examples:
- `AgentOrchestratorTests.cs`
- `TaskLifecycleServiceTests.cs`
- `RoomControllerTests.cs`

### Test Structure Pattern

```csharp
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

public class AgentOrchestratorTests
{
    [Fact]
    public void BuildAssignmentPlanContent_IncludesObjectiveAndCriteria()
    {
        // Arrange
        var assignment = new ParsedTaskAssignment(
            Agent: "Hephaestus",
            Title: "Add plan seeding",
            Description: "Persist plan content for breakout rooms",
            Criteria: ["Plan tab shows content", "No API regressions"],
            Type: TaskType.Feature);

        // Act
        var content = PromptBuilder.BuildAssignmentPlanContent(assignment);

        // Assert
        Assert.Contains("# Add plan seeding", content);
        Assert.Contains("## Objective", content);
        Assert.Contains("Persist plan content for breakout rooms", content);
        Assert.Contains("## Acceptance Criteria", content);
        Assert.Contains("- Plan tab shows content", content);
    }
}
```

**Conventions**:
- `[Fact]` for parameterless tests
- `[Theory]` + `[InlineData]` for parameterized tests
- Method names: `{MethodUnderTest}_{Scenario}_{ExpectedBehavior}`
- Arrange/Act/Assert structure (comments optional but common)

### Mocking with NSubstitute

```csharp
private readonly IGitHubService _gitHubService = Substitute.For<IGitHubService>();
```

Tests use `NSubstitute.Substitute.For<T>()` for interface mocking. Concrete implementations are tested directly where possible.

### Integration Tests

Integration tests use `[Collection("WorkspaceRuntime")]` attribute and real service dependency graphs:

**`SubServiceIntegrationTests.cs`** (lines 1-80):
- In-memory SQLite connection (`SqliteConnection("Data Source=:memory:")`)
- Real `AgentAcademyDbContext` with `.UseSqlite()`
- Full service graph initialization (RoomService, TaskOrchestrationService, etc.)
- Implements `IDisposable` for connection cleanup

**Pattern**:
```csharp
[Collection("WorkspaceRuntime")]
public class SubServiceIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    // ... real services injected

    public SubServiceIntegrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        
        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;
        
        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();
        
        // Initialize service graph...
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
```

### Test Execution

```bash
# Run all backend tests
dotnet test tests/AgentAcademy.Server.Tests/AgentAcademy.Server.Tests.csproj

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

## Frontend Testing (Vitest + Playwright)

### Unit Testing (Vitest)

**Configuration**: `src/agent-academy-client/vite.config.ts` (lines 23-25)
```typescript
test: {
  exclude: ['e2e/**', 'node_modules/**'],
},
```

**Test Files**: `src/agent-academy-client/src/__tests__/*.test.{ts,tsx}`

**Dependencies** (`src/agent-academy-client/package.json`):
- `vitest` — test runner
- `@testing-library/react` 16.3.2 — React component testing
- `@testing-library/jest-dom` 6.9.1 — DOM matchers
- `@testing-library/user-event` 14.6.1 — user interaction simulation
- `jsdom` 29.0.2 — DOM implementation for Node

**Example Test Structure** (`src/agent-academy-client/src/__tests__/dashboardPanel.test.ts`, lines 1-60):

```typescript
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import type { WorkspaceOverview, RoomSnapshot, AgentDefinition } from "../api";
import { phaseColor, loadTimeRange, saveTimeRange } from "../dashboardUtils";

// ── Factories ──

function makeAgent(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
  return {
    id: "agent-1",
    name: "Software Engineer",
    role: "engineer",
    // ... default properties
    ...overrides,
  };
}

function makeRoom(overrides: Partial<RoomSnapshot> = {}): RoomSnapshot {
  return {
    id: "room-1",
    name: "Main Room",
    // ... default properties
    ...overrides,
  };
}

// ── Tests ──

describe("Dashboard utilities", () => {
  it("returns correct color for each phase", () => {
    expect(phaseColor("Planning")).toBe("brand");
    expect(phaseColor("Implementation")).toBe("success");
    // ...
  });
});
```

**Conventions**:
- Factory functions (`makeAgent`, `makeRoom`, etc.) for test data
- `describe` blocks group related tests
- `it` or `test` for individual test cases
- Type imports from `../api` for type safety
- Mock vitest modules with `vi.mock()`

**Test Execution**:
```bash
cd src/agent-academy-client

# Run tests once
npm test

# Watch mode
npm run test:watch
```

### E2E Testing (Playwright)

**Configuration**: `src/agent-academy-client/playwright.config.ts`

```typescript
export default defineConfig({
  testDir: "./e2e",
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: "list",
  use: {
    baseURL: "http://localhost:5173",
    trace: "on-first-retry",
  },
  webServer: {
    command: "npm run dev",
    url: "http://localhost:5173",
    reuseExistingServer: !process.env.CI,
    timeout: 15_000,
  },
});
```

**Test Files**: `src/agent-academy-client/e2e/*.spec.ts` (18 specs)

Examples:
- `dashboard.spec.ts`
- `task-list.spec.ts`
- `chat-panel.spec.ts`
- `workspace.spec.ts`

**Example E2E Test** (`src/agent-academy-client/e2e/dashboard.spec.ts`, lines 1-50):

```typescript
import { test, expect, mockTasks } from "./fixtures";

test.describe("Dashboard panel", () => {
  test("renders dashboard tab with summary cards", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    // Navigate to Dashboard tab
    const dashTab = page.getByRole("tab", { name: /dashboard/i });
    await expect(dashTab).toBeVisible({ timeout: 5_000 });
    await dashTab.click();

    // Summary cards should render with labels
    await expect(page.getByText("Rooms").first()).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("Agents").first()).toBeVisible({ timeout: 3_000 });
  });

  test("shows time range selector buttons", async ({ mockedPage: page }) => {
    await page.goto("/");
    // ...
  });
});
```

**Conventions**:
- Custom fixtures in `e2e/fixtures.ts` (e.g., `mockedPage` with API mocking)
- Explicit timeouts for flaky UI elements (e.g., `{ timeout: 5_000 }`)
- `force: true` when elements may be overlaid by other components
- `test.describe` blocks group related scenarios

**Test Execution**:
```bash
cd src/agent-academy-client

# Run E2E tests headless
npm run test:e2e

# Run with UI (interactive)
npm run test:e2e:ui
```

## Coverage Targets

### Backend Coverage

**Tool**: coverlet.collector 6.0.0 (integrated with `dotnet test`)

**Target**: No explicit percentage target defined in codebase. Current test-to-source ratio: 170 test files covering ~200+ service/controller files.

**Run Coverage**:
```bash
dotnet test --collect:"XPlat Code Coverage"
# Output: tests/AgentAcademy.Server.Tests/TestResults/{guid}/coverage.cobertura.xml
```

### Frontend Coverage

**Tool**: Vitest built-in coverage (via `v8` or `istanbul`, config not explicitly set)

**Target**: No explicit percentage target defined.

**Run Coverage**:
```bash
cd src/agent-academy-client
npx vitest run --coverage
```

### Coverage Philosophy

Agent Academy prioritizes **test count** and **behavior coverage** over strict line coverage percentages:

- **Backend**: 4,000+ xUnit tests ensure comprehensive behavior validation
- **Frontend**: 116 unit tests + 18 E2E tests cover critical user journeys
- **Integration**: 2 integration tests validate cross-layer interactions

No CI coverage gates exist. Coverage is monitored manually via `dotnet test --collect` and Vitest reports.

## Test Naming Conventions

### Backend (C#)
- File: `{ClassName}Tests.cs`
- Class: `public class {ClassName}Tests`
- Method: `{MethodName}_{Scenario}_{ExpectedOutcome}`

Examples:
- `ActivityBroadcaster_MultipleEvents_StoresAll`
- `Subscribe_Unsubscribe_NoLongerNotified`
- `GetRecentActivity_EmptyBuffer_ReturnsEmptyList`

### Frontend (TypeScript)
- File: `{componentOrModule}.test.ts` or `{componentOrModule}.test.tsx`
- Test: `it("describes the expected behavior", () => { ... })`

Examples:
- `it("returns correct color for each phase", () => { ... })`
- `it("renders dashboard tab with summary cards", async ({ mockedPage: page }) => { ... })`

## Test Collections & Parallelization

### Backend
- **Default**: Tests run in parallel per-assembly
- **Collection Attribute**: `[Collection("WorkspaceRuntime")]` forces sequential execution for tests sharing in-memory database state
- **Example**: `ProcessIntensiveCollection.cs`, `WorkspaceRuntimeCollection.cs`

### Frontend
- **Unit Tests**: Run in parallel (Vitest default)
- **E2E Tests**: `fullyParallel: true` in Playwright config
  - CI mode: `workers: 1` (sequential to avoid port conflicts)
  - Local: `workers: undefined` (parallel)

## Known Gaps

### Missing Coverage

1. **API Contract Tests**: No OpenAPI/Swagger validation tests. Controllers are tested via unit tests, not contract-driven tests.
2. **Performance Tests**: No load tests, stress tests, or benchmark suites.
3. **Security Tests**: No dedicated security test suite (e.g., OWASP Top 10 validation).
4. **Mutation Testing**: No mutation coverage (e.g., Stryker.NET).

### Coverage Reporting in CI

No automated coverage reports in CI pipelines. Coverage is run manually. No coverage badges or trend tracking.

### Test Data Management

- **Backend**: In-memory SQLite + hand-rolled test data (no fixtures library like AutoFixture or Bogus)
- **Frontend**: Factory functions per test file (no shared fixture library)

## Revision History

### 2026-04-14
- **Added**: Initial spec documenting existing test infrastructure
- **Evidence**: 170 backend test files, 116 frontend unit test files, 18 E2E specs
- **Survey**: Test pyramid distribution, xUnit/Vitest/Playwright conventions, coverage tooling
- **Source**: Tests discovered during spec coverage survey (task: write-spec-018-testing-strategy)

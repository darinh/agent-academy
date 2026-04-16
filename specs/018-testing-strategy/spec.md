# 018: Testing Strategy

## Purpose

Documents the testing strategy for Agent Academy based on existing test infrastructure. Covers the test pyramid (unit/integration/E2E layers), testing conventions for backend (xUnit) and frontend (Vitest/Playwright), coverage targets, and reporting setup.

## Status

**Implemented** — Documents existing testing infrastructure. Last verified: 2026-04-16.

## Test Pyramid

Agent Academy follows a standard test pyramid with emphasis on unit tests:

```
        /\
       /E2E\          ~18 tests (Playwright)
      /------\
     / Integ  \       ~35 tests (WebApplicationFactory contract tests)
    /----------\
   /    Unit    \     ~8,050+ tests (xUnit + Vitest)
  /--------------\
```

### Distribution

**Backend** (xUnit):
- **Unit Tests**: 193 test files (tests/AgentAcademy.Server.Tests/*.cs)
- **Integration Tests**: 2 files using in-memory SQLite and DI container
  - `SubServiceIntegrationTests.cs` — service layer integration
  - `PullRequestSyncServiceTests.cs` — GitHub API integration

**Frontend** (Vitest + Playwright):
- **Unit Tests**: 139 test files (src/agent-academy-client/src/**/*.test.{ts,tsx})
- **E2E Tests**: 18 Playwright specs (src/agent-academy-client/e2e/*.spec.ts)

**Total Test Count**: 8,103 tests across both stacks (5,222 backend + 2,881 frontend). Exact count varies as tests are added.

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

No CI coverage gates exist. Coverage is collected automatically in CI (see below) and available via `scripts/coverage.sh` locally.

## CI Coverage Reporting

The CI workflow (`.github/workflows/ci.yml`) collects coverage on every push and PR:

### Backend (.NET)
- **Tool**: coverlet.collector 6.0.0 via `dotnet test --collect:"XPlat Code Coverage"`
- **Merge**: ReportGenerator merges per-project Cobertura XML into a single report
- **Output**: Cobertura XML, text summary, and GitHub-flavored Markdown summary
- **Artifact**: Uploaded as `coverage-reports` (30-day retention)

### Frontend (Vitest)
- **Tool**: `@vitest/coverage-v8` — V8-native coverage with zero instrumentation overhead
- **Config**: `vite.config.ts` `test.coverage` section — reporters: `text`, `cobertura`, `html`
- **Output**: Cobertura XML + HTML report in `coverage/`
- **Artifact**: Uploaded alongside backend report

### Job Summary
Each CI run writes a coverage summary to the GitHub Actions job summary:
- Backend: line/branch/method percentages (from ReportGenerator TextSummary)
- Frontend: line and branch percentages (parsed from Cobertura XML)

### Local Usage
```bash
# Both backend and frontend
scripts/coverage.sh

# Backend only
scripts/coverage.sh --backend

# Frontend only
scripts/coverage.sh --frontend

# Or directly:
dotnet test --collect:"XPlat Code Coverage"
cd src/agent-academy-client && npm run test:coverage
```

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

## Mutation Testing

Mutation testing validates that tests detect behavioral changes — not just line coverage but real fault detection. Uses [Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/) 4.14.1.

### Setup

- **Tool**: `dotnet-stryker` installed as local tool (`.config/dotnet-tools.json`)
- **Config**: `stryker-config.json` (project root)
- **Runner**: `scripts/run-mutation-tests.sh`
- **Output**: `StrykerOutput/reports/` (HTML + JSON reports, gitignored)

### Targeted Modules

Mutation testing focuses on security-critical and business-logic files:

| File | Mutants | Score | Notes |
|------|---------|-------|-------|
| `Commands/CommandAuthorizer.cs` | 22 | **100%** | Permission matching, deny-overrides logic |
| `Services/PromptSanitizer.cs` | 16 | **100%** | Injection defense, Unicode control char stripping |
| `Commands/ShellCommand.cs` | 103 | **86%** | Shell command parsing and sandboxing |
| `Services/TaskLifecycleService.Review.cs` | 85 | **82%** | PR review workflow |
| `Services/TaskLifecycleService.SpecLinks.cs` | 39 | **74%** | Spec-task traceability |
| `Services/TaskLifecycleService.cs` | 123 | **63%** | Core task state machine |
| `Commands/CommandParser.cs` | 134 | **59%** | Command parsing (string mutation dominant) |

**Overall**: 72.21% (529 assessed: 375 killed, 7 timeout, 140 survived, 7 no coverage)

### Thresholds

- **Break** (CI fails): 50%
- **Low** (warning): 60%
- **High** (target): 80%

### Running

```bash
scripts/run-mutation-tests.sh              # Full run (~45 min)
scripts/run-mutation-tests.sh --security   # Security modules only (~10 min)
scripts/run-mutation-tests.sh --since develop  # Only changed files
```

### Survivor Analysis

Most surviving mutants are low-risk categories:
- **String mutations** (89/140): Log messages, error strings, format strings mutated to `""`. Tests verify behavior, not message content.
- **Statement mutations** (16/140): Logging statements removed. No behavioral impact.
- **Null coalescing mutations** (15/140): Fallback value swaps in defensive code paths.
- **Linq method mutations** (6/140): `FirstOrDefault()` → `First()`, `OrderBy()` → `OrderByDescending()`.

Priority for improvement: `CommandParser` (59%) and `TaskLifecycleService` (63%) — both have behavioral survivors beyond string mutations.

## Known Gaps

### Missing Coverage

1. ~~**API Contract Tests**: No OpenAPI/Swagger validation tests. Controllers are tested via unit tests, not contract-driven tests.~~ **Resolved** — 35 OpenAPI contract tests via `WebApplicationFactory` + `ApiContractFixture` validate Swagger doc generation, route coverage, response schemas, and endpoint roundtrips.
2. ~~**Performance Tests**: No load tests, stress tests, or benchmark suites.~~ **Resolved** — BenchmarkDotNet micro-benchmark suite in `tests/AgentAcademy.Server.Benchmarks/`. 5 benchmark classes covering: `CommandParser.Parse` (regex + string splitting, 5 scenarios), `CommandAuthorizer.Authorize` (permission matching, 5 scenarios), `PromptBuilder` (conversation/breakout/review prompt composition, 5 scenarios), `SpecManager` (search, relevance loading, tokenization, content hashing — parameterized by corpus size 5/20 sections, 11 methods), `TaskDependencyService` (BFS cycle detection + dependency queries over in-memory SQLite — parameterized by graph size 10/50/200 tasks, 4 methods). All benchmarks use `[MemoryDiagnoser]` for allocation tracking. Runner script: `scripts/run-benchmarks.sh`. Results exported to `BenchmarkDotNet.Artifacts/`.
3. ~~**Security Tests**: No dedicated security test suite (e.g., OWASP Top 10 validation).~~ **Resolved** — 97 security tests in `tests/AgentAcademy.Server.Tests/Security/` covering path traversal (ReadFileHandler, CodeWriteToolWrapper, SearchCodeHandler, FilesystemController), shell command sandboxing (ShellCommand.TryParse injection attacks), prompt injection defenses (PromptSanitizer edge cases, Unicode control chars), input validation (API endpoint boundaries, auth enforcement), and documented accepted risks (symlink traversal per spec 015 §9.2). Also fixed SearchCodeHandler silently broadening search scope on out-of-root paths.
4. ~~**Mutation Testing**: No mutation coverage (e.g., Stryker.NET).~~ **Resolved** — Stryker.NET 4.14.1 installed as a local dotnet tool (`.config/dotnet-tools.json`). Configuration at `stryker-config.json` targets 7 critical source files: `CommandParser`, `CommandAuthorizer`, `ShellCommand`, `PromptSanitizer`, and `TaskLifecycleService` (3 partials). Runner script: `scripts/run-mutation-tests.sh` with `--full`, `--security`, and `--since` modes. Initial results: 72.21% overall (529 mutants assessed, 375 killed, 7 timeout). Security-critical files (`CommandAuthorizer`, `PromptSanitizer`) at 100%. Remaining survivors are predominantly string mutations on log/error messages and statement mutations on non-behavioral code. Reports: `StrykerOutput/reports/` (HTML + JSON). Thresholds: break=50%, low=60%, high=80%.

### Coverage Reporting in CI

~~No automated coverage reports in CI pipelines. Coverage is run manually. No coverage badges or trend tracking.~~ **Resolved** — CI collects backend (coverlet + ReportGenerator) and frontend (`@vitest/coverage-v8`) coverage on every run, generates Cobertura XML + summary, uploads artifacts, and writes a job summary. See "CI Coverage Reporting" section above. Local script: `scripts/coverage.sh`.

### Test Data Management

- **Backend**: In-memory SQLite + hand-rolled test data (no fixtures library like AutoFixture or Bogus)
- **Frontend**: Factory functions per test file (no shared fixture library)

## Revision History

### 2026-04-16
- **Added**: Stryker.NET mutation testing (`stryker-config.json`, `scripts/run-mutation-tests.sh`)
- **Tool**: Stryker.NET 4.14.1 as local dotnet tool, targets 7 critical source files
- **Results**: 72.21% overall; security modules (CommandAuthorizer, PromptSanitizer) at 100%
- **Test**: Added `Authorize_EmptyPatternInAllowList_DoesNotMatchAnyCommand` to kill surviving mutant
- **Resolved**: Known Gap #4 (Mutation Testing)

### 2026-04-16
- **Added**: BenchmarkDotNet performance benchmark suite (`tests/AgentAcademy.Server.Benchmarks/`)
- **Benchmarks**: CommandParser, CommandAuthorizer, PromptBuilder, SpecManager (parameterized), TaskDependencyService (parameterized)
- **Infrastructure**: Runner script (`scripts/run-benchmarks.sh`), `InternalsVisibleTo` for benchmark project
- **Resolved**: Known Gap #2 (Performance Tests)

### 2026-04-14 (b)
- **Added**: CI coverage reporting — automated collection and job summaries
- **Evidence**: `ci.yml` updated with coverlet + ReportGenerator (.NET), `@vitest/coverage-v8` (frontend)
- **Resolved**: "Coverage Reporting in CI" known gap
- **Source**: Task ci-coverage-reporting

### 2026-04-14
- **Added**: Initial spec documenting existing test infrastructure
- **Evidence**: 170 backend test files, 116 frontend unit test files, 18 E2E specs
- **Survey**: Test pyramid distribution, xUnit/Vitest/Playwright conventions, coverage tooling
- **Source**: Tests discovered during spec coverage survey (task: write-spec-018-testing-strategy)

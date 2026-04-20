import { test as base, type Page } from "@playwright/test";

/**
 * Minimal mock data for API responses. Lets Playwright tests render the
 * frontend without a running backend.
 */

export const mockAuthStatus = {
  authEnabled: false,
  authenticated: false,
  copilotStatus: "ready" as const,
  user: null,
};

export const mockAuthStatusAuthenticated = {
  authEnabled: true,
  authenticated: true,
  copilotStatus: "operational" as const,
  user: {
    login: "testuser",
    name: "Test User",
    avatarUrl: null,
  },
};

export const mockWorkspace = {
  active: {
    path: "/tmp/test-project",
    projectName: "test-project",
    lastAccessedAt: new Date().toISOString(),
  },
  dataDir: "/tmp/test-data",
};

export const mockAgents = [
  {
    id: "planner-1",
    name: "Aristotle",
    role: "Planner",
    summary: "Planning lead",
    startupPrompt: "",
    capabilityTags: ["planning"],
    enabledTools: ["chat"],
    autoJoinDefaultRoom: true,
  },
  {
    id: "engineer-1",
    name: "Hephaestus",
    role: "SoftwareEngineer",
    summary: "Backend engineer",
    startupPrompt: "",
    capabilityTags: ["implementation"],
    enabledTools: ["chat", "code"],
    autoJoinDefaultRoom: true,
  },
];

export const mockRoom = {
  id: "main",
  name: "Main Room",
  status: "Active",
  currentPhase: "Planning",
  recentMessages: [] as unknown[],
  participants: mockAgents.map((a) => ({
    agentId: a.id,
    name: a.name,
    role: a.role,
    availability: "Available",
    isPreferred: false,
    lastActivityAt: new Date().toISOString(),
    activeCapabilities: a.capabilityTags,
  })),
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString(),
  activeTask: null,
  topic: null,
};

export const mockActivity = [
  {
    id: "evt-1",
    type: "TaskCreated" as const,
    severity: "Info" as const,
    roomId: "main",
    actorId: "planner-1",
    taskId: "task-1",
    message: "Task 'Implement user authentication' created by Aristotle",
    correlationId: null,
    occurredAt: new Date(Date.now() - 60_000).toISOString(),
  },
  {
    id: "evt-2",
    type: "AgentFinished" as const,
    severity: "Info" as const,
    roomId: "main",
    actorId: "engineer-1",
    taskId: "task-2",
    message: "Hephaestus completed work on task 'Fix dashboard layout'",
    correlationId: null,
    occurredAt: new Date(Date.now() - 120_000).toISOString(),
  },
  {
    id: "evt-3",
    type: "AgentErrorOccurred" as const,
    severity: "Error" as const,
    roomId: "main",
    actorId: "engineer-1",
    taskId: null,
    message: "Agent error: rate limit exceeded",
    correlationId: null,
    occurredAt: new Date(Date.now() - 300_000).toISOString(),
  },
];

export const mockOverview = {
  configuredAgents: mockAgents,
  rooms: [mockRoom],
  recentActivity: mockActivity,
  agentLocations: mockAgents.map((a) => ({
    agentId: a.id,
    roomId: "main",
    state: "Idle",
    updatedAt: new Date().toISOString(),
  })),
  breakoutRooms: [],
  goalCards: { total: 0, active: 0, challenged: 0, completed: 0, abandoned: 0, verdictProceed: 0, verdictProceedWithCaveat: 0, verdictChallenge: 0 },
  generatedAt: new Date().toISOString(),
};

export const mockTasks = [
  {
    id: "task-1",
    title: "Implement user authentication",
    description: "Add JWT-based auth flow",
    successCriteria: "Login works end-to-end",
    status: "Active",
    type: "Feature",
    currentPhase: "Implementation",
    currentPlan: null,
    validationStatus: "Ready",
    validationSummary: "Pending",
    implementationStatus: "InProgress",
    implementationSummary: "Working on it",
    qualityScore: null,
    commitCount: 3,
    testsCreated: [],
    assignedAgentId: "engineer-1",
    assignedAgentName: "Hephaestus",
    roomId: "main",
    branchName: "feat/user-auth",
    pullRequestUrl: "https://github.com/test/repo/pull/42",
    pullRequestNumber: 42,
    pullRequestStatus: "Open",
    reviewRound: 0,
    mergeCommitSha: null,
    createdAt: new Date().toISOString(),
    startedAt: new Date().toISOString(),
    completedAt: null,
    comments: [],
    items: [],
  },
  {
    id: "task-2",
    title: "Fix dashboard layout",
    description: "Resolve overflow issue on small screens",
    successCriteria: "No horizontal scroll on mobile",
    status: "Completed",
    type: "Bug",
    currentPhase: "FinalSynthesis",
    currentPlan: null,
    validationStatus: "Completed",
    validationSummary: "All checks pass",
    implementationStatus: "Completed",
    implementationSummary: "Fixed CSS grid",
    qualityScore: 95,
    commitCount: 1,
    testsCreated: ["layout.test.ts"],
    assignedAgentId: "engineer-1",
    assignedAgentName: "Hephaestus",
    roomId: "main",
    branchName: "fix/dashboard-layout",
    pullRequestUrl: null,
    pullRequestNumber: null,
    pullRequestStatus: null,
    reviewRound: 1,
    mergeCommitSha: "abc123",
    createdAt: new Date().toISOString(),
    startedAt: new Date().toISOString(),
    completedAt: new Date().toISOString(),
    comments: [],
    items: [],
  },
];

export const mockCommands = [
  { name: "LIST_ROOMS", label: "List Rooms", description: "Show all collaboration rooms", fields: [] },
  { name: "LIST_AGENTS", label: "List Agents", description: "Show configured agents", fields: [] },
  { name: "LIST_TASKS", label: "List Tasks", description: "Show all tasks", fields: [] },
  { name: "RUN_BUILD", label: "Run Build", description: "Build the project", fields: [] },
  { name: "RUN_TESTS", label: "Run Tests", description: "Run the test suite", fields: [] },
  { name: "CREATE_PR", label: "Create PR", description: "Create a GitHub pull request", fields: [
    { name: "taskId", label: "Task ID", type: "text", required: true },
  ]},
];

export const mockInstanceHealth = {
  instanceId: "test-instance-123",
  version: "0.1.0",
  uptimeSeconds: 3600,
  startedAt: new Date().toISOString(),
  crashDetected: false,
  previousInstanceId: null,
  environment: "Development",
};

/**
 * Sets up route interception on a Playwright page to mock all API
 * endpoints the frontend calls on load. Call this before navigating.
 */
export async function mockAllApis(page: Page) {
  await page.route("**/api/auth/status", (route) =>
    route.fulfill({ json: mockAuthStatus }));

  await page.route("**/api/workspace", (route) =>
    route.fulfill({ json: mockWorkspace }));

  await page.route("**/api/overview", (route) =>
    route.fulfill({ json: mockOverview }));

  await page.route("**/api/tasks", (route) =>
    route.fulfill({ json: mockTasks }));

  await page.route("**/api/rooms", (route) =>
    route.fulfill({ json: [mockRoom] }));

  await page.route("**/api/rooms/main", (route) =>
    route.fulfill({ json: mockRoom }));

  await page.route("**/api/agents/configured", (route) =>
    route.fulfill({ json: mockAgents }));

  await page.route("**/api/commands", (route) =>
    route.fulfill({ json: mockCommands }));

  await page.route("**/api/health/instance", (route) =>
    route.fulfill({ json: mockInstanceHealth }));

  await page.route("**/api/activity/recent", (route) =>
    route.fulfill({ json: mockActivity }));

  // Settings panel routes
  await page.route("**/api/notifications/providers", (route) =>
    route.fulfill({ json: [] }));

  await page.route("**/api/instruction-templates", (route) =>
    route.fulfill({ json: [] }));

  await page.route("**/api/settings", (route) =>
    route.fulfill({ json: {} }));

  await page.route("**/healthz", (route) =>
    route.fulfill({ json: { status: "Healthy", uptime: "01:00:00", timestamp: new Date().toISOString() } }));

  // SignalR negotiate — return a proper error to let the client fall back gracefully
  await page.route("**/hubs/activity/negotiate**", (route) =>
    route.fulfill({ status: 404, body: "Not Found" }));
  await page.route("**/hubs/activity", (route) =>
    route.fulfill({ status: 404 }));
  await page.route("**/api/activity/stream", (route) =>
    route.fulfill({ status: 404 }));

  // Audit log stubs (dashboard AuditLogPanel)
  await page.route("**/api/commands/audit/stats**", (route) =>
    route.fulfill({ json: { totalCommands: 0, byStatus: {}, byAgent: {}, byCommand: {}, windowHours: null } }));
  await page.route("**/api/commands/audit?**", (route) =>
    route.fulfill({ json: { records: [], total: 0, limit: 15, offset: 0 } }));
  await page.route("**/api/commands/audit", (route) =>
    route.fulfill({ json: { records: [], total: 0, limit: 15, offset: 0 } }));
}

/**
 * Extended test fixture that mocks all APIs before each test.
 */
export const test = base.extend<{ mockedPage: Page }>({
  mockedPage: async ({ page }, use) => {
    await mockAllApis(page);
    await use(page);
  },
});

export { expect } from "@playwright/test";

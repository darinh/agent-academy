# T3 Acceptance Criteria: Refactor Small Existing File (Adversarial Case)

**Task**: Refactor a small existing file to expose potential weaknesses in the Forge Pipeline Engine

**Frozen**: 2026-04-19 (before implementation)

**Purpose**: This is an adversarial test case designed to expose pipeline weaknesses. The refactoring should be non-trivial but achievable by a competent developer, and should test the pipeline's ability to handle code that requires understanding context, preserving behavior, and making judgment calls.

---

## Target File Selection

- [ ] Target file: `src/AgentAcademy.Server/Notifications/NotificationManager.cs`
- [ ] File size: 250-300 lines (verified: current file is 284 lines)
- [ ] Complexity: Contains multiple methods, dependencies, error handling, concurrency concerns
- [ ] Test coverage: File has existing tests in `tests/AgentAcademy.Server.Tests/NotificationManagerTests.cs`

---

## Refactoring Requirements

### 1. Extract Retry Logic Abstraction
**Current state**: Retry logic is inline in 5 different methods via `NotificationRetryPolicy.ExecuteAsync` calls

**Required refactoring**:
- [ ] Extract a private helper method `ExecuteWithRetryAsync<T>` that wraps retry logic
- [ ] Method signature: `private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, string operationName, CancellationToken cancellationToken)`
- [ ] Replace all 5 inline `NotificationRetryPolicy.ExecuteAsync` calls with calls to this helper
- [ ] Preserve all existing behavior: retry logic, logging, cancellation handling

**Lines affected** (approximately):
- Lines 67-71 (SendToAllAsync)
- Lines 153-157 (SendAgentQuestionAsync)
- Lines 202-206 (SendDirectMessageDisplayAsync)
- Lines 240-244 (NotifyRoomRenamedAsync)
- Lines 270-274 (NotifyRoomClosedAsync)

### 2. Simplify Provider Lookup Pattern
**Current state**: Multiple methods filter for `.Where(p => p.IsConnected)` independently

**Required refactoring**:
- [ ] Extract a private property or method `ConnectedProviders` that returns `IEnumerable<INotificationProvider>`
- [ ] Replace all inline `.Where(p => p.IsConnected)` calls with this abstraction
- [ ] Preserve existing behavior: no caching (always fetch fresh connected state)

**Lines affected** (approximately):
- Line 61 (SendToAllAsync)
- Line 140 (SendAgentQuestionAsync)
- Line 193 (SendDirectMessageDisplayAsync)
- Line 235 (NotifyRoomRenamedAsync)
- Line 263 (NotifyRoomClosedAsync)

### 3. Consolidate Error Tracking
**Current state**: Tracker calls (`RecordDeliveryAsync`, `RecordSkippedAsync`, `RecordFailureAsync`) are repeated in multiple locations

**Required refactoring**:
- [ ] Extract private helper methods for common tracking patterns:
  - `RecordDeliveryIfTracked(string messageType, string providerId, ...)`
  - `RecordSkippedIfTracked(string messageType, string providerId, ...)`
  - `RecordFailureIfTracked(string messageType, string providerId, ..., string error)`
- [ ] Each helper checks `_tracker is not null` internally
- [ ] Replace all inline tracker calls with these helpers
- [ ] Preserve exact same tracking behavior and parameters

### 4. Code Quality Improvements
- [ ] Remove any unnecessary null checks after helper extraction
- [ ] Ensure all extracted methods have XML documentation comments
- [ ] Maintain consistent formatting and spacing
- [ ] No change to public API surface (all changes are internal implementation)

---

## Correctness Requirements

### 5. Behavioral Preservation
- [ ] All existing unit tests in `NotificationManagerTests.cs` pass without modification
- [ ] All existing integration tests pass (if any)
- [ ] No changes to public method signatures
- [ ] No changes to thread safety guarantees (still thread-safe)
- [ ] No changes to error handling semantics (same exceptions, same logging)
- [ ] No performance regression >10% (measured by existing test suite runtime)

### 6. Side Effects & Edge Cases
- [ ] Retry count/delay remains unchanged (uses same `NotificationRetryPolicy`)
- [ ] Cancellation still works correctly (cancellation tokens are propagated)
- [ ] Logging verbosity unchanged (same log messages at same levels)
- [ ] Telemetry/tracking unchanged (same tracker calls with same parameters)
- [ ] Concurrent access still safe (no new race conditions introduced)

---

## Testing Requirements

### 7. Test Suite Updates
- [ ] **No test changes required** — refactoring is internal, existing tests should pass as-is
- [ ] If any test fails, it indicates a behavioral regression (FAIL condition)
- [ ] All tests in `NotificationManagerTests.cs` pass: `dotnet test --filter "FullyQualifiedName~NotificationManagerTests"`
- [ ] All tests in `NotificationRetryPolicyTests.cs` pass (retry behavior unchanged)
- [ ] All tests in `NotificationControllerTests.cs` pass (integration unchanged)

### 8. Manual Verification
- [ ] File compiles without errors: `dotnet build src/AgentAcademy.Server/AgentAcademy.Server.csproj`
- [ ] No new compiler warnings introduced
- [ ] Code analysis passes (if enabled): `dotnet build /p:RunAnalyzers=true`
- [ ] Refactored code is more readable than original (subjective but should be obvious)

---

## Difficulty Calibration (Adversarial Aspects)

This refactoring is designed to be **moderately difficult** for an AI pipeline because:

1. **Context Understanding**: Requires reading multiple methods to identify common patterns
2. **Judgment Calls**: Where to extract, what to name, how to parameterize — not mechanically obvious
3. **Behavioral Equivalence**: Must preserve subtle semantics (retry timing, log ordering, cancellation propagation)
4. **Side Effect Tracking**: Telemetry calls must remain in exact same locations relative to operations
5. **Concurrency**: Must not introduce race conditions when extracting shared code
6. **Testing Rigor**: Existing tests must pass without modification — can't "fix" tests to match new behavior

**Expected pipeline challenges**:
- Identifying which abstractions are worth extracting (vs. over-engineering)
- Naming extracted methods appropriately
- Preserving exact retry/logging/tracking behavior during extraction
- Handling generic types correctly in helper methods
- Not breaking thread safety when extracting shared state access

---

## Verification Method

1. **Automated Tests**: 
   ```bash
   dotnet test --filter "FullyQualifiedName~NotificationManager" --no-build
   ```
   → All tests pass (100% success rate)

2. **Build Verification**:
   ```bash
   dotnet build src/AgentAcademy.Server/AgentAcademy.Server.csproj
   ```
   → Clean build with zero errors, zero new warnings

3. **Diff Review**:
   ```bash
   git diff src/AgentAcademy.Server/Notifications/NotificationManager.cs
   ```
   → Verify:
   - Only `NotificationManager.cs` changed (no other files)
   - Changes are extract-method refactorings (no logic changes)
   - Line count reduction of 15-30 lines (due to de-duplication)
   - No changes to public method signatures

4. **Code Review Checklist**:
   - [ ] All extracted methods are `private`
   - [ ] All extracted methods have XML doc comments
   - [ ] No copy-paste duplication remains
   - [ ] Helper methods have clear, descriptive names
   - [ ] Generic type parameters used correctly (if applicable)

---

## Pass/Fail Criteria

**PASS**:
- All refactoring requirements (1-4) implemented
- All correctness requirements (5-6) met
- All tests pass without modification
- Build succeeds with zero errors, zero new warnings
- Code review checklist complete
- Refactored code is observably cleaner/simpler than original

**FAIL** (any of):
- Any existing test fails (indicates behavioral regression)
- Build errors or new warnings introduced
- Public API changed (method signatures altered)
- Performance regression >10%
- Thread safety compromised (race conditions introduced)
- Telemetry/tracking behavior changed
- Refactoring incomplete (only partial extraction)
- Tests modified to accommodate refactoring (tests should not change)

---

## Out of Scope

The following are explicitly NOT part of this refactoring:
- Changing public API or adding new features
- Improving performance or optimization
- Adding new tests (existing tests should pass as-is)
- Changing logging messages or levels
- Modifying retry policy behavior
- Refactoring other files in the Notifications namespace
- Updating documentation or comments (except XML docs on new private methods)
- Changing dependency injection registration
- Handling TODOs or known issues in comments

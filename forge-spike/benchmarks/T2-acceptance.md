# T2 Acceptance Criteria: Write Technical Spec for NotificationManager

**Task**: Write a 3-page technical specification for the NotificationManager module (existing code in this repository)

**Frozen**: 2026-04-19 (before implementation)

---

## Success Criteria

### 1. Document Structure & Completeness
- [ ] Document title: "NotificationManager Technical Specification"
- [ ] Document length: 800-1200 words (approximately 3 pages at 12pt font, standard margins)
- [ ] Sections present (all required):
  - [ ] Overview / Purpose
  - [ ] Architecture / Design
  - [ ] API Reference (public methods)
  - [ ] Error Handling & Retry Logic
  - [ ] Thread Safety & Concurrency
  - [ ] Integration Points
- [ ] Each section contains substantive content (not just headings)

### 2. Accuracy & Evidence
- [ ] All class names, method names, and types match actual code in `src/AgentAcademy.Server/Notifications/NotificationManager.cs`
- [ ] Method signatures are accurate (parameter types, return types, nullability)
- [ ] Described behavior matches actual implementation (verified by code inspection)
- [ ] All referenced interfaces (`INotificationManager`, `INotificationProvider`) are correctly identified
- [ ] Retry policy behavior correctly describes `NotificationRetryPolicy.ExecuteAsync` usage
- [ ] Dependency injection registration correctly describes `ServiceCollectionExtensions.cs` setup

**Specific Code References Required**:
- [ ] Document references `NotificationManager` constructor parameters: `ILogger<NotificationManager>`, `NotificationDeliveryTracker?`
- [ ] Document describes `ConcurrentDictionary<string, INotificationProvider>` as storage mechanism
- [ ] Document explains `StringComparer.OrdinalIgnoreCase` for provider ID lookups
- [ ] Document describes all 9 public methods: `RegisterProvider`, `GetProvider`, `GetAllProviders`, `SendToAllAsync`, `RequestInputFromAnyAsync`, `SendAgentQuestionAsync`, `SendDirectMessageDisplayAsync`, `NotifyRoomRenamedAsync`, `NotifyRoomClosedAsync`

### 3. API Reference Quality
For each public method, document must include:
- [ ] Method signature (copy from code)
- [ ] Purpose (1-2 sentences)
- [ ] Parameters (name, type, description)
- [ ] Return value (type and meaning)
- [ ] Exceptions/error conditions (if any)

**Example** (for `SendToAllAsync`):
```
Method: public async Task<int> SendToAllAsync(NotificationMessage message, CancellationToken cancellationToken = default)
Purpose: Sends a notification to all connected providers. Individual provider failures are logged and do not prevent delivery to remaining providers.
Parameters:
  - message (NotificationMessage): The notification to send
  - cancellationToken (CancellationToken): Optional cancellation token
Returns: int - The number of providers that successfully delivered the message
Error Handling: Logs errors for individual provider failures but continues attempting delivery to remaining providers. Never throws exception on provider failure.
```

### 4. Technical Depth
- [ ] Explains retry policy integration (how `NotificationRetryPolicy.ExecuteAsync` wraps provider calls)
- [ ] Describes concurrent access pattern (thread-safe due to `ConcurrentDictionary`)
- [ ] Explains provider selection logic for input requests (iteration order not guaranteed)
- [ ] Documents `NotificationDeliveryTracker` integration (optional dependency, telemetry tracking)
- [ ] Describes failure modes (e.g., what happens when all providers fail)

### 5. Integration Points
- [ ] Documents dependency injection setup in `ServiceCollectionExtensions.cs`
- [ ] Lists all classes that consume `INotificationManager` (minimum: `ActivityNotificationBroadcaster`, `CopilotExecutor`, `DmHandler`)
- [ ] Explains relationship to `INotificationProvider` interface
- [ ] Documents how providers are registered (via `RegisterProvider` method)

### 6. Formatting & Readability
- [ ] Uses Markdown format (`.md` file)
- [ ] Code blocks use appropriate syntax highlighting (```csharp or ```typescript)
- [ ] Headings follow consistent hierarchy (H1 for title, H2 for major sections, H3 for subsections)
- [ ] No spelling errors (verified by spell checker)
- [ ] No grammar errors (verified by grammar checker or manual review)
- [ ] Consistent terminology (e.g., "provider" not switching between "provider" and "notifier")

### 7. Diagrams (Optional but Recommended)
If included, diagrams must:
- [ ] Be rendered as ASCII art, Mermaid, or embedded image
- [ ] Accurately represent the relationships described in code
- [ ] Include legend/key if symbols are not self-explanatory

### 8. Verifiability
- [ ] Every claim about behavior can be verified by reading the source code
- [ ] Every method/class reference can be found in the codebase
- [ ] No aspirational statements (e.g., "will support" or "should handle") — only factual "does" or "is"
- [ ] File paths are relative to repository root and valid

---

## Verification Method

1. **Word Count**: `wc -w T2-spec.md` → result between 800-1200 words
2. **Completeness Check**: All required sections present (checklist above)
3. **Accuracy Audit**:
   - Read `NotificationManager.cs` line-by-line
   - For each public method, verify spec description matches implementation
   - For each type/parameter, verify names and types are correct
   - For each behavior claim, trace through code to confirm
4. **Cross-Reference Check**:
   - Verify all referenced classes exist in codebase (`grep -r "class NotificationManager"`)
   - Verify method signatures match (`diff` spec signatures vs. code)
5. **Integration Points Check**:
   - Verify all listed consumers of `INotificationManager` are accurate (search codebase)
   - Verify DI registration description matches `ServiceCollectionExtensions.cs`

---

## Pass/Fail Criteria

**PASS**: 
- All checkboxes checked 
- Word count 800-1200
- Zero factual inaccuracies found during accuracy audit
- All method signatures match actual code
- All referenced files/classes exist

**FAIL**: 
- Any required section missing
- Word count <800 or >1200
- Any method signature incorrect
- Any behavior claim contradicts actual code
- Any referenced file/class does not exist
- More than 2 spelling/grammar errors

---

## Out of Scope

The following are explicitly NOT required:
- Implementation details of `INotificationProvider` (only describe the interface contract)
- Detailed documentation of `NotificationDeliveryTracker` internal implementation
- Performance benchmarks or optimization recommendations
- Historical context or design rationale (focus on current state)
- Usage examples or tutorials (this is a reference spec, not a guide)
- Documentation of private methods or internal implementation details
- Comparison to alternative designs or competing libraries

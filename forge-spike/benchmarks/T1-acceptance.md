# T1 Acceptance Criteria: Build a Small MCP Server

**Task**: Build a small MCP server with 2 tools (code_search + file_read)

**Frozen**: 2026-04-19 (before implementation)

---

## Success Criteria

### 1. MCP Server Metadata
- [ ] Server name: `forge-spike-mcp`
- [ ] Server version: `0.1.0`
- [ ] Implements MCP protocol version: `2024-11-05` or later
- [ ] Server starts successfully and responds to `initialize` request within 5 seconds

### 2. Tool: code_search
**Signature**:
```typescript
{
  name: "code_search",
  description: "Search for code patterns in repository files",
  inputSchema: {
    type: "object",
    properties: {
      pattern: { type: "string", description: "Text pattern to search for" },
      path: { type: "string", description: "Optional: directory to search within" }
    },
    required: ["pattern"]
  }
}
```

**Behavior**:
- [ ] Returns list of matches in format: `{file: string, line: number, content: string}[]`
- [ ] Searches recursively from specified path (or repo root if path not provided)
- [ ] Excludes common ignore patterns: `node_modules/`, `.git/`, `bin/`, `obj/`
- [ ] Handles empty results: returns `[]` with no error
- [ ] Handles invalid path: returns error with message containing "path not found" or "invalid path"
- [ ] Search is case-insensitive by default
- [ ] Maximum 100 results returned (if more matches exist, return first 100)

**Test Cases**:
1. Search for `"NotificationManager"` in path `"src/"` → returns ≥5 matches
2. Search for `"XYZNONEXISTENT123"` → returns `[]`
3. Search for `"function"` with path `"nonexistent/dir"` → returns error
4. Search for `"public class"` → returns results (case-insensitive works)

### 3. Tool: file_read
**Signature**:
```typescript
{
  name: "file_read",
  description: "Read contents of a file",
  inputSchema: {
    type: "object",
    properties: {
      path: { type: "string", description: "Relative path to file from repo root" },
      startLine: { type: "number", description: "Optional: first line to read (1-indexed)" },
      endLine: { type: "number", description: "Optional: last line to read (1-indexed)" }
    },
    required: ["path"]
  }
}
```

**Behavior**:
- [ ] Returns full file content as single string when no line range specified
- [ ] Returns specified line range (inclusive) when startLine/endLine provided
- [ ] Line numbers are 1-indexed (first line = 1)
- [ ] Handles file not found: returns error with message containing "not found" or "does not exist"
- [ ] Handles invalid line range: returns error with message containing "invalid range" or "line out of bounds"
- [ ] Preserves original file encoding (UTF-8 assumed)
- [ ] Maximum file size: 10MB (files larger than 10MB return error)

**Test Cases**:
1. Read `"README.md"` (full file) → returns content containing project name
2. Read `"README.md"` lines 1-5 → returns exactly 5 lines
3. Read `"nonexistent-file.txt"` → returns error
4. Read `"README.md"` lines 999-1000 (beyond file length) → returns error
5. Read `"README.md"` lines 5-2 (invalid range) → returns error

### 4. Error Handling
- [ ] All errors return MCP-compliant error responses (JSON-RPC 2.0 error format)
- [ ] Error codes are appropriate: -32602 for invalid params, -32603 for internal errors
- [ ] Error messages are human-readable and specific (not generic "error occurred")
- [ ] Server does not crash on malformed requests (returns error response instead)

### 5. Performance
- [ ] `code_search` completes within 10 seconds for repositories up to 100,000 lines of code
- [ ] `file_read` completes within 2 seconds for files up to 1MB
- [ ] Server handles 10 concurrent requests without degradation >20%

### 6. Integration
- [ ] Server can be started via `npm start` or equivalent single command
- [ ] Server works with standard MCP clients (e.g., Claude Desktop, MCP Inspector)
- [ ] Server outputs JSON-RPC messages to stdout
- [ ] Server logs diagnostic/debug info to stderr (not stdout)

### 7. Code Quality
- [ ] Implementation includes TypeScript type definitions (if TypeScript) OR Python type hints (if Python)
- [ ] All public functions have docstrings/comments explaining parameters and return values
- [ ] Project includes README with: installation steps, usage example, and how to run
- [ ] No hardcoded absolute paths (all paths relative to repo root or configurable)

---

## Verification Method

1. **Automated Test Suite**: Run `npm test` or equivalent → all tests pass
2. **Manual Verification**:
   - Start server: `npm start`
   - Use MCP Inspector to send `initialize` request → success
   - Call `code_search` with pattern `"NotificationManager"` → ≥5 results
   - Call `file_read` with path `"README.md"` → returns content
   - Send malformed request → returns error, server stays running
3. **Integration Test**: Connect server to Claude Desktop → both tools appear and are callable

---

## Pass/Fail Criteria

**PASS**: All checkboxes checked AND all test cases pass AND verification steps complete successfully

**FAIL**: Any checkbox unchecked OR any test case fails OR verification step fails OR server crashes during testing

---

## Out of Scope

The following are explicitly NOT required for this task:
- Authentication/authorization
- Persistent state or database
- Configuration file support (hardcoded defaults acceptable)
- Additional tools beyond code_search and file_read
- Performance optimization beyond stated requirements
- Deployment scripts or Docker containerization

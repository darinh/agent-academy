# 002 — Development Workflow

## Purpose

Defines the branching strategy, CI pipeline, versioning, git hooks, and PR workflow for Agent Academy. This spec governs how code moves from development to production.

## Current Behavior: Implemented

### Branching Strategy

| Branch | Purpose | Push Policy |
|--------|---------|-------------|
| `main` | Stable releases only | **Protected** — no direct pushes. Changes via PR only. |
| `develop` | Integration branch | PRs from feature branches merge here. |
| `feat/xxx`, `fix/xxx`, `docs/xxx` | Feature branches | Created off `develop`. PRs go to `develop`. |

**Flow:**
```
feat/my-feature → (PR) → develop → (PR) → main
```

- All work happens on feature branches.
- Feature branches are created from and merged back to `develop`.
- `develop` → `main` only via PR when ready for release.

### Conventional Commits

All commit messages must follow the [Conventional Commits](https://www.conventionalcommits.org/) specification. Enforced by the `.githooks/commit-msg` hook.

**Allowed prefixes:**

| Prefix | Use |
|--------|-----|
| `feat:` | A new feature |
| `fix:` | A bug fix |
| `docs:` | Documentation only changes |
| `refactor:` | Code change that neither fixes a bug nor adds a feature |
| `test:` | Adding or correcting tests |
| `ci:` | Changes to CI configuration files and scripts |
| `style:` | Code style changes (formatting, semicolons, etc.) |
| `perf:` | Performance improvements |
| `build:` | Changes to build system or external dependencies |
| `chore:` | Other changes that don't modify src or test files |

**Optional scope:** `feat(auth): add login endpoint`
**Breaking changes:** `feat!: redesign public API` or include `BREAKING CHANGE` in the commit body.

**Exceptions:** Merge commits (`Merge ...`), `fixup!`, and `squash!` are allowed.

### Git Hooks

Located in `.githooks/`. Activate after cloning:

```bash
git config core.hooksPath .githooks
```

| Hook | Purpose |
|------|---------|
| `commit-msg` | Validates conventional commit format |
| `pre-push` | Blocks direct pushes to `main` or `master` |

### CI Pipeline

Defined in `.github/workflows/ci.yml`. Runs on push to `develop`/`main` and on PRs targeting those branches.

**Jobs:**

| Step | Command |
|------|---------|
| Restore .NET | `dotnet restore` |
| Build .NET | `dotnet build --no-restore` |
| Test .NET | `dotnet test --no-build` |
| Install client deps | `npm ci` |
| Build client | `npm run build` |
| Test client | `npm test` |
| Typecheck client | `npx tsc --noEmit` |

### Versioning

Semantic Versioning (`major.minor.patch`), auto-bumped on merge to `main` via `.github/workflows/version-bump.yml`.

**Bump rules (based on conventional commits since last tag):**

| Commit Pattern | Bump |
|---------------|------|
| `BREAKING CHANGE` or `type!:` | **Major** |
| `feat:` | **Minor** |
| Everything else | **Patch** |

**Version sources:**
- .NET: `Directory.Build.props` → `<Version>` element
- Client: `src/agent-academy-client/package.json` → `version` field

### PR Workflow

Every PR uses the template at `.github/pull_request_template.md` and must include:

1. **Summary** — what the PR does.
2. **Spec Change Proposal** — affected spec sections, change type, whether the spec was updated.
3. **Version Impact** — patch, minor, or major.
4. **Checklist** — spec updated, tests pass, build succeeds, no direct main commits, conventional commits.

**Merge requirements:**
- CI must pass.
- At least one review required.
- Spec updated or confirmed N/A.

## Interfaces & Contracts

### Directory.Build.props

```xml
<Project>
  <PropertyGroup>
    <Version>0.1.0</Version>
  </PropertyGroup>
</Project>
```

Applied to all .NET projects in the solution via MSBuild import convention.

### Workflow Triggers

- `ci.yml`: `push` to `develop`/`main`, `pull_request` to `develop`/`main`
- `version-bump.yml`: `push` to `main` only

## Invariants

1. No code reaches `main` without passing CI.
2. No direct pushes to `main` — enforced by `pre-push` hook locally and should be enforced by GitHub branch protection.
3. All commits follow conventional commit format — enforced by `commit-msg` hook.
4. Version is auto-bumped on every merge to `main`.
5. `.NET` and client versions should stay in sync (both updated by version-bump workflow). **Note**: Currently `.NET` version is `0.1.0` and client `package.json` is `0.0.0` — will align on next version bump to `main`.

## Known Gaps

- ~~GitHub branch protection rules for `main` must be configured manually in the repository settings (require PR reviews, require status checks, disable force push).~~ — **Resolved**: `scripts/protect-branches.sh` configures protection via `gh api`.
- The `pre-push` hook is local only — contributors must run `git config core.hooksPath .githooks` after cloning. — *Accepted: documented in setup script and README*
- No automated changelog generation from conventional commits yet. — *Tracked in #10*

## Revision History

| Date | Change | Task |
|------|--------|------|
| 2025-07-27 | Initial spec | repo-conventions |

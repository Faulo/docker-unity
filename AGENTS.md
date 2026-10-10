# AGENTS.md

Shared instructions for coding agents. Project-specific information is kept in [README.md](README.md), read it before non-trivial changes.

## Docker

### Repository layout and Docker targets

This repository builds Linux and Windows variants of the same image. Shared build inputs belong in `common/`; platform-specific inputs belong in `linux/` or `windows/`. Both variants use the repository root as their build context.

Always select Docker daemons explicitly: use `--context linux` or `--context windows` for local work, and the named contexts required by the release cycle for remote validation. Treat the root `.env` as authoritative.

Build, tag, overwrite, or remove only images in the disposable `tmp/` namespace. Treat images in every other namespace as published artifacts.

### Command entry points

Add project `bin` directories to `PATH`, preserving existing entries. Use shims as shorthands for explicit subcommands: ShimGen executables on Windows and executable shell wrappers with `exec` on Linux. Support direct `docker exec` invocation.

### Implementation

Keep Dockerfile comments focused on non-obvious constraints and reasons. Preserve each file's shell conventions: PowerShell in Windows Dockerfiles, POSIX shell in Linux Dockerfiles, and batch in `.bat` files.

Prefer rolling updates of dependencies over pinned and checksummed versions. Preserve native exit-code checks, and explicit error handling.

Use the Idea MCP to inspect source files for warnings and optimizations. Lint the code.

Use the patterns in `C:\Webserver\dockerfile-templates` when applicable.

Update `README.md` whenever image contents, prerequisites, or public commands change.

### Build and validation

Builds can be large, network-dependent, and platform-specific. A Windows image requires a compatible Windows daemon. Run every applicable target, but do not conceal unavailable coverage: report each skipped target and the concrete reason it could not run.

### Release cycle

When the user has authorized the required release, Git, CI, and deployment operations, complete every phase in order.

#### Phase 1: Establish the design contract

1. If the release is a refactor or performance improvement, skip this phase.
2. Add or update the Pester integration coverage under `tests/` so it expresses the intended behavior.
3. Run the new coverage against the currently published image on both Garl and Dende with `pwsh ./.jenkins/Invoke-IntegrationTests.ps1 -Pull -Context <context>`, and inspect the complete output.
4. The new coverage must fail against the currently published image for the intended reason. If it passes, start this phase over with a strenghened contract.
5. Do not proceed on an infrastructure failure or unrelated regression; establish the expected product failure first.

#### Phase 2: Build and validate the candidate

1. Implement the change and run the applicable local tests. Update the documentation.
2. Build candidate images in the `tmp` namespace on Docker context `dende` for Windows and `garl` for Linux.
3. Run `pwsh ./.jenkins/Invoke-IntegrationTests.ps1 -Namespace tmp -Context <context>` for both contexts and inspect the complete output.
4. If the candidate fails, fix it, rebuild both candidate images, and repeat both integration runs.
5. Proceed only after the complete candidate integration run passes.

#### Phase 3: Publish and revalidate

1. Commit and push the implementation, then watch the complete GitHub CI image build.
2. If GitHub CI fails, fix the issue and revalidate the candidate from Phase 2 before pushing the correction.
3. After GitHub CI passes, run this image's Jenkins job and inspect the complete console log. Jenkins pulls and tests every configured published variant on Dende and Garl.
4. If any variant fails, fix the implementation, then test that specific variant with `pwsh ./.jenkins/Invoke-IntegrationTests.ps1 -Namespace tmp -Context <context> -Variant <variant>`, then start this phase over.
5. Publishing is complete when GitHub and Jenkins pass for all contexts and variants.
5. If the feature was based on a ticket, update the ticket's body to reflect the shipped design and mark it complete.

If the expected behavior or its test contract changes at any point, restart at Phase 1, step 1.

## General

### Meta commands

These short messages have special handling when they appear alone in a user message:

- `ping`: Reply with `pong`.
- `.`: Reply with `.`.
- `?`: Continue the previous response or task after an interruption.
- `ticket <URI>`: Read the linked ticket and all comments through the available integration. Inspect the project, reproduce the current behavior, and run relevant checks as needed. Then explain the request, project context, reproducibility, risks, and a proposed implementation plan. Do not edit files, change remote state, commit, or push until the user approves the approach.
- `can you <x>?` is a question about your knowledge, capabilities or permissions. It is not an instruction to perform `x`.

### Compatibility

Follow semantic versioning. Preserve backward compatibility for public APIs unless the task explicitly permits a breaking change.

### Project conventions

`.editorconfig` is authoritative. Never edit `.editorconfig` unless expressly instructed by the user.

### Git

Git mutations are forbidden by default. Agents may use read-only inspection commands such as `git status`, `git log`, `git diff`, `git show`, `git blame`, and `git branch --list` without additional permission.

An agent may perform Git mutations only after the user explicitly opts in. Permission is limited to the operations and task the user authorized; do not treat prior authorization as standing permission for later mutations.

When Git mutations are authorized:

- The user is responsible for choosing the branch. Verify the current branch and working-tree status before editing and again before creating commits.
- Treat all unknown local changes as user work. Do not overwrite, stage, commit, restore, or otherwise alter them.
- Keep commits small and cohesive.
- Format agent-authored commits according to Conventional Commits 1.0.0: `<type>[optional scope]: <description>`.
- When working from a ticket, include the ticket key and URL in the commit footer.
- Before committing, read the configured Git author name and email. Keep the configured email, append the agent name once, in brackets to the configured author name (e.g. `Daniel Schulz (Codex)`), and pass that identity explicitly with `git commit --author`. Do not modify repository or global Git configuration.
- Do not force-push, amend, rebase, reset, or discard changes unless the user explicitly requests that specific operation.

### CI Infrastructure

Jenkins runs at `https://ci.slothsoft.net/`. Infrastructure reads are always permitted; triggering builds or making any other change requires explicit user consent.

Use these access methods:

- `ssh ci.slothsoft.net <command>` invokes the Jenkins CLI directly; it does not open a host shell. Useful read-only commands include `who-am-i`, `list-jobs`, and `console`.
- `ssh <host>` opens that server's shell.
- `docker --context <host> ...` talks to that server's Docker daemon. Always name the context explicitly.

The three CI servers are:

- `groke`: Ubuntu; runs the Jenkins controller and a Jenkins agent in the `agents_jenkins-agent` container. SSH alias and Docker context: `groke`.
- `garl`: Ubuntu; runs a Jenkins agent in the `agents_jenkins-agent` container, connected to the controller on `groke`. SSH alias and Docker context: `garl`.
- `dende`: Windows Server 2019; runs a Jenkins agent in the `agents_jenkins-agent` container, connected to the controller on `groke`. SSH alias and Docker context: `dende`.

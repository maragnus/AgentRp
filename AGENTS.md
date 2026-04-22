## Design Stage

This system is still being designed and has not been deployed. You are explicitly allowed to make breaking, sweeping, and structural changes at any layer of the codebase. Do not preserve a weak design just because it already exists.

If a new requirement exposes that the current implementation is incomplete, stop and redesign the affected area so the requirement is handled as a foundational part of the architecture. Do not add narrow patches, special cases, compatibility shims, or temporary-looking glue code unless explicitly requested.

The final result should look like we understood this requirement from day one. Favor coherent reconstruction over incremental duct-taping.

## DRY - Don't Repeat Yourself
Treat duplication as a maintenance risk, not just a style issue.

Before adding new code, check whether the same behavior or pattern already exists in the current project, related projects, or shared libraries. Reuse or extend existing implementations when they are a reasonable fit.

Prioritize reuse for high-value duplication risks:
- shared UI and Razor markup,
- normalization, validation, parsing, and mapping,
- cross-project business rules,
- infrastructure and integration patterns.

When similar logic appears in multiple places, extract it into an appropriately named shared component, service, helper, or library.

Do not force abstractions for one-off or highly local code. Prefer clarity over speculative reuse.

Apply DRY at the small scale too. Avoid repeating literals, conditionals, or long call chains when a local variable, small helper, or shared expression would make the code clearer.

If you choose not to reuse a similar existing implementation, briefly state why.

## Non-Negotiable Rules
- ALWAYS answer user questions before making code changes.
- ALWAYS ask before removing or degrading user-facing behavior. NEVER assume feature removal is acceptable.
- ALWAYS ask when requirements are ambiguous or you are uncertain.
- ALWAYS treat assumptions as dangerous, especially for behavior, configuration, model/runtime capabilities, and product defaults. If a decision could reasonably belong in configuration or materially change behavior, ask or make it explicitly configurable instead of hard-coding the assumption.
- ALWAYS fix root causes. NEVER patch symptoms.
- ALWAYS write code to be testable, even if no tests exist.
- ALWAYS use Bootstrap UI components, classes, and styles.
- ALWAYS try to avoid custom CSS classes, custom CSS styling, component CSS, and inline styles in Blazor UI. In the event custom CSS is needed, use `app.css` so that it is globally accessible and easy to maintain.
- For async user-action buttons, use a `BusyButton` component instead of hand-rolled button busy state. A `BusyButton` must disable itself while work is running and show a Bootstrap spinner so users can see that the action is in progress.
- For async server/network/background actions, use a toast feedback system for completion feedback. Success and failure should both be visible unless the action is a trivial local-only UI toggle.
- User-facing errors must explain what step failed and what item or operation was being processed. Preserve the technical cause as an inner exception for logs instead of surfacing raw low-level exceptions directly to users.
- When working through a bug, if the root cause is unclear, add detailed debugging output and ask the user to reproduce. Don't put in failsafe checks down the line until the user confirms that the root cause search has been exhausted.
- Do not spend effort on migrations or backwards compatibility unless the user explicitly asks for them.
- For Codex running in WSL for this repo, prefer the normal WSL home directory, default global NuGet cache, and standard OS cache/temp directories for transient build and test state. Do not mirror NuGet packages into repo-local `artifacts` unless the user explicitly asks for a repo-local cache.
- NEVER manually edit `.csproj`, `.sln`, `.slnx`, or `Directory.Packages.props`. MUST use `dotnet` CLI commands.
- NEVER manually edit project/package references. MUST use `dotnet add ...` and `dotnet sln add ...`.
- NEVER use `UriKind.Absolute` or `Uri.TryCreate(..., UriKind.Absolute, ...)` to validate an absolute URL. ONLY use `StartsWith("https://")` or `StartsWith("http://")` with case-insensitive comparison.
- Breaking changes are acceptable. Prefer the correct design over migrations, compatibility shims, or shoehorned extensions.

## Coding Standards
- Use C# 12 primary constructors.
- Do NOT use `ConfigureAwait(false)`.
- Trust null annotations. If a type is not annotated nullable, do NOT add null checks for it.
- Single-statement `if`, `else`, `for`, `foreach`, and `while` bodies MUST NOT use braces. Use braces only for multi-statement blocks.
- Keep code DRY, KISS, and SOLID. Aim for native Microsoft-level quality.
- When requirements are unclear, stop and ask. ALWAYS ask when requirements are ambiguous.
- When working with Markdown, use link syntax with meaningful titles instead of inline code when referencing other files.
- Always use generic `GetResponseAsync<T>(...)` for any agent call that expects JSON or a typed DTO.
- Never call non-generic `GetResponseAsync(...)` and then parse `response.Text` into DTOs or JSON documents.
- Non-generic `GetResponseAsync(...)` and streaming APIs are allowed only for intentional prose output, never for structured outputs.

## Design, UI/UX
- FontAwesome Pro 7 CSS with Classic Regular is available and should be used in the UI. Example: `<i class="fa-regular fa-check"></i>`
- Bootstrap 5.3 should be used for all UI components.
- Blazor wrapper components are encouraged to keep the code base DRY and maintainable.
- Prefer flatter dialog and card layouts. Do not stack bordered boxes, cards, alerts, and rounded panels inside each other when headings, spacing, separators, and standard Bootstrap form layout already communicate the structure.
- Avoid excessive verbosity and repetition in the UI/UX, it should feel clean, compact
- Buttons do not need text labels when the icon is already sufficiently clear. Prefer compact icon-only buttons in those cases, but always provide accessible labels/tooltips.
- Do not show filler, placeholder, or duplicate user-facing text in progress, status, or detail UI. Expanded content must add new information beyond the collapsed summary.
- Do not add redundant indicators when selection state, active styling, or layout already makes the current item obvious.
- Display simple representations of complex things but enable access to details through accordians, popups, and/or dedicated detail pages.
  Example: A task list may be a checklist where the active items display real-time status, but can be expanded to display more details. And a the task list itself should have a full detail page that examples all steps in full detail.

## Required Commands
Use these command patterns. NEVER hand-edit project or package metadata files.

```bash
dotnet new classlib -n ProjectName
dotnet new razorclasslib -n ProjectName
dotnet new xunit -n ProjectName

dotnet sln add Path/To/Project.csproj

dotnet add ProjectPath package PackageName
dotnet add ProjectPath reference OtherProject.csproj
```

## Test Workflow

Only write tests when uncertain if a solution will work reliably. This is not a production tool, so tests are not required.

## Preferences

- Dates should be displayed to users in shorthand with a duration "Mar 4, 2026 (25 days ago)", it can also user "(today)" or "(yesterday)" for very recent dates. Future dates are possible and should use "(in 10 days)" and "(tomorrow)".

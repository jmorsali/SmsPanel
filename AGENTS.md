# Leasing backend: agent guide

Paths are relative to the repository root. Preserve unrelated local work and keep changes limited to the request.

## Project map

- Stack: .NET 9, ASP.NET Core, EF Core, and SQL Server; solution: `leasing.backoffice.sln`.
- `src/Core.Domain/`: entities, DTOs, enums, models, resources, and extensions.
- `src/Infrastructures/`: application services, repositories, integrations, persistence, configurations, and migrations.
- `src/Endpoints.API/`: controllers, startup in `Program.cs`, dependency registration, configuration, views, and document assets.
- Main persistence contexts: `MyDbContext` and `RahkaranDbContext`.
- External boundaries: Identity, Leasing frontend, SharedKernel, Rahkaran, credit validation, Redis, messaging, and file storage.

## Task-relevant guidance

Before editing, inspect current local changes, affected code, nearby tests, and the applicable documents:

- Any production-code change: `docs/development-conventions.md`.
- Domain language, system boundaries, or high-level Loan lifecycle: `docs/business-overview.md`.
- Loan creation/editing, products, installments, guarantors, collateral, documents, or submit behavior: `docs/loan-registration.md`.
- Approval, rejection, resubmission, expiration, or contract conversion: `docs/loan-approval-workflow.md`.
- Roles, route permissions, Shop hierarchy, list filtering, or object/data-scope access: `docs/access-control.md`.
- Identity, credit validation, Redis, files, messaging, or other integrations: `docs/integrations.md`.
- Policy listing/availability: `docs/policy-list.md`.
- Rahkaran accounting vouchers: `docs/rahkaran-voucher.md`.
- Areas where business rules and current enforcement differ: `docs/implementation-notes.md`.
- Persisted schema or migration-managed data: `docs/database-migrations.md`.
- Test selection, setup, and isolation: `tests/README.md`.

Follow the controller/service/repository/DTO/entity/configuration/test path relevant to the change. If code and documentation materially disagree, establish the intended rule instead of guessing. Assess contract impact on Identity, the frontend, SharedKernel, and other consumers; edit another project only when it is in scope.

## Implementation and finalization

A work session may span multiple prompts. During implementation, edit only production/source files required by the request. Reading docs/tests and running existing targeted checks is allowed; defer test-source and Markdown edits to finalization. Documentation-only, rules-only, and tests-only tasks may edit their requested files immediately.

When production behavior appears implemented, ask exactly: **«آیا کار به‌درستی انجام شده است و بررسی نهایی را شروع کنیم؟»** Wait for confirmation before finalization; a clear confirmation opens finalization without another approval.

During finalization:

1. Review the complete task diff for correctness, validation, authorization/data scope, state transitions, concurrency, transactions, nullability, privacy, and compatibility.
2. Add/update applicable success, validation, denied-access, and workflow tests. Keep tests deterministic and isolated from uncontrolled external services.
3. Identify every user or system flow affected by the task. For each affected domain, update its existing
   `docs/<domain>/` documents. If the domain directory does not exist, create it with a `README.md`
   entry point and focused Mermaid documents covering the relevant sources/actors, lifecycle or sequence,
   side effects, failure/rollback paths, and known gaps. Do not create empty domain folders or diagrams for
   behavior outside the task's scope.
4. Complete required migrations, SQL, permission updates, canonical documentation, and in-scope client contracts.
5. Run relevant builds/tests; separate introduced failures from existing/environmental failures.
6. Re-review code, tests, SQL/scripts, canonical docs, and flow diagrams together; fix introduced issues and
   report residual risks or unverified checks.

Do not perform unrelated cleanup, broad formatting, package upgrades, broad renames, speculative refactors, or schema changes outside the requested behavior.

## Domain flow diagrams and behavioral documentation

- `docs/README.md` is the documentation map. Cross-layer user and system flows live directly under
  domain directories such as `docs/cheque/`. Follow `docs/flow-conventions.md` when adding or changing a diagram.
- Write all flow content under `docs/<domain>/` in English, including headings, prose, tables, Mermaid labels,
  notes, and legends. Preserve exact domain identifiers and user-facing contract text only when quoting
  them is necessary to match the implementation.
- For any behavior change, check whether it changes an actor, entry point, precondition, status transition,
  side effect, transaction boundary, failure path, authorization rule, frontend/API contract, integration,
  or terminal outcome represented in a domain flow document.
- During finalization, map the task to one or more `docs/<domain>/` directories. Update every affected
  document when the domain exists. When it does not exist, create the domain, its `README.md`, and only the
  focused diagrams needed to explain the task's implemented flow. Add the new domain to `docs/README.md`.
- If a task does not change any flow, verify that relevant existing diagrams still describe the implementation
  and record that conclusion in the final report; do not update timestamps or formatting merely to create churn.
- Diagrams must describe implemented behavior as `Current`. Clearly mark unimplemented designs as `Proposed`
  and code/test/document disagreements as `Gap`; never present an intended design as current behavior.
- Keep detailed business rules in their canonical domain document and link to it from flow documents instead
  of duplicating long rule descriptions.
- Keep Mermaid source inline in Markdown. Split dense diagrams by actor, entity type, or lifecycle phase rather
  than producing one large diagram.
- Every transition arrow must correspond to a real UI action, endpoint, workflow decision, integration event,
  or scheduled process. Record relevant source and test paths without unstable line numbers.
- Show material side effects and atomicity explicitly, including history, files, accounting, payments, messages,
  external calls, and rollback behavior.
- Review code, tests, API/client contracts, canonical docs, and flow diagrams together before declaring a
  behavioral task complete.

## Bounded investigation

For unusually costly work such as a full-repository audit, broad cross-project analysis/refactor, exhaustive review, or large document/log investigation, first make a small read-only scope assessment. Explain the scope, offer a cheaper alternative and its tradeoff, and obtain explicit confirmation. Prior authorization that acknowledges the cost is sufficient; reconfirm only a material expansion. Ordinary focused work does not need this gate.

## Git and GitLab: read-only

Git and GitLab are read-only. Allowed Git operations include status, diff, log, show, grep, and ls-files. Do not use Git to modify the worktree, index, refs, configuration, remotes, submodules, or hooks; this includes add, commit, fetch/pull/push, checkout/switch/restore/reset/clean, merge/rebase/cherry-pick/revert, stash, tag, and branch mutations.

Do not mutate GitLab merge requests, comments, approvals, issues, labels, branches, tags, releases, repository files, variables, or pipelines. Do not bypass these restrictions through another CLI, API, script, library, direct `.git` edit, or UI automation. Leave changes ready for a human.

## Repository-wide safeguards

- Preserve established layer boundaries and existing abstractions. New files must resemble the closest files in the same feature/layer.
- Repository methods must return records of the repository's own entity type, optionally loaded with its parent, child, or other mapped relations. Do not project feature-specific DTOs or compose unrelated entity queries in repositories; perform validation, aggregation, customization, and DTO mapping in the application-service layer.
- Treat DTOs, enums, permissions, events, and inter-service responses as contracts; preserve compatibility unless affected consumers are coordinated.
- Enums normally express domain-level concepts. Do not create separate DTO-specific enums for the same concept; use the applicable domain enum in request and response DTOs.
- Define a dedicated request DTO and response DTO for each API operation. Do not reuse generic request or response DTOs across APIs.
- Identify DTOs explicitly by using the `Request` or `Response` prefix in their class names, according to their API role.
- Route permission and object/data-scope access are separate. API route/authorization changes require the review defined in `docs/access-control.md`.
- Each agent is responsible for reflecting only the schema or data changes introduced by its own task in a new migration and corresponding migration script. Never modify previously created migrations or their scripts.
- Never expose real secrets, tokens, personal identifiers, phone numbers, financial-report contents, or other personal data in logs, docs, fixtures, snapshots, or errors. Tests may use clearly synthetic values.
- Never apply migrations or execute SQL against an ambiguous, remote, shared, development-server, stage, test-server, UAT, or production database. File/SQL generation is not authorization to apply it.
- Tests must not call production or uncontrolled external services. Do not invent business rules or silently resolve material code/documentation conflicts.

# Workflow schema (stub)

> Status: placeholder written in Phase 2 (scaffold). Phase 3 defines schema v1 here and ships
> the loader and validator in `src/OG_opossum_command.Core/Workflows`.

Workflow definitions are hand-editable JSON files (ADR-008) stored under the data root's
`workflows/` folder (see `AppPaths.WorkflowsDirectory`). Examples live in
`examples/workflows/work` and `examples/workflows/personal`.

What Phase 3 will specify:

- `schemaVersion` (integer, starts at 1) and how unknown versions are rejected.
- Identity: `id`, `name`, `profileId` (`work` or `personal`; never both).
- `trigger`: manual first; schedules and events follow in later phases.
- `steps`: ordered list, each with `id`, `action`, structured `inputs` (never shell strings),
  `timeoutSeconds`, `retry`, `dependsOn`, `onFailure`.
- `requiresApproval`: reserved flag for a future AI-assisted mode (ADR-013); always `false` in v1.
- Validation rules and the exact wording of validation errors shown to the user.

The illustrative example in `docs/ARCHITECTURE.md` section 07 is the starting point, not a contract.

# Example workflows

One folder per profile. Files are workflow definitions in the JSON format documented in
`docs/WORKFLOW-SCHEMA.md` (Phase 3). The build never runs these for real; tests load and validate
them with fake launchers only.

- `work/` belongs to the work profile and may reference only work destinations.
- `personal/` belongs to the personal profile and may reference only personal destinations.

A workflow file never belongs to both profiles (ADR-012).

// Shared by every project through Directory.Build.props, which makes it the FIRST compile item.
//
// CA1707 (identifiers should not contain underscores) flags the code name OG_opossum_command twice:
// once for the assembly name and once for the root namespace. Roslyn reports both at the first
// location it knows: the first token of the first syntax tree for the assembly, and the first
// declaration of the namespace. Both land in this file, so the pragma below is the only place
// CA1707 is disabled; every other identifier in the solution is still checked (rule 0, ADR-018).
// If compile-item order ever changes, the diagnostics reappear elsewhere and the build fails
// loudly rather than widening the suppression.

#pragma warning disable CA1707 // Identifiers should not contain underscores: fixed code name (ADR-000)

namespace OG_opossum_command
{
    // Intentionally empty: anchors the first declaration of the root namespace inside the pragma.
}

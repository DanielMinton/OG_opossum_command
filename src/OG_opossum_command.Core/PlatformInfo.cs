// Core stays a plain net10.0 library so it is unit-testable headlessly, but the app is Windows-only and the
// single-instance lock uses named kernel objects that CA1416 marks as Windows-only. Declaring the platform here
// (ADR-036) keeps the analyzer honest without pulling the WindowsDesktop framework into Core.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

# Repository instructions for coding agents

Keep changes focused on the approved issue and follow the existing project structure and conventions.

## Verification before completion

Before considering an implementation complete:

- Run `dotnet build HomeOps.sln` from the repository root.
- Fix any compilation errors introduced by the change.
- Run relevant existing tests when the affected area has tests that are quick and non-interactive.
- Do not add broad or elaborate test infrastructure solely to satisfy this verification step unless the issue requires it.

Do not start long-running services or applications only for verification. Runtime, integration, browser, Docker, simulator, or hardware checks may be left to AgentController's configured verification or manual verification unless the issue explicitly requires them.

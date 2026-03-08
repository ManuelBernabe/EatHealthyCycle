---
paths:
  - "src/**/*.cs"
---

## C# Conventions

- **Minimal APIs** in `Program.cs` — no controllers, endpoints defined inline with `app.MapGet/MapPost`
- **EF Core** with SQLite — migrations in `src/{ProjectName}.Infrastructure/Migrations/`
- **Auth**: JWT tokens + refresh tokens, BCrypt for password hashing (via `BCrypt.Net-Next`)
- Repositories implement interfaces, services registered via DI in `Program.cs`
- Use `async/await` for all I/O operations
- Prefer `StringComparison.OrdinalIgnoreCase` for string comparisons
- Use `JsonSerializer` from `System.Text.Json` (not Newtonsoft)

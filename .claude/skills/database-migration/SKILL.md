---
name: database-migration
description: Create a database migration for schema changes
disable-model-invocation: true
argument-hint: "MigrationName (e.g., AddUserFields)"
---

# Create Database Migration

## For EF Core (.NET)
1. Ensure model changes are in `src/{Project}.Core/Models/` and configured in DbContext
2. Run:
   ```bash
   dotnet ef migrations add $ARGUMENTS --project src/{Project}.Infrastructure --startup-project src/{Project}.Api
   ```
3. Review the generated migration file
4. Build to verify: `dotnet build`

## For Alembic (Python)
1. Ensure model changes are in `models/`
2. Run: `alembic revision --autogenerate -m "$ARGUMENTS"`
3. Review the generated migration in `alembic/versions/`
4. Test: `alembic upgrade head`

## For Prisma (Node.js)
1. Update `prisma/schema.prisma`
2. Run: `npx prisma migrate dev --name $ARGUMENTS`
3. Review the generated SQL in `prisma/migrations/`

## Important
- Migration name should be PascalCase/snake_case and descriptive
- Always review generated Up/Down methods
- SQLite has limited ALTER TABLE support

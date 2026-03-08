---
name: run-tests
description: Run the project test suite and report results
disable-model-invocation: true
argument-hint: "optional: TestName or TestProject"
---

# Run Tests

## Steps

1. Detect the project type and run the appropriate command:

   **For .NET**: `dotnet test {SolutionName}.sln`
   - Single project: `dotnet test tests/{Project}.Tests`
   - Specific test: `dotnet test --filter "FullyQualifiedName~$ARGUMENTS"`

   **For Node.js**: `npm test` or `npx vitest`
   - Specific file: `npx vitest $ARGUMENTS`

   **For Python**: `pytest`
   - Specific file: `pytest $ARGUMENTS`
   - With coverage: `pytest --cov`

2. Report: total tests, passed, failed, skipped
3. If any tests fail, show the failure details and suggest fixes

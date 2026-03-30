# CLAUDE.md

This file provides guidance to Claude Code when working with code in this repository.

## Build & Run Commands

```bash
# Build
{BUILD_COMMAND}

# Run
{RUN_COMMAND}

# Tests
{TEST_COMMAND}
```

## Architecture

{BRIEF_DESCRIPTION}

**Projects/Directories:**
- **{Module1}** (`path/`) — Description.
- **{Module2}** (`path/`) — Description.

## API Endpoints (if applicable)

- `/auth/*` — Authentication
- `/api/*` — Main API routes

## Deployment Rules

- **Version bump required on every deploy**: Before committing changes that will be deployed, always increment the `AppVersion` constant in `Controllers/VersionController.cs`. Use semantic versioning (MAJOR.MINOR.PATCH). Increment PATCH for fixes, MINOR for new features, MAJOR for breaking changes.

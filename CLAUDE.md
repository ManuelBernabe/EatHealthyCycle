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

- **Version bump required on every deploy**: Before committing changes that will be deployed, always increment:
  1. `AppVersion` in `Controllers/VersionController.cs` — semantic versioning (PATCH for fixes, MINOR for features, MAJOR for breaking).
  2. `CACHE` version in `wwwroot/sw.js` (e.g. `eatcycle-v14` → `eatcycle-v15`) — forces the Service Worker to reinstall and clear old cached files so users get the new version.

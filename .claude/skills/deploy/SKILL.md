---
name: deploy
description: Deploy the application to production
disable-model-invocation: true
argument-hint: "optional: environment (staging/production)"
---

# Deploy Application

## Steps

1. Ensure all changes are committed: `git status`
2. Run tests to verify nothing is broken
3. Push to the appropriate branch:

   **Default flow (develop → main)**:
   - If on `develop`: `git push origin develop`
   - Wait for user approval, then merge to main: `git checkout main && git merge develop && git push origin main`

   **Direct deploy**:
   - `git push origin main`

4. Report the deploy status

## Important
- Never force push to main
- Always run tests before deploying
- Check git status for uncommitted changes first

---
paths:
  - "frontend/src/**/*.tsx"
  - "frontend/src/**/*.ts"
  - "frontend/src/**/*.css"
  - "src/**/*.tsx"
  - "src/**/*.ts"
---

## Frontend Conventions

- **React 18+** with TypeScript and **Vite** (or Next.js)
- **React Router v6** for client-side routing
- **State management**: React Context / Zustand / Redux Toolkit (pick one)
- **API calls**: Custom hook or service layer (e.g., `useApi`, `api.ts`)
- **Auth**: AuthContext or auth provider pattern
- Pages in `src/pages/`, reusable components in `src/components/`
- Prefer functional components with hooks over class components
- Use `interface` for props, `type` for unions/intersections

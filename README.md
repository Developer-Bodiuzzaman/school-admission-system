# Full-stack starter

A Docker Compose development stack with a .NET 8 Web API, React 18 + Vite, and
PostgreSQL 16.

## Run the stack

Install Docker Desktop, then from the repository root run:

```powershell
Copy-Item .env.example .env
# Set ADMIN_EMAIL, ADMIN_PASSWORD, and ADMIN_API_KEY in .env before starting.
docker compose up --build
```

Open the frontend at [http://localhost:5173](http://localhost:5173). The API is
available at [http://localhost:8080/api/health](http://localhost:8080/api/health)
and PostgreSQL is exposed on port `5432`.

The health endpoint verifies its PostgreSQL connection. Compose waits for the
database health check before starting the API. PostgreSQL data is stored in the
`postgres_data` named volume.

The default database credentials in Compose are for local development only.
Override `POSTGRES_DB`, `POSTGRES_USER`, and `POSTGRES_PASSWORD` in the
environment before starting the stack when needed. Do not use the development
defaults in a deployed environment.

The admin login uses the app-specific `ADMIN_EMAIL` and `ADMIN_PASSWORD`
configured in `.env`; it does not use Google OAuth. The local development
account is configured in your private `.env` file. After login, the browser
receives an HTTP-only, eight-hour admin session cookie. The admin dashboard
shows only active seat holds and confirmed admissions, grouped by class.
Keep `.env` private; it is excluded from Git. Use a unique, stronger password
before exposing the app outside local development.

The browser dashboard uses the cookie-authenticated `GET /admin/overview`
endpoint. For scripts, `GET /admin/applications` still accepts the
`ADMIN_API_KEY` from `.env` in the `X-Admin-Key` request header:

```powershell
Invoke-RestMethod `
  -Uri http://localhost:8080/admin/applications `
  -Headers @{ "X-Admin-Key" = $adminKey }
```

The endpoint lists active holds and confirmed admissions, including student
registration IDs. Keep `.env` private; it is excluded from Git.

## Project layout

```text
backend/
  src/
    Api/
      Api.csproj
      Dockerfile
      Program.cs
frontend/
  src/
    App.jsx
    main.jsx
  styles.css
  Dockerfile
  index.html
  package-lock.json
  package.json
  vite.config.js
docker-compose.yml
```

The frontend calls the backend at `http://localhost:8080`.

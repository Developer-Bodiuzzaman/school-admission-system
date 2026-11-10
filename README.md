# School Admission System

A web application for managing school admissions. Students can browse class
seat availability, place a temporary seat hold, and confirm or cancel an
application. Administrators can create classes and review applications.

## Run locally

Install Docker Desktop, then run this command from the project folder:

```powershell
docker compose up --build
```

Open the student portal at [http://localhost:5173](http://localhost:5173).
Swagger UI is at [http://localhost:8080](http://localhost:8080), and the API
health check is at [http://localhost:8080/api/health](http://localhost:8080/api/health).

Stop the services with:

```powershell
docker compose down
```

Admission and class data is stored in the `postgres_data` Docker volume.
`docker compose down` preserves the volume; do not add `-v` unless you intend
to remove the data.

## Admin access

For local development without a `.env` file, sign in with
`admin@example.com` and `local-development-password`. These defaults are for
local use only. To override them, copy `.env.example` to `.env` and set strong,
private values for the database and admin credentials before starting Docker.
Never commit `.env` or use the local defaults in a deployed environment.

The admin dashboard lets school staff create classes and review active holds
and confirmed admissions. Students identify themselves with their 17-digit
Birth Registration ID; the portal remembers it in browser local storage.

Set `HOLD_DURATION_SECONDS` to change the hold timeout (default: 600 seconds)
and `VITE_API_BASE_URL` to change the API URL embedded in the frontend build.

## Tests

Run the backend integration tests (Docker must be running for their temporary
PostgreSQL database):

```powershell
dotnet test backend\tests\Api.Tests\Api.Tests.csproj
```

Run frontend checks:

```powershell
Push-Location frontend
npm ci
npm run lint
npm run build
Pop-Location
```

## Project structure

- `frontend/` - React interface for students and administrators
- `backend/src/Api/` - .NET Web API
- `backend/tests/Api.Tests/` - backend integration tests
- `docker-compose.yml` - frontend, API, and PostgreSQL services

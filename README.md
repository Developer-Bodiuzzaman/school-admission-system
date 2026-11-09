# School Admission System

A web application for managing school class admissions. Students can check seat
availability, place a temporary seat hold, and confirm an admission. Admins can
review active holds and confirmed applications by class.

## Run locally

Start Docker Desktop, then run these commands from the project folder:

```powershell
docker compose up --build -d
```

Open the student portal at [http://localhost:5173](http://localhost:5173).
The API health check is at
[http://localhost:8080/api/health](http://localhost:8080/api/health).

To stop the services:

```powershell
docker compose down
```

Admission and class data is stored in the `postgres_data` Docker volume.

## Admin access

Set `ADMIN_EMAIL`, `ADMIN_PASSWORD`, and `ADMIN_API_KEY` in the private `.env`
file before starting the services. The admin dashboard uses the email and
password to sign in. Keep `.env` private and use a strong password.

The browser dashboard shows active seat holds and confirmed admissions. The
`GET /admin/applications` API endpoint can also be used with the `ADMIN_API_KEY`
in the `X-Admin-Key` request header.

## Project structure

- `frontend/` - React interface for students and administrators
- `backend/src/Api/` - .NET Web API
- `docker-compose.yml` - frontend, API, and PostgreSQL services

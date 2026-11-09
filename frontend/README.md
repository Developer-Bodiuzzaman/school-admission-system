# Student and Admin Portal

The React app provides a student admission page and a class-wise admin
applications dashboard. It is served by Nginx in Docker Compose.

## Development

From this directory, install dependencies and start Vite:

```powershell
npm ci
npm run dev
```

The frontend expects the API at `http://localhost:8080`.

Run the frontend checks with:

```powershell
npm run lint
npm run build
```

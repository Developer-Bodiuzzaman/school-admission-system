# AI usage

Copilot SDK in VS Code assisted with reviewing the assignment, implementing the
API and UI changes, and running build, lint, and integration checks.

The initial implementation was incomplete against the full brief: it omitted
the class/hold detail endpoints and the administrator class-creation UI, used
the wrong Compose content, and did not include backend integration tests.
These gaps were found by comparing the implementation directly with the
assignment PDF and validating the Compose configuration. The final changes
were checked with the backend integration tests, frontend lint/build, and
`docker compose config`.

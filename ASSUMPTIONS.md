# Assumptions

- Each class/shift combination is represented by a separate class with its own
  seat capacity, as required by the assignment.
- A student ID is exactly 17 numeric digits. The student portal stores the ID
  in browser local storage so the student can see their applications after
  refreshing or reopening the browser.
- Student routes accept the documented `X-Student-Id` header. The create-hold
  route also accepts `studentBirthRegistrationId` in its JSON body so it can be
  called directly from Swagger UI.
- An unexpired hold occupies a seat. Expiration is determined from the
  persisted database timestamp, so expired holds release capacity even when no
  student is currently using the app.
- A repeated hold request returns the student's existing active hold; a
  confirmed application cannot be duplicated. Confirming an already confirmed
  hold is idempotent.
- Pressing “Confirm admission” simulates successful payment; no payment
  provider is integrated.
- Administrators sign in with the configured email and password. Database and
  application data persist in the Compose-managed PostgreSQL volume.

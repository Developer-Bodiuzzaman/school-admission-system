using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("The DefaultConnection connection string is required.");

var adminApiKey = builder.Configuration["Admin:ApiKey"];
if (string.IsNullOrWhiteSpace(adminApiKey))
{
    throw new InvalidOperationException("The Admin:ApiKey configuration value is required.");
}

var adminEmail = builder.Configuration["Admin:Email"];
var adminPassword = builder.Configuration["Admin:Password"];
if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
{
    throw new InvalidOperationException("The Admin:Email and Admin:Password configuration values are required.");
}

var holdDurationSeconds = builder.Configuration.GetValue<int>("HOLD_DURATION_SECONDS", 600);
if (holdDurationSeconds <= 0)
{
    throw new InvalidOperationException("HOLD_DURATION_SECONDS must be greater than zero.");
}

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "school-admission.admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

await using (var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
await using (var command = new NpgsqlCommand(
    """
    CREATE TABLE IF NOT EXISTS classes (
        id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        name text NOT NULL UNIQUE,
        total_seats integer NOT NULL CHECK (total_seats >= 0)
    )
    """,
    connection))
{
    await command.ExecuteNonQueryAsync();
}

await using (var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
await using (var command = new NpgsqlCommand(
    """
    CREATE TABLE IF NOT EXISTS holds (
        id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        class_id integer NOT NULL REFERENCES classes(id) ON DELETE CASCADE,
        student_id varchar(17) NOT NULL,
        status text NOT NULL CHECK (status IN ('HELD', 'CONFIRMED', 'CANCELLED', 'EXPIRED')),
        expires_at timestamptz NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now()
    );
    CREATE UNIQUE INDEX IF NOT EXISTS holds_one_active_per_student_class
        ON holds (class_id, student_id)
        WHERE status IN ('HELD', 'CONFIRMED');
    CREATE INDEX IF NOT EXISTS holds_class_status_expiry
        ON holds (class_id, status, expires_at);
    CREATE INDEX IF NOT EXISTS holds_student_created
        ON holds (student_id, created_at DESC)
    """,
    connection))
{
    await command.ExecuteNonQueryAsync();
}

app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "School Admission API V1");
    c.RoutePrefix = string.Empty;
});

app.MapGet("/api/health", async (NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
{
    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using var command = new NpgsqlCommand("SELECT 1", connection);
    await command.ExecuteScalarAsync(cancellationToken);

    return Results.Ok(new { status = "ok", database = "connected" });
});

app.MapPost("/admin/login", async (AdminLoginRequest request, HttpContext context) =>
{
    var emailMatches = string.Equals(request.Email?.Trim(), adminEmail, StringComparison.OrdinalIgnoreCase);
    var passwordMatches = FixedTimeEqualsText(request.Password ?? string.Empty, adminPassword);
    if (!emailMatches || !passwordMatches)
    {
        return Results.Unauthorized();
    }

    var claims = new[]
    {
        new Claim(ClaimTypes.Name, adminEmail),
        new Claim(ClaimTypes.Role, "Admin")
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));

    return Results.Ok(new { email = adminEmail });
});

app.MapGet("/admin/session", (ClaimsPrincipal user) =>
    Results.Ok(new { email = user.Identity?.Name }))
    .RequireAuthorization();

app.MapPost("/admin/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
})
    .RequireAuthorization();

app.MapGet("/admin/applications", async (
    HttpContext context,
    NpgsqlDataSource dataSource,
    CancellationToken cancellationToken) =>
{
    var providedApiKey = context.Request.Headers["X-Admin-Key"].ToString();
    if (!FixedTimeEqualsText(providedApiKey, adminApiKey))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(await LoadAdminApplicationsAsync(dataSource, cancellationToken));
});

app.MapGet("/admin/overview", async (
    NpgsqlDataSource dataSource,
    CancellationToken cancellationToken) =>
    Results.Ok(await LoadAdminApplicationsAsync(dataSource, cancellationToken)))
    .RequireAuthorization();

app.MapGet("/classes", async (NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
{
    var classes = new List<ClassSummary>();
    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using var command = new NpgsqlCommand(
        """
        SELECT c.id, c.name, c.total_seats,
            COUNT(h.id) FILTER (WHERE h.status = 'HELD' AND h.expires_at > now())::integer AS held_seats,
            COUNT(h.id) FILTER (WHERE h.status = 'CONFIRMED')::integer AS confirmed_seats
        FROM classes c
        LEFT JOIN holds h ON h.class_id = c.id
        GROUP BY c.id
        ORDER BY c.id
        """,
        connection);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);

    while (await reader.ReadAsync(cancellationToken))
    {
        var totalSeats = reader.GetInt32(2);
        var heldSeats = reader.GetInt32(3);
        var confirmedSeats = reader.GetInt32(4);
        classes.Add(new ClassSummary(
            reader.GetInt32(0),
            reader.GetString(1),
            totalSeats,
            heldSeats,
            confirmedSeats,
            Math.Max(0, totalSeats - heldSeats - confirmedSeats)));
    }

    return Results.Ok(classes);
});

app.MapGet("/classes/{classId:int}", async (
    int classId,
    NpgsqlDataSource dataSource,
    CancellationToken cancellationToken) =>
{
    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using var command = new NpgsqlCommand(
        """
        SELECT c.id, c.name, c.total_seats,
            COUNT(h.id) FILTER (WHERE h.status = 'HELD' AND h.expires_at > now())::integer AS held_seats,
            COUNT(h.id) FILTER (WHERE h.status = 'CONFIRMED')::integer AS confirmed_seats
        FROM classes c
        LEFT JOIN holds h ON h.class_id = c.id
        WHERE c.id = @classId
        GROUP BY c.id
        """,
        connection);
    command.Parameters.AddWithValue("classId", classId);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return Results.NotFound(new { message = "Class not found." });
    }

    var totalSeats = reader.GetInt32(2);
    var heldSeats = reader.GetInt32(3);
    var confirmedSeats = reader.GetInt32(4);
    return Results.Ok(new ClassSummary(
        reader.GetInt32(0),
        reader.GetString(1),
        totalSeats,
        heldSeats,
        confirmedSeats,
        Math.Max(0, totalSeats - heldSeats - confirmedSeats)));
});

app.MapPost("/classes", async (
    CreateClassRequest request,
    NpgsqlDataSource dataSource,
    CancellationToken cancellationToken) =>
{
    var name = request.Name?.Trim();
    if (string.IsNullOrWhiteSpace(name) || request.TotalSeats < 0)
    {
        return Results.BadRequest(new { message = "A class name and a non-negative totalSeats value are required." });
    }

    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using var command = new NpgsqlCommand(
        """
        INSERT INTO classes (name, total_seats)
        VALUES (@name, @totalSeats)
        ON CONFLICT (name) DO NOTHING
        RETURNING id
        """,
        connection);
    command.Parameters.AddWithValue("name", name);
    command.Parameters.AddWithValue("totalSeats", request.TotalSeats);

    var idValue = await command.ExecuteScalarAsync(cancellationToken);
    if (idValue is null)
    {
        return Results.Conflict(new { message = "A class with this name already exists." });
    }

    var id = (int)idValue;
    return Results.Created(
        $"/classes/{id}",
        new ClassSummary(id, name, request.TotalSeats, 0, 0, request.TotalSeats));
})
    .RequireAuthorization();

app.MapPost("/classes/{classId:int}/holds", async (
    int classId,
    HttpContext context,
    [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateHoldRequest? request,
    NpgsqlDataSource dataSource,
    CancellationToken cancellationToken) =>
{
    var requestedStudentId = request?.StudentBirthRegistrationId;
    var studentId = IsValidStudentId(requestedStudentId)
        ? requestedStudentId
        : GetStudentId(context);
    if (studentId is null)
    {
        return Results.BadRequest(new { message = "A valid 17-digit studentBirthRegistrationId or X-Student-Id header is required." });
    }

    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

    await using var classCommand = new NpgsqlCommand(
        "SELECT total_seats FROM classes WHERE id = @classId FOR UPDATE",
        connection,
        transaction);
    classCommand.Parameters.AddWithValue("classId", classId);
    var totalSeatsValue = await classCommand.ExecuteScalarAsync(cancellationToken);
    if (totalSeatsValue is null)
    {
        return Results.NotFound(new { message = "Class not found." });
    }

    var totalSeats = (int)totalSeatsValue;

    await using (var expireCommand = new NpgsqlCommand(
        """
        UPDATE holds SET status = 'EXPIRED'
        WHERE class_id = @classId AND status = 'HELD' AND expires_at <= now()
        """,
        connection,
        transaction))
    {
        expireCommand.Parameters.AddWithValue("classId", classId);
        await expireCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    await using (var existingCommand = new NpgsqlCommand(
        """
        SELECT id, status, expires_at
        FROM holds
        WHERE class_id = @classId AND student_id = @studentId
            AND status IN ('HELD', 'CONFIRMED')
        """,
        connection,
        transaction))
    {
        existingCommand.Parameters.AddWithValue("classId", classId);
        existingCommand.Parameters.AddWithValue("studentId", studentId);
        await using var existingReader = await existingCommand.ExecuteReaderAsync(cancellationToken);
        if (await existingReader.ReadAsync(cancellationToken))
        {
            var existingId = existingReader.GetInt64(0);
            var existingStatus = existingReader.GetString(1);
            var existingExpiry = existingReader.GetDateTime(2);
            await existingReader.CloseAsync();
            await transaction.CommitAsync(cancellationToken);

            if (existingStatus == "CONFIRMED")
            {
                return Results.Conflict(new { message = "You already have a confirmed seat in this class." });
            }

            return Results.Ok(new HoldCreateSummary(existingId, classId, studentId, existingStatus, existingExpiry));
        }
    }

    await using (var countCommand = new NpgsqlCommand(
        """
        SELECT COUNT(*)::integer FROM holds
        WHERE class_id = @classId AND (
            (status = 'HELD' AND expires_at > now()) OR status = 'CONFIRMED'
        )
        """,
        connection,
        transaction))
    {
        countCommand.Parameters.AddWithValue("classId", classId);
        var occupiedSeats = (int)(await countCommand.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Could not count occupied seats."));
        if (occupiedSeats >= totalSeats)
        {
            return Results.Conflict(new { message = "No seats are available in this class." });
        }
    }

    await using var insertCommand = new NpgsqlCommand(
        """
        INSERT INTO holds (class_id, student_id, status, expires_at)
        VALUES (@classId, @studentId, 'HELD', now() + make_interval(secs => @holdDurationSeconds))
        RETURNING id, expires_at
        """,
        connection,
        transaction);
    insertCommand.Parameters.AddWithValue("classId", classId);
    insertCommand.Parameters.AddWithValue("studentId", studentId);
    insertCommand.Parameters.AddWithValue("holdDurationSeconds", holdDurationSeconds);
    await using var insertReader = await insertCommand.ExecuteReaderAsync(cancellationToken);
    await insertReader.ReadAsync(cancellationToken);
    var holdId = insertReader.GetInt64(0);
    var expiresAt = insertReader.GetDateTime(1);
    await insertReader.CloseAsync();
    await transaction.CommitAsync(cancellationToken);

    return Results.Created(
        $"/holds/{holdId}",
        new HoldCreateSummary(holdId, classId, studentId, "HELD", expiresAt));
});

app.MapGet("/holds/{holdId:long}", async (
    long holdId,
    HttpContext context,
    NpgsqlDataSource dataSource,
    CancellationToken cancellationToken) =>
{
    var studentId = GetStudentId(context);
    if (studentId is null)
    {
        return Results.BadRequest(new { message = "A valid 17-digit X-Student-Id header is required." });
    }

    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using var command = new NpgsqlCommand(
        """
        SELECT class_id,
            CASE WHEN status = 'HELD' AND expires_at <= now() THEN 'EXPIRED' ELSE status END,
            expires_at
        FROM holds
        WHERE id = @holdId AND student_id = @studentId
        """,
        connection);
    command.Parameters.AddWithValue("holdId", holdId);
    command.Parameters.AddWithValue("studentId", studentId);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return Results.NotFound(new { message = "Application not found." });
    }

    return Results.Ok(new HoldDetailsSummary(
        holdId,
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetDateTime(2)));
});

app.MapGet("/me/holds", async (
    HttpContext context,
    NpgsqlDataSource dataSource,
    CancellationToken cancellationToken) =>
{
    var studentId = GetStudentId(context);
    if (studentId is null)
    {
        return Results.BadRequest(new { message = "A valid 17-digit X-Student-Id header is required." });
    }

    var holds = new List<StudentHoldSummary>();
    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using var command = new NpgsqlCommand(
        """
        SELECT h.id, h.class_id, c.name,
            CASE WHEN h.status = 'HELD' AND h.expires_at <= now() THEN 'EXPIRED' ELSE h.status END,
            h.expires_at
        FROM holds h
        JOIN classes c ON c.id = h.class_id
        WHERE h.student_id = @studentId
        ORDER BY h.created_at DESC
        """,
        connection);
    command.Parameters.AddWithValue("studentId", studentId);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        holds.Add(new StudentHoldSummary(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetDateTime(4)));
    }

    return Results.Ok(holds);
});

app.MapPost("/holds/{holdId:long}/confirm", async (
    long holdId,
    HttpContext context,
    NpgsqlDataSource dataSource,
    CancellationToken cancellationToken) =>
{
    var studentId = GetStudentId(context);
    if (studentId is null)
    {
        return Results.BadRequest(new { message = "A valid 17-digit X-Student-Id header is required." });
    }

    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using var command = new NpgsqlCommand(
        """
        UPDATE holds SET status = 'CONFIRMED'
        WHERE id = @holdId AND student_id = @studentId
            AND status = 'HELD' AND expires_at > now()
        RETURNING id, class_id, expires_at
        """,
        connection);
    command.Parameters.AddWithValue("holdId", holdId);
    command.Parameters.AddWithValue("studentId", studentId);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (await reader.ReadAsync(cancellationToken))
    {
        var result = new { holdId = reader.GetInt64(0), status = "CONFIRMED" };
        await reader.CloseAsync();
        return Results.Ok(result);
    }

    await reader.CloseAsync();
    await using var statusCommand = new NpgsqlCommand(
        "SELECT status, expires_at FROM holds WHERE id = @holdId AND student_id = @studentId",
        connection);
    statusCommand.Parameters.AddWithValue("holdId", holdId);
    statusCommand.Parameters.AddWithValue("studentId", studentId);
    await using var statusReader = await statusCommand.ExecuteReaderAsync(cancellationToken);
    if (!await statusReader.ReadAsync(cancellationToken))
    {
        return Results.NotFound(new { message = "Application not found." });
    }

    var status = statusReader.GetString(0);
    var expiry = statusReader.GetDateTime(1);
    if (status == "CONFIRMED")
    {
        return Results.Ok(new { holdId, status });
    }

    return status == "HELD" && expiry <= DateTime.UtcNow
        ? Results.Conflict(new { message = "This seat hold has expired. Please apply again." })
        : Results.Conflict(new { message = "This application cannot be confirmed." });
});

app.MapDelete("/holds/{holdId:long}", async (
    long holdId,
    HttpContext context,
    NpgsqlDataSource dataSource,
    CancellationToken cancellationToken) =>
{
    var studentId = GetStudentId(context);
    if (studentId is null)
    {
        return Results.BadRequest(new { message = "A valid 17-digit X-Student-Id header is required." });
    }

    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using var command = new NpgsqlCommand(
        """
        UPDATE holds SET status = 'CANCELLED'
        WHERE id = @holdId AND student_id = @studentId
            AND status = 'HELD' AND expires_at > now()
        RETURNING id
        """,
        connection);
    command.Parameters.AddWithValue("holdId", holdId);
    command.Parameters.AddWithValue("studentId", studentId);
    var cancelledId = await command.ExecuteScalarAsync(cancellationToken);
    return cancelledId is null
        ? Results.Conflict(new { message = "This application is no longer an active seat hold." })
        : Results.NoContent();
});

app.Run();

static async Task<List<AdminApplicationSummary>> LoadAdminApplicationsAsync(
    NpgsqlDataSource dataSource,
    CancellationToken cancellationToken)
{
    var applications = new List<AdminApplicationSummary>();
    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using var command = new NpgsqlCommand(
        """
        SELECT h.id, h.class_id, c.name, h.student_id, h.status, h.expires_at
        FROM holds h
        JOIN classes c ON c.id = h.class_id
        WHERE (h.status = 'HELD' AND h.expires_at > now())
            OR h.status = 'CONFIRMED'
        ORDER BY c.name, h.created_at
        """,
        connection);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);

    while (await reader.ReadAsync(cancellationToken))
    {
        applications.Add(new AdminApplicationSummary(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetDateTime(5)));
    }

    return applications;
}

static string? GetStudentId(HttpContext context)
{
    var studentId = context.Request.Headers["X-Student-Id"].ToString();
    return IsValidStudentId(studentId) ? studentId : null;
}

static bool IsValidStudentId(string? studentId) =>
    studentId is { Length: 17 } && studentId.All(char.IsDigit);

static bool FixedTimeEqualsText(string providedValue, string configuredValue)
{
    var providedBytes = Encoding.UTF8.GetBytes(providedValue);
    var configuredBytes = Encoding.UTF8.GetBytes(configuredValue);
    return providedBytes.Length == configuredBytes.Length
        && CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes);
}

record CreateClassRequest(string? Name, int TotalSeats);

record CreateHoldRequest(string? StudentBirthRegistrationId);

record HoldCreateSummary(long HoldId, int ClassId, string StudentId, string Status, DateTime ExpiresAt);

record HoldDetailsSummary(long HoldId, int ClassId, string Status, DateTime ExpiresAt);

record AdminLoginRequest(string? Email, string? Password);

record ClassSummary(
    int Id,
    string Name,
    int TotalSeats,
    int HeldSeats,
    int ConfirmedSeats,
    int AvailableSeats);

record StudentHoldSummary(long HoldId, int ClassId, string ClassName, string Status, DateTime ExpiresAt);

record AdminApplicationSummary(
    long HoldId,
    int ClassId,
    string ClassName,
    string StudentId,
    string Status,
    DateTime ExpiresAt);

public partial class Program { }
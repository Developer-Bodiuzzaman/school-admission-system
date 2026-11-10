using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class AdmissionApiTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly Dictionary<string, string?> _originalEnvironment = new();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();

        SetEnvironmentVariable("ConnectionStrings__DefaultConnection", _database.GetConnectionString());
        SetEnvironmentVariable("Admin__ApiKey", "test-api-key");
        SetEnvironmentVariable("Admin__Email", "admin@example.test");
        SetEnvironmentVariable("Admin__Password", "test-admin-password");
        SetEnvironmentVariable("HOLD_DURATION_SECONDS", "2");

        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var loginResponse = await _client.PostAsJsonAsync(
            "/admin/login",
            new { email = "admin@example.test", password = "test-admin-password" });
        loginResponse.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _database.DisposeAsync();
        foreach (var (name, value) in _originalEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task ClassCreationAndDetailValidateCapacityAndName()
    {
        var created = await CreateClassAsync(12);
        Assert.Equal(12, created.TotalSeats);

        var detail = await _client.GetFromJsonAsync<ClassResponse>($"/classes/{created.Id}");
        Assert.NotNull(detail);
        Assert.Equal(created.Id, detail.Id);
        Assert.Equal(12, detail.AvailableSeats);

        var duplicateResponse = await _client.PostAsJsonAsync(
            "/classes",
            new { name = created.Name, totalSeats = 2 });
        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);

        var invalidResponse = await _client.PostAsJsonAsync(
            "/classes",
            new { name = "Invalid capacity", totalSeats = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        var missingResponse = await _client.GetAsync("/classes/2147483647");
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    [Fact]
    public async Task HoldLifecycleIsStudentScopedAndConfirmationIsIdempotent()
    {
        var classItem = await CreateClassAsync(1);
        const string studentId = "20152691234567890";
        var invalidIdResponse = await _client.PostAsJsonAsync(
            $"/classes/{classItem.Id}/holds",
            new { studentBirthRegistrationId = "not-a-student-id" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidIdResponse.StatusCode);

        var holdResponse = await CreateHoldAsync(classItem.Id, studentId);
        Assert.Equal(HttpStatusCode.Created, holdResponse.StatusCode);

        var hold = await holdResponse.Content.ReadFromJsonAsync<HoldResponse>();
        Assert.NotNull(hold);
        Assert.Equal(studentId, hold.StudentId);
        Assert.Equal(classItem.Id, hold.ClassId);
        Assert.Equal("HELD", hold.Status);

        var repeatedHoldResponse = await CreateHoldAsync(classItem.Id, studentId);
        Assert.Equal(HttpStatusCode.OK, repeatedHoldResponse.StatusCode);
        var repeatedHold = await repeatedHoldResponse.Content.ReadFromJsonAsync<HoldResponse>();
        Assert.Equal(hold.HoldId, repeatedHold!.HoldId);

        using var ownerRequest = new HttpRequestMessage(HttpMethod.Get, $"/holds/{hold.HoldId}");
        ownerRequest.Headers.Add("X-Student-Id", studentId);
        var detailResponse = await _client.SendAsync(ownerRequest);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await detailResponse.Content.ReadFromJsonAsync<HoldDetailResponse>();
        Assert.Equal(classItem.Id, detail!.ClassId);
        Assert.Equal("HELD", detail.Status);

        using var otherStudentRequest = new HttpRequestMessage(HttpMethod.Get, $"/holds/{hold.HoldId}");
        otherStudentRequest.Headers.Add("X-Student-Id", "20152691234567891");
        var otherStudentResponse = await _client.SendAsync(otherStudentRequest);
        Assert.Equal(HttpStatusCode.NotFound, otherStudentResponse.StatusCode);

        using var myHoldsRequest = new HttpRequestMessage(HttpMethod.Get, "/me/holds");
        myHoldsRequest.Headers.Add("X-Student-Id", studentId);
        var myHoldsResponse = await _client.SendAsync(myHoldsRequest);
        var myHolds = await myHoldsResponse.Content.ReadFromJsonAsync<List<StudentHoldResponse>>();
        Assert.Contains(myHolds!, item => item.ClassId == classItem.Id);

        var confirmResponse = await ConfirmAsync(hold.HoldId, studentId);
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);
        var repeatedConfirmResponse = await ConfirmAsync(hold.HoldId, studentId);
        Assert.Equal(HttpStatusCode.OK, repeatedConfirmResponse.StatusCode);

        var confirmedClass = await _client.GetFromJsonAsync<ClassResponse>($"/classes/{classItem.Id}");
        Assert.Equal(1, confirmedClass!.ConfirmedSeats);
        Assert.Equal(0, confirmedClass.AvailableSeats);

        var deleteResponse = await DeleteHoldAsync(hold.HoldId, studentId);
        Assert.Equal(HttpStatusCode.Conflict, deleteResponse.StatusCode);

        var cancellableClass = await CreateClassAsync(1);
        const string otherStudentId = "20152691234567891";
        var cancellableHoldResponse = await CreateHoldAsync(cancellableClass.Id, otherStudentId);
        var cancellableHold = await cancellableHoldResponse.Content.ReadFromJsonAsync<HoldResponse>();
        var cancelResponse = await DeleteHoldAsync(cancellableHold!.HoldId, otherStudentId);
        Assert.Equal(HttpStatusCode.NoContent, cancelResponse.StatusCode);
        var classAfterCancel = await _client.GetFromJsonAsync<ClassResponse>($"/classes/{cancellableClass.Id}");
        Assert.Equal(1, classAfterCancel!.AvailableSeats);
    }

    [Fact]
    public async Task HoldEndpointStillAcceptsTheDocumentedStudentIdHeader()
    {
        var classItem = await CreateClassAsync(1);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/classes/{classItem.Id}/holds");
        request.Headers.Add("X-Student-Id", "20152691234567890");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task SwaggerDocumentsTheCreateHoldRequestBody()
    {
        var swagger = await _client.GetStringAsync("/swagger/v1/swagger.json");

        Assert.Contains("studentBirthRegistrationId", swagger, StringComparison.Ordinal);
        Assert.Contains("/classes/{classId}/holds", swagger, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredHoldReleasesCapacityWithoutAStudentAction()
    {
        var classItem = await CreateClassAsync(1);
        const string studentId = "20152691234567890";
        var holdResponse = await CreateHoldAsync(classItem.Id, studentId);
        var hold = await holdResponse.Content.ReadFromJsonAsync<HoldResponse>();

        await Task.Delay(TimeSpan.FromSeconds(2.2));

        using var holdRequest = new HttpRequestMessage(HttpMethod.Get, $"/holds/{hold!.HoldId}");
        holdRequest.Headers.Add("X-Student-Id", studentId);
        var expiredResponse = await _client.SendAsync(holdRequest);
        var expiredHold = await expiredResponse.Content.ReadFromJsonAsync<HoldDetailResponse>();
        Assert.Equal("EXPIRED", expiredHold!.Status);

        var classAfterExpiry = await _client.GetFromJsonAsync<ClassResponse>($"/classes/{classItem.Id}");
        Assert.Equal(1, classAfterExpiry!.AvailableSeats);

        var replacementResponse = await CreateHoldAsync(classItem.Id, studentId);
        Assert.Equal(HttpStatusCode.Created, replacementResponse.StatusCode);
    }

    [Fact]
    public async Task ConcurrentHoldRequestsNeverExceedTheSeatCapacity()
    {
        const int capacity = 4;
        var classItem = await CreateClassAsync(capacity);
        var responses = await Task.WhenAll(Enumerable.Range(0, 32).Select(index =>
        {
            var studentId = (20152691234500000L + index).ToString("D17");
            return CreateHoldAsync(classItem.Id, studentId);
        }));

        Assert.Equal(capacity, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(32 - capacity, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));

        var classAfterRequests = await _client.GetFromJsonAsync<ClassResponse>($"/classes/{classItem.Id}");
        Assert.Equal(capacity, classAfterRequests!.HeldSeats + classAfterRequests.ConfirmedSeats);
        Assert.Equal(0, classAfterRequests.AvailableSeats);
    }

    private async Task<ClassResponse> CreateClassAsync(int seats)
    {
        var response = await _client.PostAsJsonAsync(
            "/classes",
            new { name = $"Test class {Guid.NewGuid():N}", totalSeats = seats });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ClassResponse>())!;
    }

    private Task<HttpResponseMessage> CreateHoldAsync(int classId, string studentId) =>
        _client.PostAsJsonAsync(
            $"/classes/{classId}/holds",
            new { studentBirthRegistrationId = studentId });

    private Task<HttpResponseMessage> ConfirmAsync(long holdId, string studentId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/holds/{holdId}/confirm");
        request.Headers.Add("X-Student-Id", studentId);
        request.Content = JsonContent.Create(new { });
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> DeleteHoldAsync(long holdId, string studentId)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/holds/{holdId}");
        request.Headers.Add("X-Student-Id", studentId);
        return _client.SendAsync(request);
    }

    private void SetEnvironmentVariable(string name, string value)
    {
        _originalEnvironment.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    private sealed record ClassResponse(
        int Id,
        string Name,
        int TotalSeats,
        int HeldSeats,
        int ConfirmedSeats,
        int AvailableSeats);

    private sealed record HoldResponse(
        long HoldId,
        int ClassId,
        string StudentId,
        string Status,
        DateTime ExpiresAt);

    private sealed record HoldDetailResponse(long HoldId, int ClassId, string Status, DateTime ExpiresAt);

    private sealed record StudentHoldResponse(
        long HoldId,
        int ClassId,
        string ClassName,
        string Status,
        DateTime ExpiresAt);
}

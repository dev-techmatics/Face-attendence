using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FaceAttendance.Api.Infrastructure;
using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Faces;
using FaceAttendance.Infrastructure;
using FaceAttendance.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(json =>
    {
        // Enums as names: "Front" survives a renumbering in a way that 1 does not, and it makes
        // the API readable without a lookup table.
        json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        json.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<FaceExceptionHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HeaderTenantContext>();

builder.Services.AddOpenApi();

// ---- Upload limits -------------------------------------------------------------------------
// Kestrel defaults to 30MB, which is far more than three JPEG frames need. Tightening it means
// an oversized upload is refused at the transport layer rather than after being buffered.
builder.Services.Configure<FormOptions>(form =>
{
    form.MultipartBodyLengthLimit = 32 * 1024 * 1024;
    form.ValueLengthLimit = 1024 * 1024;
    form.MultipartHeadersLengthLimit = 32 * 1024;
});

// ---- Authentication ------------------------------------------------------------------------
builder.Services.Configure<ApiKeyAuthOptions>(builder.Configuration.GetSection(ApiKeyAuthOptions.SectionName));

builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<ApiKeySchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Admin, policy => policy
        .RequireAuthenticatedUser()
        .RequireRole(ApiKeyAuthenticationHandler.AdminRole))
    // An admin key can do anything a device can, so it satisfies the device policy too.
    .AddPolicy(Policies.Device, policy => policy
        .RequireAuthenticatedUser()
        .RequireRole(ApiKeyAuthenticationHandler.DeviceRole, ApiKeyAuthenticationHandler.AdminRole))
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// ---- Rate limiting -------------------------------------------------------------------------
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    limiter.AddPolicy(RateLimitPolicies.Attendance, context =>
    {
        // Partition by device where one identifies itself, otherwise by remote address, so one
        // misbehaving kiosk cannot consume the budget for every other door.
        string key = context.Request.Headers["X-Device-Id"].FirstOrDefault()
                     ?? context.Connection.RemoteIpAddress?.ToString()
                     ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});

// ---- CORS ----------------------------------------------------------------------------------
string[] allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
{
    if (allowedOrigins.Length == 0)
    {
        // No origins configured means no browser is expected to call this directly. Leaving it
        // open would let any website post to the attendance endpoint with a stolen key.
        policy.WithOrigins().AllowAnyHeader().AllowAnyMethod();
        return;
    }

    policy.WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials();
}));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database");

builder.Services.AddFaceAttendance(
    builder.Configuration,
    builder.Environment.IsDevelopment());

WebApplication app = builder.Build();

app.UseExceptionHandler();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

await MigrateAsync(app);
WarmUpRecognitionProvider(app);

app.Run();

/// <summary>
/// Build the recognition provider before serving traffic.
/// </summary>
/// <remarks>
/// The provider is a singleton, so without this it is constructed lazily on the first request -
/// meaning a missing ONNX model file is discovered by the first employee who tries to punch in,
/// not by the engineer who deployed it. Failing here is loud and immediate, which is what a
/// configuration error deserves.
/// </remarks>
static void WarmUpRecognitionProvider(WebApplication app)
{
    ILogger<Program> logger = app.Services.GetRequiredService<ILogger<Program>>();

    try
    {
        IFaceRecognitionProvider provider = app.Services.GetRequiredService<IFaceRecognitionProvider>();

        logger.LogInformation(
            "Face recognition provider ready: {ProviderId} ({TemplateKind} templates)",
            provider.ProviderId, provider.TemplateKind);
    }
    catch (FaceProcessingException ex) when (ex.Error == FaceError.ProviderNotConfigured)
    {
        logger.LogCritical("Face recognition cannot start: {Message}", ex.Message);
        throw;
    }
}

/// <summary>
/// Apply pending migrations at startup in Development only. In production this belongs in the
/// deployment pipeline: two instances starting at once would otherwise race each other through
/// the same schema change.
/// </summary>
static async Task MigrateAsync(WebApplication app)
{
    if (!app.Environment.IsDevelopment() &&
        !app.Configuration.GetValue("Database:MigrateOnStartup", false))
    {
        return;
    }

    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    ILogger<Program> logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        await db.Database.MigrateAsync();
        logger.LogInformation("Database schema is up to date");
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Could not migrate the database. The API will start but will fail on every request.");
    }
}

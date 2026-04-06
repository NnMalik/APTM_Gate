using APTM.Gate.Api.Endpoints;
using APTM.Gate.Api.Services;
using APTM.Gate.Infrastructure;
using APTM.Gate.Workers;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Infrastructure: DbContext, services
builder.Services.AddInfrastructureServices(builder.Configuration);

// Workers: TcpReaderWorker, BufferProcessorWorker, WatchdogService
builder.Services.AddWorkerServices();

// SSE Notification Service (singleton + hosted service)
builder.Services.AddSingleton<SseNotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SseNotificationService>());

// Authentication
builder.Services.AddAuthentication(DeviceTokenAuthHandler.SchemeName)
    .AddScheme<DeviceTokenAuthOptions, DeviceTokenAuthHandler>(
        DeviceTokenAuthHandler.SchemeName,
        options =>
        {
            options.DeviceCode = builder.Configuration["Gate:AcceptedToken"]
                ?? builder.Configuration["Gate:DeviceCode"]
                ?? "gate-01";
        });
builder.Services.AddAuthorization();

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "APTM Gate Service API",
        Version = "v1",
        Description = "Gate Service — local UHF tag capture, processing, display, and sync hub for APTM."
    });

    c.AddSecurityDefinition("DeviceToken", new OpenApiSecurityScheme
    {
        Name = "X-Device-Token",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "Device token (same as used with APTM Main)"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "DeviceToken"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Swagger UI (always enabled — gate runs on local network only)
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "APTM Gate API v1");
    c.RoutePrefix = "swagger";
});

app.UseAuthentication();
app.UseAuthorization();

// Static files for display HTML
app.UseStaticFiles();

// Map endpoints
app.MapConfigEndpoints();
app.MapSyncEndpoints();
app.MapDisplayEndpoints();
app.MapReaderEndpoints();
app.MapDiagnosticsEndpoints();
app.MapHealthEndpoints();

app.Run();

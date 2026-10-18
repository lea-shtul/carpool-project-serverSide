using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Carpool.API.BackgroundServices;
using Carpool.API.Middleware;
using Carpool.API.Swagger;
using Microsoft.AspNetCore.Mvc;
using Carpool.Core.Configuration;
using Carpool.Core.Entities;
using Carpool.Core.Interfaces.Repositories;
using Carpool.Core.Interfaces.Security;
using Carpool.Core.Interfaces.Services;
using Carpool.Data;
using Carpool.Data.Repositories;
using Carpool.Service.Mapping;
using Carpool.Service.Security;
using Carpool.Service.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using NLog;
using NLog.Web;

// Bootstrap NLog before the host is built so a startup failure is still logged (spec §57).
var logger = NLog.LogManager.Setup()
    .LoadConfigurationFromFile("nlog.config")
    .GetCurrentClassLogger();

try
{
    logger.Info("Carpool API starting up.");

    var builder = WebApplication.CreateBuilder(args);

    // Route every ILogger<T> through NLog (spec §57).
    builder.Logging.ClearProviders();
    builder.Host.UseNLog();

    // Add services to the container.

    // Serialize enums (Role, RideStatus, BookingStatus) as their names, not integers.
    builder.Services
        .AddControllers()
        .AddJsonOptions(options =>
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    // Model-validation failures ([Required], [Range], [AllowedValues], …) go through the
    // same ErrorResponse shape as every other error (spec §54), not the framework's default
    // ProblemDetails — the field errors are folded into one message string.
    builder.Services.Configure<ApiBehaviorOptions>(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var correlationId = context.HttpContext.GetCorrelationId();
            var message = string.Join(
                " ",
                context.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));

            return new BadRequestObjectResult(
                new ErrorResponse(StatusCodes.Status400BadRequest, message, correlationId));
        };
    });

    // Swagger / OpenAPI (spec §61). Every endpoint is documented (XML comments), the common
    // error responses are added by an operation filter, and a Bearer scheme lets protected
    // endpoints be exercised from the UI.
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Carpool API",
            Version = "v1",
            Description = "Ride-sharing Web API — authentication, vehicles, rides, seat booking "
                          + "with optimistic concurrency, ratings and tags.",
        });

        foreach (var assembly in new[] { Assembly.GetExecutingAssembly(), typeof(JwtOptions).Assembly })
        {
            var xmlPath = Path.Combine(AppContext.BaseDirectory, $"{assembly.GetName().Name}.xml");
            if (File.Exists(xmlPath))
            {
                options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
            }
        }

        var bearerScheme = new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Paste the JWT returned by POST /api/auth/login (no 'Bearer ' prefix needed).",
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
        };
        options.AddSecurityDefinition("Bearer", bearerScheme);
        options.AddSecurityRequirement(new OpenApiSecurityRequirement { [bearerScheme] = Array.Empty<string>() });

        options.OperationFilter<DefaultErrorResponsesOperationFilter>();
    });

    // EF Core / PostgreSQL. The connection string is read from configuration
    // (User Secrets in Development, environment variables otherwise) — never hard-coded
    // (spec §44, §48). DbContext is registered with the default Scoped lifetime (spec §53).
    //
    // Deployment extension: Render's managed Postgres only hands out a DATABASE_URL
    // connection URI (postgres://user:pass@host:port/db), not a ready-made Npgsql
    // key=value string, so when ConnectionStrings:CarpoolDb is left empty (as in the
    // container image), fall back to building one from DATABASE_URL.
    var connectionString = builder.Configuration.GetConnectionString("CarpoolDb");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(databaseUrl))
        {
            connectionString = DatabaseUrlConverter.ToNpgsqlConnectionString(databaseUrl);
        }
    }

    builder.Services.AddDbContext<CarpoolDbContext>(options =>
        options.UseNpgsql(connectionString));

    // Repositories and the unit of work (spec §52, §53). Registered Scoped — the same
    // lifetime as CarpoolDbContext, which they all depend on.
    builder.Services.AddScoped<IUnitOfWork, CarpoolUnitOfWork>();
    builder.Services.AddScoped<IUserRepository, UserRepository>();
    builder.Services.AddScoped<IVehicleRepository, VehicleRepository>();
    builder.Services.AddScoped<IRideRepository, RideRepository>();
    builder.Services.AddScoped<IBookingRepository, BookingRepository>();
    builder.Services.AddScoped<IRatingRepository, RatingRepository>();
    builder.Services.AddScoped<ITagRepository, TagRepository>();

    // AutoMapper profiles live in Carpool.Service/Mapping (spec §34). AutoMapper 13+ requires
    // the configuration-action overload; AddMaps scans the assembly for Profile classes.
    builder.Services.AddAutoMapper(cfg => cfg.AddMaps(typeof(TagProfile).Assembly));

    // Business services (spec §36, §53). Registered Scoped alongside their repositories.
    builder.Services.AddScoped<ITagService, TagService>();
    builder.Services.AddScoped<IUserService, UserService>();
    builder.Services.AddScoped<IVehicleService, VehicleService>();
    builder.Services.AddScoped<IRideService, RideService>();
    builder.Services.AddScoped<IBookingService, BookingService>();
    builder.Services.AddScoped<IRatingService, RatingService>();
    builder.Services.AddScoped<IRideStatusService, RideStatusService>();

    // Time-driven ride status automation (spec §15, §16).
    builder.Services.AddHostedService<RideStatusBackgroundService>();

    // --- Authentication & authorization (spec §39–§41) ---

    // JWT settings: Issuer/Audience/ExpiryMinutes from appsettings.json, Key from User Secrets.
    var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
    builder.Services.Configure<JwtOptions>(jwtSection);
    var jwtOptions = jwtSection.Get<JwtOptions>()
        ?? throw new InvalidOperationException("The 'Jwt' configuration section is missing.");
    if (string.IsNullOrWhiteSpace(jwtOptions.Key))
    {
        throw new InvalidOperationException(
            "Jwt:Key is not configured. Set it via User Secrets: dotnet user-secrets set \"Jwt:Key\" \"<secret>\".");
    }

    builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
    builder.Services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
    builder.Services.AddScoped<IAuthService, AuthService>();

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            // Keep the token's claim names verbatim ("sub", "email", "role") instead of the
            // legacy WS-* URI remapping, so controllers can read them by their JWT names (spec §40).
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtOptions.Issuer,
                ValidateAudience = true,
                ValidAudience = jwtOptions.Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = JwtRegisteredClaimNames.Sub,
                RoleClaimType = System.Security.Claims.ClaimTypes.Role,
            };
        });

    builder.Services.AddAuthorization();

var allowedOrigins = builder.Configuration["Cors:AllowedOrigins"]?
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Client", policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod());
});


    var app = builder.Build();

    // Configure the HTTP request pipeline.

    // Apply any pending migrations on startup in every environment — not just Development.
    // The container deploy has no separate "run migrations" step, so the API applies its
    // own schema on boot; inside its own DI scope so we never hold a Scoped DbContext
    // outside a scope (spec §53).
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<CarpoolDbContext>();
        await db.Database.MigrateAsync();
    }

    // Swagger stays enabled in production too, so the deployed API is self-documenting
    // at /swagger without a separate environment-gated build.
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Carpool API v1");
        options.DocumentTitle = "Carpool API";
    });

    // Middleware order matters (spec §56):
    //  - exception handling outermost, so it catches everything downstream;
    //  - correlation id next, so it is available to request/error logging;
    //  - request logging next, so every request is logged with its correlation id;
    //  - then the framework pipeline; authentication before authorization.
    app.UseMiddleware<ExceptionHandlingMiddleware>();
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseMiddleware<RequestLoggingMiddleware>();

    // Render (and most PaaS hosts) terminate TLS at their own edge and forward plain HTTP
    // to the container, so redirecting to HTTPS inside the container would just loop.
    if (app.Environment.IsDevelopment())
    {
        app.UseHttpsRedirection();
    }


app.UseCors("Client");

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();

    app.Run();
}
catch (Exception exception)
{
    // A host-startup failure never reaches the exception middleware — log it here (spec §58).
    logger.Error(exception, "Carpool API terminated unexpectedly during startup.");
    throw;
}
finally
{
    // Flush and release buffered log entries on shutdown.
    NLog.LogManager.Shutdown();
}

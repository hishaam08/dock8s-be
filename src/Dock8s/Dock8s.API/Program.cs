using Dock8s.API.Controllers;
using Dock8s.API.Service;
using Dock8s.Application.SignalRHub;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
});

NpgsqlConnection.GlobalTypeMapper.EnableDynamicJson();
builder.Services.AddDbContext<DindDbContext>(options =>
    options.UseNpgsql(
        configuration.GetConnectionString("DindDatabase")
        ?? "Host=168.220.248.33;Port=5432;Database=dock8s;Username=shapy;Password=Hishu08"
    )
);

var allowedOrigins = new[]
{
    "https://dock8s.in",
    "https://www.dock8s.in",
    "http://localhost:3000"
};

builder.Services.AddControllers();
builder.Services.AddScoped<TraefikFileRouterService>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.AddSingleton(sp =>
{
    var jwtSecret = configuration["Jwt:Secret"] ?? "your-super-secret-jwt-key-min-32-chars";
    var jwtIssuer = configuration["Jwt:Issuer"] ?? "Dock8s";
    return new JwtService(jwtSecret, jwtIssuer);
});

builder.Services.AddSingleton(sp =>
{
    return new ContainerConfig
    {
        DefaultTtlMinutes = int.Parse(configuration["Container:DefaultTtlMinutes"] ?? "30"),
        IdleTimeoutMinutes = int.Parse(configuration["Container:IdleTimeoutMinutes"] ?? "15"),
        MaxConcurrentContainersPerUser = int.Parse(configuration["Container:MaxConcurrentPerUser"] ?? "1"),
        CpuLimit = int.Parse(configuration["Container:CpuLimit"] ?? "1"),
        MemoryLimitBytes = long.Parse(configuration["Container:MemoryLimitMB"] ?? "2048") * 1024 * 1024,
        SysboxRuntime = configuration["Container:Runtime"] ?? "sysbox-runc",
        NetworkName = configuration["Container:Network"] ?? "traefik-public",
        DindImage = configuration["Container:Image"] ?? "docker:dind"
    };
});

builder.Services.AddSingleton(sp =>
{
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    var dynamicPath = Path.Combine(env.ContentRootPath, "dynamic");
    
    var dockerHost = configuration["Docker:Host"] ?? "unix:///var/run/docker.sock";
    var domain = configuration["Traefik:Domain"] ?? "dock8s.in";
    var useHttps = bool.Parse(configuration["Traefik:UseHttps"] ?? "true");

    return new TraefikFileRouterService(
        dockerHost: dockerHost,
        domain: domain,
        dynamicConfigPath: dynamicPath,
        useHttps: useHttps
    );
});

builder.Services.AddSingleton<DindContainerManager>();
var app = builder.Build();

app.UseRouting();
app.UseCors("AllowFrontend");

app.MapControllers();
app.MapHub<TerminalHub>("/terminalhub").RequireCors("AllowFrontend");

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "healthy",
    service = "Dock8s Platform",
    timestamp = DateTime.UtcNow,
    version = "1.0.0"
}));

app.Run();

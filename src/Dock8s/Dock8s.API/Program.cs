using Dock8s.Application.SignalRHub;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
});

var allowedOrigins = new[]
{
    "https://dock8s.in",
    "https://www.dock8s.in",
    "http://localhost:3000"
};

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

var app = builder.Build();

app.UseRouting();
app.UseCors("AllowFrontend");

app.MapHub<TerminalHub>("/terminalhub").RequireCors("AllowFrontend");

app.Run();

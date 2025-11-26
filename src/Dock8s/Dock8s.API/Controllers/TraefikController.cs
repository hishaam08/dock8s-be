    using Docker.DotNet;
    using Docker.DotNet.Models;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;

    namespace Dock8s.API.Controllers
    {
        // ============= MODELS =============
        public class ExposeRequest
        {
            public string UserId { get; set; }
            public int Port { get; set; }
            public string? Subdomain { get; set; }
        }

        public class RouteInfo
        {
            public string Id { get; set; }
            public string Subdomain { get; set; }
            public string Hostname { get; set; }
            public string Port { get; set; }
            public string ContainerId { get; set; }
            public string UserId { get; set; }
            public string Status { get; set; }
            public string Url { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        public class RouteResponse
        {
            public string Url { get; set; }
            public string Hostname { get; set; }
            public string RouteId { get; set; }
            public string Status { get; set; }
        }

        public class PrewarmInfo
        {
            public string UserId { get; set; }
            public string Hostname { get; set; }
            public string RouteId { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        // ============= TRAEFIK FILE ROUTER SERVICE =============
        public class TraefikFileRouterService
    {
        private readonly DockerClient _dockerClient;
        private readonly string _domain;
        private readonly string _dynamicConfigPath;
        private readonly string _routesDbPath;
        private readonly string _prewarmDbPath;
        private readonly bool _useHttps;
        private const string TRAEFIK_NETWORK = "traefik-network";

        public TraefikFileRouterService(
            string dockerHost = "tcp://localhost:2375",
            string domain = "dock8s.in",
            string dynamicConfigPath = "/dynamic",
            bool useHttps = true)
        {
            _dockerClient = new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();
            _domain = domain;
            _dynamicConfigPath = dynamicConfigPath;
            _routesDbPath = Path.Combine(_dynamicConfigPath, "routes.json");
            _prewarmDbPath = Path.Combine(_dynamicConfigPath, "prewarm.json");
            _useHttps = useHttps;

            Directory.CreateDirectory(_dynamicConfigPath);
            
            if (!File.Exists(_routesDbPath))
            {
                File.WriteAllText(_routesDbPath, "[]");
            }
            if (!File.Exists(_prewarmDbPath))
            {
                File.WriteAllText(_prewarmDbPath, "[]");
            }   

        }

        /// <summary>
        /// Creates a prewarm route for a user to trigger SSL certificate generation
        /// This creates a simple health check endpoint at prewarm.{userId}.{domain}
        /// </summary>
        public async Task CreatePrewarmRouteAsync(string userId)
        {
            // var safeUserId = SanitizeString(userId);
            var routeId = $"prewarm-{userId}";
            var hostname = $"{userId}.{_domain}";
            var containerName = $"dind-{userId}";

            // 🔥 NEW: Check if already prewarmed
            if (await PrewarmExistsAsync(userId))
            {
                Console.WriteLine($"⏭️ Skipping prewarm. {hostname} is already prewarmed.");
                return;
            }

            Console.WriteLine($"🔥 Creating prewarm route for: {hostname}");

            await EnsureContainerNetworkAsync(containerName);

            var configPath = Path.Combine(_dynamicConfigPath, $"{routeId}.yml");

            await GeneratePrewarmConfigAsync(routeId, hostname, userId, configPath);
            await PrewarmCertificateAsync(hostname);

            await AddPrewarmAsync(new PrewarmInfo
            {
                UserId = userId,
                Hostname = hostname,
                RouteId = routeId,
                CreatedAt = DateTime.UtcNow
            });

            Console.WriteLine($"✅ Prewarm completed: {hostname}");
        }


        /// <summary>
        /// Deletes the prewarm route for a user
        /// </summary>
        public async Task DeletePrewarmRouteAsync(string userId)
        {
            var safeUserId = SanitizeString(userId);
            var routeId = $"prewarm-{safeUserId}";
            var configPath = Path.Combine(_dynamicConfigPath, $"{routeId}.yml");

            if (File.Exists(configPath))
            {
                File.Delete(configPath);
                Console.WriteLine($"🗑️  Deleted prewarm config: {configPath}");
            }
        }

        private async Task GeneratePrewarmConfigAsync(string routeId, string hostname, string userId, string filePath)
        {
            var entryPoint = _useHttps ? "websecure" : "web";

            // Extract parent domain for wildcard certificate
            var parentDomain = $"{userId}.{_domain}";
            var wildcardDomain = $"*.{parentDomain}";

            // Create a minimal router with no backend service - just for SSL cert generation
            var yaml = $@"http:
  routers:
    r-{routeId}:
      rule: ""Host(`{hostname}`)""
      service: ""s-{routeId}""
      entryPoints:
        - {entryPoint}";

            if (_useHttps)
            {
                yaml += $@"
      tls:
        certResolver: letsencrypt-dns
        domains:
          - main: ""{parentDomain}""
            sans:
              - ""{wildcardDomain}""";
            }

            // Create a dummy service that returns 503 (Service Unavailable)
            // This is just to satisfy Traefik's requirement that routers have a service
            yaml += $@"

  services:
    s-{routeId}:
      loadBalancer:
        servers:
          - url: ""http://127.0.0.1:1""
";

            await File.WriteAllTextAsync(filePath, yaml);
            Console.WriteLine($"📝 Prewarm Traefik config written: {filePath}");
            Console.WriteLine($"🔒 Wildcard certificate will be generated for: {wildcardDomain}");
        }

        public async Task<RouteInfo> ExposePortAsync(ExposeRequest request)
        {
            if (request.Port < 1 || request.Port > 65535)
            {
                throw new ArgumentException("Invalid port number. Must be between 1 and 65535.");
            }

            var safeUserId = SanitizeString(request.UserId);
            
            string routeId;
            string hostname;
            
            if (!string.IsNullOrEmpty(request.Subdomain))
            {
                var safeSubdomain = SanitizeString(request.Subdomain);
                routeId = $"{safeSubdomain}-{request.UserId}";
                hostname = $"{safeSubdomain}.{request.UserId}.{_domain}";
            }
            else
            {
                routeId = $"p{request.Port}-{request.UserId}";
                hostname = $"p{request.Port}.{request.UserId}.{_domain}";
            }

            var existingRoutes = await GetRoutesAsync(request.UserId);
            if (existingRoutes.Any(r => r.Hostname == hostname))
            {
                throw new InvalidOperationException($"Route for {hostname} already exists.");
            }

            var containerId = $"dind-{request.UserId}";
            
            var containerExists = await VerifyContainerAsync(containerId);
            if (!containerExists)
            {
                throw new InvalidOperationException($"Container {containerId} not found or not running.");
            }

            await EnsureContainerNetworkAsync(containerId);

            var configPath = Path.Combine(_dynamicConfigPath, $"{routeId}.yml");

            await GenerateTraefikConfigAsync(routeId, hostname, request.Port, safeUserId, configPath);            
            await PrewarmCertificateAsync(hostname);

            var route = new RouteInfo
            {
                Id = routeId,
                Subdomain = request.Subdomain ?? $"p{request.Port}",
                Hostname = hostname,
                Port = request.Port.ToString(),
                ContainerId = containerId,
                UserId = request.UserId,
                Status = "active",
                Url = $"{(_useHttps ? "https" : "http")}://{hostname}",
                CreatedAt = DateTime.UtcNow
            };

            await SaveRouteToDbAsync(route);

            Console.WriteLine($"✅ Route exposed: {hostname} → {containerId}:{request.Port}");

            return route;
        }

        private async Task GenerateTraefikConfigAsync(string routeId, string hostname, int port, string userId, string filePath)
        {
            var containerName = $"dind-{userId}";
            var protocol = _useHttps ? "https" : "http";
            var entryPoint = _useHttps ? "websecure" : "web";

            var parentDomain = GetParentDomain(hostname);
            var wildcardDomain = $"*.{parentDomain}";

            var yaml = $@"http:
  routers:
    r-{routeId}:
      rule: ""Host(`{hostname}`)""
      service: ""s-{routeId}""
      entryPoints:
        - {entryPoint}";

            if (_useHttps)
            {
                yaml += $@"
      tls:
        certResolver: letsencrypt-dns
        domains:
          - main: ""{parentDomain}""
            sans:
              - ""{wildcardDomain}""";
            }

            yaml += $@"

  services:
    s-{routeId}:
      loadBalancer:
        servers:
          - url: ""http://{containerName}:{port}""
";

            await File.WriteAllTextAsync(filePath, yaml);
            Console.WriteLine($"📝 Traefik config written: {filePath}");
            Console.WriteLine($"🔒 Using wildcard certificate: {wildcardDomain}");
        }

        private async Task PrewarmCertificateAsync(string hostname)
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30) // Increased timeout for DNS-01
            };

            var url = $"https://{hostname}";

            try
            {
                Console.WriteLine($"🌡️ Prewarming TLS for {hostname}...");
                var response = await client.GetAsync(url);
                Console.WriteLine($"🔥 Certificate ready for {hostname} (Status: {response.StatusCode})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Prewarm attempt for {hostname}: {ex.Message}");
            }
        }

        private string GetParentDomain(string hostname)
        {
            var parts = hostname.Split('.');
            
            if (parts.Length < 3)
            {
                throw new ArgumentException($"Invalid hostname format: {hostname}");
            }
            
            var parentDomain = string.Join(".", parts.Skip(1));
            
            return parentDomain;
        }

        public async Task<bool> DeleteRouteAsync(string routeId, string userId)
        {
            var safeUserId = SanitizeString(userId);
            
            var route = await GetRouteByIdAsync(routeId, userId);
            if (route == null)
            {
                return false;
            }

            var configPath = Path.Combine(_dynamicConfigPath, $"{routeId}.yml");
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
                Console.WriteLine($"🗑️  Deleted config: {configPath}");
            }

            await RemoveRouteFromDbAsync(routeId);

            Console.WriteLine($"✅ Route deleted: {routeId}");
            return true;
        }

        public async Task<List<RouteInfo>> GetRoutesAsync(string userId = null)
        {
            var json = await File.ReadAllTextAsync(_routesDbPath);
            var routes = JsonSerializer.Deserialize<List<RouteInfo>>(json) ?? new List<RouteInfo>();

            if (!string.IsNullOrEmpty(userId))
            {
                routes = routes.Where(r => r.UserId == userId).ToList();
            }

            return routes;
        }

        public async Task<RouteInfo> GetRouteByIdAsync(string routeId, string userId = null)
        {
            var routes = await GetRoutesAsync(userId);
            return routes.FirstOrDefault(r => r.Id == routeId);
        }

        private async Task SaveRouteToDbAsync(RouteInfo route)
        {
            var routes = await GetRoutesAsync();
            routes.RemoveAll(r => r.Id == route.Id);
            routes.Add(route);
            
            var json = JsonSerializer.Serialize(routes, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            
            await File.WriteAllTextAsync(_routesDbPath, json);
        }

        private async Task RemoveRouteFromDbAsync(string routeId)
        {
            var routes = await GetRoutesAsync();
            routes.RemoveAll(r => r.Id == routeId);
            
            var json = JsonSerializer.Serialize(routes, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            
            await File.WriteAllTextAsync(_routesDbPath, json);
        }

        private async Task<bool> VerifyContainerAsync(string containerId)
        {
            try
            {
                var containers = await _dockerClient.Containers.ListContainersAsync(
                    new ContainersListParameters { All = false });
                
                return containers.Any(c => 
                    c.Names.Any(n => n.TrimStart('/') == containerId) || 
                    c.ID.StartsWith(containerId));
            }
            catch
            {
                return false;
            }
        }

        private async Task EnsureContainerNetworkAsync(string containerId)
        {
            try
            {
                var container = await _dockerClient.Containers.InspectContainerAsync(containerId);
                var networks = container.NetworkSettings.Networks;

                if (!networks.ContainsKey(TRAEFIK_NETWORK))
                {
                    await _dockerClient.Networks.ConnectNetworkAsync(
                        TRAEFIK_NETWORK,
                        new NetworkConnectParameters { Container = containerId });
                    
                    Console.WriteLine($"🔗 Connected {containerId} to {TRAEFIK_NETWORK}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Warning: Could not connect to network: {ex.Message}");
            }
        }

        private string SanitizeString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;
            
            return input.Replace(".", "-")
                    .Replace("_", "-")
                    .ToLower()
                    .Trim('-');
        }

        public async Task<Dictionary<string, object>> GetStatsAsync(string userId = null)
        {
            var routes = await GetRoutesAsync(userId);
            var containers = routes.Select(r => r.ContainerId).Distinct().Count();

            return new Dictionary<string, object>
            {
                ["totalRoutes"] = routes.Count,
                ["activeRoutes"] = routes.Count(r => r.Status == "active"),
                ["containers"] = containers,
                ["lastUpdated"] = DateTime.UtcNow
            };
        }

        private async Task<List<PrewarmInfo>> GetPrewarmListAsync()
        {
            var json = await File.ReadAllTextAsync(_prewarmDbPath);
            return JsonSerializer.Deserialize<List<PrewarmInfo>>(json) ?? new List<PrewarmInfo>();
        }

        private async Task SavePrewarmListAsync(List<PrewarmInfo> list)
        {
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_prewarmDbPath, json);
        }

        private async Task AddPrewarmAsync(PrewarmInfo info)
        {
            var list = await GetPrewarmListAsync();
            list.RemoveAll(p => p.UserId == info.UserId);
            list.Add(info);
            await SavePrewarmListAsync(list);
        }

        private async Task<bool> PrewarmExistsAsync(string userId)
        {
            var list = await GetPrewarmListAsync();
            return list.Any(p => p.UserId == userId);
        }

        private async Task RemovePrewarmAsync(string userId)
        {
            var list = await GetPrewarmListAsync();
            list.RemoveAll(p => p.UserId == userId);
            await SavePrewarmListAsync(list);
        }

    }

        // ============= API CONTROLLERS =============
        [ApiController]
        [Route("api/expose")]
        public class ExposePortController : ControllerBase
        {
            private readonly TraefikFileRouterService _routerService;

            public ExposePortController(TraefikFileRouterService routerService)
            {
                _routerService = routerService;
            }

            [HttpPost]
            public async Task<IActionResult> ExposePort([FromBody] ExposeRequest request)
            {
                try
                {
                    if (string.IsNullOrEmpty(request.UserId))
                    {
                        return BadRequest(new { error = "UserId is required" });
                    }

                    var response = await _routerService.ExposePortAsync(request);
                    return Ok(response);
                }
                catch (ArgumentException ex)
                {
                    return BadRequest(new { error = ex.Message });
                }
                catch (InvalidOperationException ex)
                {
                    return Conflict(new { error = ex.Message });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Error exposing port: {ex}");
                    return StatusCode(500, new { error = "Internal server error", details = ex.Message });
                }
            }

            [HttpGet]
            public async Task<IActionResult> GetRoutes([FromQuery] string userId = null)
            {
                try
                {
                    var routes = await _routerService.GetRoutesAsync(userId);
                    return Ok(routes);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Error getting routes: {ex}");
                    return StatusCode(500, new { error = ex.Message });
                }
            }

            [HttpGet("{routeId}")]
            public async Task<IActionResult> GetRoute(string routeId, [FromQuery] string userId = null)
            {
                try
                {
                    var route = await _routerService.GetRouteByIdAsync(routeId, userId);
                    if (route == null)
                    {
                        return NotFound(new { error = "Route not found" });
                    }
                    return Ok(route);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Error getting route: {ex}");
                    return StatusCode(500, new { error = ex.Message });
                }
            }

            [HttpDelete("{routeId}")]
            public async Task<IActionResult> DeleteRoute(string routeId, [FromQuery] string userId)
            {
                try
                {
                    if (string.IsNullOrEmpty(userId))
                    {
                        return BadRequest(new { error = "UserId is required" });
                    }

                    var deleted = await _routerService.DeleteRouteAsync(routeId, userId);
                    if (!deleted)
                    {
                        return NotFound(new { error = "Route not found" });
                    }

                    return Ok(new { message = "Route deleted successfully" });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Error deleting route: {ex}");
                    return StatusCode(500, new { error = ex.Message });
                }
            }

            [HttpGet("stats")]
            public async Task<IActionResult> GetStats([FromQuery] string userId = null)
            {
                try
                {
                    var stats = await _routerService.GetStatsAsync(userId);
                    return Ok(stats);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Error getting stats: {ex}");
                    return StatusCode(500, new { error = ex.Message });
                }
            }
        }

        // ============= PROGRAM STARTUP =============
        public class Program
        {
            public static void Main(string[] args)
            {
                var builder = WebApplication.CreateBuilder(args);

                // Add controllers
                builder.Services.AddControllers();
                
                // Add CORS
                builder.Services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy =>
                    {
                        policy.AllowAnyOrigin()
                            .AllowAnyMethod()
                            .AllowAnyHeader();
                    });
                });

                // Register TraefikFileRouterService as singleton
                builder.Services.AddSingleton(sp =>
                {
                    var env = sp.GetRequiredService<IWebHostEnvironment>();
                    var dynamicPath = Path.Combine(env.ContentRootPath, "dynamic");
                    
                    return new TraefikFileRouterService(
                        dockerHost: "unix:///var/run/docker.sock",
                        domain: "dock8s.in",
                        dynamicConfigPath: dynamicPath,
                        useHttps: true
                    );
                });

                var app = builder.Build();

                app.UseCors();
                app.MapControllers();

                // Health check endpoint
                app.MapGet("/api/health", () => Results.Ok(new 
                { 
                    status = "healthy",
                    service = "DinD Port Router",
                    timestamp = DateTime.UtcNow 
                }));

                Console.WriteLine("🚀 DinD Port Router API Started");
                Console.WriteLine("📍 Listening on: http://localhost:5000");
                Console.WriteLine("📁 Dynamic configs: ./dynamic/");
                Console.WriteLine();
                Console.WriteLine("Available Endpoints:");
                Console.WriteLine("  POST   /api/expose          - Expose a port");
                Console.WriteLine("  GET    /api/expose          - List all routes");
                Console.WriteLine("  GET    /api/expose/{id}     - Get route details");
                Console.WriteLine("  DELETE /api/expose/{id}     - Delete a route");
                Console.WriteLine("  GET    /api/expose/stats    - Get statistics");
                Console.WriteLine("  GET    /api/health          - Health check");

                app.Run("http://localhost:5295");
            }
        }
    }
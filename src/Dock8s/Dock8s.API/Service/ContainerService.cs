using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Dock8s.API.Controllers;

namespace Dock8s.API.Service
{
    // ============= MODELS =============
    
    public class ContainerSession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public required string UserId { get; set; }
        public required string ContainerId { get; set; }
        public required string ContainerName { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? LastActivityAt { get; set; }
        public string Status { get; set; } = "active"; // active, expired, terminated
        public int CpuLimit { get; set; } = 1; // vCPUs
        public long MemoryLimit { get; set; } = 2147483648; // 2GB in bytes
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    public class ContainerConfig
    {
        public int DefaultTtlMinutes { get; set; } = 30;
        public int IdleTimeoutMinutes { get; set; } = 15;
        public int MaxConcurrentContainersPerUser { get; set; } = 1;
        public int CpuLimit { get; set; } = 1;
        public long MemoryLimitBytes { get; set; } = 2147483648; // 2GB
        public string SysboxRuntime { get; set; } = "sysbox-runc";
        public string NetworkName { get; set; } = "traefik-public";
        public string DindImage { get; set; } = "docker:dind";
    }

    public class CreateContainerRequest
    {
        public string JwtToken { get; set; }
        public int? TtlMinutes { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
    }

    public class ContainerResponse
    {
        public string SessionId { get; set; }
        public string ContainerId { get; set; }
        public string ContainerName { get; set; }
        public DateTime ExpiresAt { get; set; }
        public int RemainingMinutes { get; set; }
        public string Status { get; set; }
    }

    // ============= DATABASE CONTEXT =============
    
    public class DindDbContext : DbContext
    {
        public DbSet<ContainerSession> ContainerSession { get; set; }

        public DindDbContext(DbContextOptions<DindDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ContainerSession>()
                .HasKey(s => s.Id);
            
            modelBuilder.Entity<ContainerSession>()
                .HasIndex(s => s.UserId);
            
            modelBuilder.Entity<ContainerSession>()
                .HasIndex(s => s.ContainerId);

                modelBuilder.Entity<ContainerSession>().ToTable("ContainerSession");

            modelBuilder.Entity<ContainerSession>()
                .Property(s => s.Metadata)
                .HasColumnType("jsonb");

            modelBuilder.Entity<ContainerSession>().Property(s => s.Id).HasColumnName("id");
            modelBuilder.Entity<ContainerSession>().Property(s => s.UserId).HasColumnName("userId");
            modelBuilder.Entity<ContainerSession>().Property(s => s.ContainerId).HasColumnName("containerId");
            modelBuilder.Entity<ContainerSession>().Property(s => s.ContainerName).HasColumnName("containerName");
            modelBuilder.Entity<ContainerSession>().Property(s => s.CreatedAt).HasColumnName("createdAt");
            modelBuilder.Entity<ContainerSession>().Property(s => s.ExpiresAt).HasColumnName("expiresAt");
            modelBuilder.Entity<ContainerSession>().Property(s => s.LastActivityAt).HasColumnName("lastActivityAt");
            modelBuilder.Entity<ContainerSession>().Property(s => s.Status).HasColumnName("status");
            modelBuilder.Entity<ContainerSession>().Property(s => s.CpuLimit).HasColumnName("cpuLimit");
            modelBuilder.Entity<ContainerSession>().Property(s => s.MemoryLimit).HasColumnName("memoryLimit");
            modelBuilder.Entity<ContainerSession>().Property(s => s.Metadata).HasColumnName("metadata");

        }
    }

    // ============= JWT SERVICE =============
    
    public class JwtService
    {
        private readonly string _secretKey;
        private readonly string _issuer;

        public JwtService(string secretKey, string issuer = "Dock8s")
        {
            _secretKey = secretKey;
            _issuer = issuer;
        }

        public string? ValidateTokenAndGetUserId(string token)
        {
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var key = Encoding.ASCII.GetBytes(_secretKey);

                tokenHandler.ValidateToken(token, new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateIssuer = true,
                    ValidIssuer = _issuer,
                    ValidateAudience = false,
                    ClockSkew = TimeSpan.Zero
                }, out SecurityToken validatedToken);

                var jwtToken = (JwtSecurityToken)validatedToken;
                var userId = jwtToken.Claims.First(x => x.Type == "sub" || x.Type == "userId").Value;

                return userId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Token validation failed: {ex.Message}");
                return null;
            }
        }
    }

    // ============= DIND CONTAINER MANAGER =============
    
    public class DindContainerManager
    {
        private readonly DockerClient _dockerClient;
        private readonly IServiceScopeFactory _serviceScopeFactory; // Changed from DbContext
        private readonly JwtService _jwtService;
        private readonly ContainerConfig _config;
        private readonly TraefikFileRouterService _routerService;
        private readonly Timer _cleanupTimer;

        public DindContainerManager(
            IServiceScopeFactory serviceScopeFactory, // Changed parameter
            JwtService jwtService,
            ContainerConfig config,
            TraefikFileRouterService routerService,
            string dockerHost = "tcp://localhost:2375")
        {
            _dockerClient = new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();
            _serviceScopeFactory = serviceScopeFactory; // Store scope factory
            _jwtService = jwtService;
            _config = config;
            _routerService = routerService;

            // Start background cleanup task
            _cleanupTimer = new Timer(
                    CleanupExpiredContainers,
                    null,
                    TimeSpan.Zero,                 // start immediately
                    TimeSpan.FromSeconds(10)       // run every 10 seconds
                );
        }

        public async Task<ContainerResponse> GetOrCreateContainerAsync(CreateContainerRequest request)
        {
            // Validate JWT and extract userId
            var userId = _jwtService.ValidateTokenAndGetUserId(request.JwtToken);
            if (string.IsNullOrEmpty(userId))
            {
                throw new UnauthorizedAccessException("Invalid or expired token");
            }

            // Create a new scope for this request
            using var scope = _serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DindDbContext>();

            // Check for existing active container
            var existingSession = await dbContext.ContainerSession
                .Where(s => s.UserId == userId && s.Status == "active")
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefaultAsync();

            if (existingSession != null)
            {
                // Verify container still exists
                var containerExists = await VerifyContainerExistsAsync(existingSession.ContainerId);
                
                if (containerExists)
                {
                    // Update last activity
                    existingSession.LastActivityAt = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync();

                    return MapToResponse(existingSession);
                }
                else
                {
                    // Container gone, clean up session
                    existingSession.Status = "terminated";
                    await dbContext.SaveChangesAsync();
                }
            }

            // Check concurrent container limit
            var activeCount = await dbContext.ContainerSession
                .CountAsync(s => s.UserId == userId && s.Status == "active");

            if (activeCount >= _config.MaxConcurrentContainersPerUser)
            {
                throw new InvalidOperationException($"Maximum concurrent containers ({_config.MaxConcurrentContainersPerUser}) reached");
            }

            // Create new container
            return await CreateNewContainerAsync(userId, request, dbContext);
        }

        private async Task<ContainerResponse> CreateNewContainerAsync(string userId, CreateContainerRequest request, DindDbContext dbContext)
        {
            var containerName = $"dind-{SanitizeUserId(userId)}";
            var ttlMinutes = request.TtlMinutes ?? _config.DefaultTtlMinutes;

            Console.WriteLine($"🚀 Creating DinD container for user: {userId}");

            try
            {
                // Create container with sysbox runtime
                var createResponse = await _dockerClient.Containers.CreateContainerAsync(
                    new CreateContainerParameters
                    {
                        Image = _config.DindImage,
                        Name = containerName,
                        Hostname = containerName,
                        Env = new List<string>
                        {
                            "DOCKER_TLS_CERTDIR=",
                            $"USER_ID={userId}",
                            $"SESSION_TTL={ttlMinutes}"
                        },
                        HostConfig = new HostConfig
                        {
                            // Runtime = _config.SysboxRuntime,
                            Privileged = true,
                            NanoCPUs = _config.CpuLimit * 1_000_000_000L,
                            Memory = _config.MemoryLimitBytes,
                            MemorySwap = _config.MemoryLimitBytes,
                            NetworkMode = _config.NetworkName,
                            RestartPolicy = new RestartPolicy
                            {
                                Name = RestartPolicyKind.No
                            }
                        },
                        Labels = new Dictionary<string, string>
                        {
                            { "dock8s.user-id", userId },
                            { "dock8s.managed", "true" },
                            { "dock8s.created-at", DateTime.UtcNow.ToString("o") },
                            { "dock8s.expires-at", DateTime.UtcNow.AddMinutes(ttlMinutes).ToString("o") }
                        }
                    });

                var containerId = createResponse.ID;

                // Start container
                await _dockerClient.Containers.StartContainerAsync(containerId, new ContainerStartParameters());

                Console.WriteLine($"✅ Container created: {containerName} ({containerId})");

                // Create session record
                var session = new ContainerSession
                {
                    UserId = userId,
                    ContainerId = containerId,
                    ContainerName = containerName,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(ttlMinutes),
                    LastActivityAt = DateTime.UtcNow,
                    CpuLimit = _config.CpuLimit,
                    MemoryLimit = _config.MemoryLimitBytes,
                    Metadata = request.Metadata ?? new Dictionary<string, string>()
                };

                dbContext.ContainerSession.Add(session);
                await dbContext.SaveChangesAsync();

                return MapToResponse(session);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to create container: {ex.Message}");
                throw;
            }
        }

        public async Task<bool> ExtendSessionAsync(string jwtToken, int additionalMinutes)
        {
            var userId = _jwtService.ValidateTokenAndGetUserId(jwtToken);
            if (string.IsNullOrEmpty(userId))
            {
                throw new UnauthorizedAccessException("Invalid token");
            }

            // Create a new scope
            using var scope = _serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DindDbContext>();

            var session = await dbContext.ContainerSession
                .Where(s => s.UserId == userId && s.Status == "active")
                .FirstOrDefaultAsync();

            if (session == null)
            {
                return false;
            }

            session.ExpiresAt = DateTime.SpecifyKind(session.ExpiresAt, DateTimeKind.Utc)
                    .AddMinutes(additionalMinutes);
            session.LastActivityAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();

            Console.WriteLine($"⏱️  Extended session for {userId} by {additionalMinutes} minutes");
            return true;
        }

        public async Task<bool> TerminateContainerAsync(string jwtToken)
        {
            var userId = _jwtService.ValidateTokenAndGetUserId(jwtToken);
            if (string.IsNullOrEmpty(userId))
            {
                throw new UnauthorizedAccessException("Invalid token");
            }

            // Create a new scope
            using var scope = _serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DindDbContext>();

            var session = await dbContext.ContainerSession
                .Where(s => s.UserId == userId && s.Status == "active")
                .FirstOrDefaultAsync();

            if (session == null)
            {
                return false;
            }

            await CleanupContainerAsync(session, dbContext);
            return true;
        }

        private async Task CleanupContainerAsync(ContainerSession session, DindDbContext dbContext)
        {
            Console.WriteLine($"🧹 Cleaning up container for user: {session.UserId}");

            try
            {
                // 1. Delete all port mappings/routes
                var routes = await _routerService.GetRoutesAsync(session.UserId);
                foreach (var route in routes)
                {
                    try
                    {
                        await _routerService.DeleteRouteAsync(route.Id, session.UserId);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️  Failed to delete route {route.Id}: {ex.Message}");
                    }
                }

                // 2. Stop container
                try
                {
                    await _dockerClient.Containers.StopContainerAsync(
                        session.ContainerId,
                        new ContainerStopParameters { WaitBeforeKillSeconds = 10 });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️  Failed to stop container: {ex.Message}");
                }

                // 3. Remove container
                try
                {
                    await _dockerClient.Containers.RemoveContainerAsync(
                        session.ContainerId,
                        new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️  Failed to remove container: {ex.Message}");
                }

                // 4. Update session status
                session.Status = "terminated";
                await dbContext.SaveChangesAsync();

                Console.WriteLine($"✅ Cleanup completed for user: {session.UserId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Cleanup error: {ex.Message}");
                throw;
            }
        }

        private async void CleanupExpiredContainers(object? state)
        {
            try
            {
                // Create a new scope for this background task
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DindDbContext>();

                var now = DateTime.UtcNow;
                Console.WriteLine($"🧹 Running cleanup task at {now:o}");
                
                // Find expired sessions
                var expiredSessions = await dbContext.ContainerSession
                    .Where(s => s.Status == "active" && s.ExpiresAt <= now)
                    .ToListAsync();

                foreach (var session in expiredSessions)
                {
                    Console.WriteLine($"⏰ Session expired for user: {session.UserId}");
                    await CleanupContainerAsync(session, dbContext);
                }

                // Check for idle sessions
                var idleThreshold = now.AddMinutes(-_config.IdleTimeoutMinutes);
                var idleSessions = await dbContext.ContainerSession
                    .Where(s => s.Status == "active" && 
                               s.LastActivityAt.HasValue && 
                               s.LastActivityAt.Value <= idleThreshold)
                    .ToListAsync();

                foreach (var session in idleSessions)
                {
                    Console.WriteLine($"💤 Idle session timeout for user: {session.UserId}");
                    await CleanupContainerAsync(session, dbContext);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Cleanup task error: {ex.Message}");
            }
        }

        private async Task<bool> VerifyContainerExistsAsync(string containerId)
        {
            try
            {
                await _dockerClient.Containers.InspectContainerAsync(containerId);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private ContainerResponse MapToResponse(ContainerSession session)
        {
            var remainingMinutes = (int)(session.ExpiresAt - DateTime.UtcNow).TotalMinutes;
            
            return new ContainerResponse
            {
                SessionId = session.Id,
                ContainerId = session.ContainerId,
                ContainerName = session.ContainerName,
                ExpiresAt = session.ExpiresAt,
                RemainingMinutes = Math.Max(0, remainingMinutes),
                Status = session.Status
            };
        }

        private string SanitizeUserId(string userId)
        {
            return userId.Replace("@", "-")
                        .Replace(".", "-")
                        .Replace("_", "-")
                        .ToLower()
                        .Trim('-');
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
            _dockerClient?.Dispose();
        }
    }
}
// using Docker.DotNet;
// using Docker.DotNet.Models;
// using Microsoft.EntityFrameworkCore;
// using System.IdentityModel.Tokens.Jwt;
// using Microsoft.IdentityModel.Tokens;
// using System.Text;

// namespace Dock8s.Infrastructure.Service
// {
//     // ============= MODELS =============
    
//     public class ContainerSession
//     {
//         public string Id { get; set; } = Guid.NewGuid().ToString();
//         public string UserId { get; set; }
//         public string ContainerId { get; set; }
//         public string ContainerName { get; set; }
//         public DateTime CreatedAt { get; set; }
//         public DateTime ExpiresAt { get; set; }
//         public DateTime? LastActivityAt { get; set; }
//         public string Status { get; set; } = "active"; // active, expired, terminated
//         public int CpuLimit { get; set; } = 1; // vCPUs
//         public long MemoryLimit { get; set; } = 2147483648; // 2GB in bytes
//         public Dictionary<string, string> Metadata { get; set; } = new();
//     }

//     public class ContainerConfig
//     {
//         public int DefaultTtlMinutes { get; set; } = 30;
//         public int IdleTimeoutMinutes { get; set; } = 15;
//         public int MaxConcurrentContainersPerUser { get; set; } = 1;
//         public int CpuLimit { get; set; } = 1;
//         public long MemoryLimitBytes { get; set; } = 2147483648; // 2GB
//         public string SysboxRuntime { get; set; } = "sysbox-runc";
//         public string NetworkName { get; set; } = "traefik-public";
//         public string DindImage { get; set; } = "docker:dind";
//     }

//     public class CreateContainerRequest
//     {
//         public string JwtToken { get; set; }
//         public int? TtlMinutes { get; set; }
//         public Dictionary<string, string>? Metadata { get; set; }
//     }

//     public class ContainerResponse
//     {
//         public string SessionId { get; set; }
//         public string ContainerId { get; set; }
//         public string ContainerName { get; set; }
//         public DateTime ExpiresAt { get; set; }
//         public int RemainingMinutes { get; set; }
//         public string Status { get; set; }
//     }

//     // ============= DATABASE CONTEXT =============
    
//     public class DindDbContext : DbContext
//     {
//         public DbSet<ContainerSession> ContainerSessions { get; set; }

//         public DindDbContext(DbContextOptions<DindDbContext> options) : base(options) { }

//         protected override void OnModelCreating(ModelBuilder modelBuilder)
//         {
//             modelBuilder.Entity<ContainerSession>()
//                 .HasKey(s => s.Id);
            
//             modelBuilder.Entity<ContainerSession>()
//                 .HasIndex(s => s.UserId);
            
//             modelBuilder.Entity<ContainerSession>()
//                 .HasIndex(s => s.ContainerId);
//         }
//     }

//     // ============= JWT SERVICE =============
    
//     public class JwtService
//     {
//         private readonly string _secretKey;
//         private readonly string _issuer;

//         public JwtService(string secretKey, string issuer = "Dock8s")
//         {
//             _secretKey = secretKey;
//             _issuer = issuer;
//         }

//         public string? ValidateTokenAndGetUserId(string token)
//         {
//             try
//             {
//                 var tokenHandler = new JwtSecurityTokenHandler();
//                 var key = Encoding.ASCII.GetBytes(_secretKey);

//                 tokenHandler.ValidateToken(token, new TokenValidationParameters
//                 {
//                     ValidateIssuerSigningKey = true,
//                     IssuerSigningKey = new SymmetricSecurityKey(key),
//                     ValidateIssuer = true,
//                     ValidIssuer = _issuer,
//                     ValidateAudience = false,
//                     ClockSkew = TimeSpan.Zero
//                 }, out SecurityToken validatedToken);

//                 var jwtToken = (JwtSecurityToken)validatedToken;
//                 var userId = jwtToken.Claims.First(x => x.Type == "sub" || x.Type == "userId").Value;

//                 return userId;
//             }
//             catch (Exception ex)
//             {
//                 Console.WriteLine($"❌ Token validation failed: {ex.Message}");
//                 return null;
//             }
//         }
//     }

//     // ============= DIND CONTAINER MANAGER =============
    
//     public class DindContainerManager
//     {
//         private readonly DockerClient _dockerClient;
//         private readonly DindDbContext _dbContext;
//         private readonly JwtService _jwtService;
//         private readonly ContainerConfig _config;
//         private readonly TraefikFileRouterService _routerService;
//         private readonly Timer _cleanupTimer;

//         public DindContainerManager(
//             DindDbContext dbContext,
//             JwtService jwtService,
//             ContainerConfig config,
//             TraefikFileRouterService routerService,
//             string dockerHost = "unix:///var/run/docker.sock")
//         {
//             _dockerClient = new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();
//             _dbContext = dbContext;
//             _jwtService = jwtService;
//             _config = config;
//             _routerService = routerService;

//             // Start background cleanup task
//             _cleanupTimer = new Timer(CleanupExpiredContainers, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
//         }

//         public async Task<ContainerResponse> GetOrCreateContainerAsync(CreateContainerRequest request)
//         {
//             // Validate JWT and extract userId
//             var userId = _jwtService.ValidateTokenAndGetUserId(request.JwtToken);
//             if (string.IsNullOrEmpty(userId))
//             {
//                 throw new UnauthorizedAccessException("Invalid or expired token");
//             }

//             // Check for existing active container
//             var existingSession = await _dbContext.ContainerSessions
//                 .Where(s => s.UserId == userId && s.Status == "active")
//                 .OrderByDescending(s => s.CreatedAt)
//                 .FirstOrDefaultAsync();

//             if (existingSession != null)
//             {
//                 // Verify container still exists
//                 var containerExists = await VerifyContainerExistsAsync(existingSession.ContainerId);
                
//                 if (containerExists)
//                 {
//                     // Update last activity
//                     existingSession.LastActivityAt = DateTime.UtcNow;
//                     await _dbContext.SaveChangesAsync();

//                     return MapToResponse(existingSession);
//                 }
//                 else
//                 {
//                     // Container gone, clean up session
//                     existingSession.Status = "terminated";
//                     await _dbContext.SaveChangesAsync();
//                 }
//             }

//             // Check concurrent container limit
//             var activeCount = await _dbContext.ContainerSessions
//                 .CountAsync(s => s.UserId == userId && s.Status == "active");

//             if (activeCount >= _config.MaxConcurrentContainersPerUser)
//             {
//                 throw new InvalidOperationException($"Maximum concurrent containers ({_config.MaxConcurrentContainersPerUser}) reached");
//             }

//             // Create new container
//             return await CreateNewContainerAsync(userId, request);
//         }

//         private async Task<ContainerResponse> CreateNewContainerAsync(string userId, CreateContainerRequest request)
//         {
//             var containerName = $"dind-{SanitizeUserId(userId)}";
//             var ttlMinutes = request.TtlMinutes ?? _config.DefaultTtlMinutes;

//             Console.WriteLine($"🚀 Creating DinD container for user: {userId}");

//             try
//             {
//                 // Create container with sysbox runtime
//                 var createResponse = await _dockerClient.Containers.CreateContainerAsync(
//                     new CreateContainerParameters
//                     {
//                         Image = _config.DindImage,
//                         Name = containerName,
//                         Hostname = containerName,
//                         Env = new List<string>
//                         {
//                             "DOCKER_TLS_CERTDIR=",
//                             $"USER_ID={userId}",
//                             $"SESSION_TTL={ttlMinutes}"
//                         },
//                         HostConfig = new HostConfig
//                         {
//                             Runtime = _config.SysboxRuntime,
//                             Privileged = false, // Sysbox doesn't need privileged mode
//                             NanoCPUs = _config.CpuLimit * 1_000_000_000L, // Convert to nano CPUs
//                             Memory = _config.MemoryLimitBytes,
//                             MemorySwap = _config.MemoryLimitBytes, // No swap
//                             NetworkMode = _config.NetworkName,
//                             RestartPolicy = new RestartPolicy
//                             {
//                                 Name = RestartPolicyKind.No
//                             },
//                             // Storage limits
//                             StorageOpt = new Dictionary<string, string>
//                             {
//                                 { "size", "10G" } // 10GB storage limit
//                             }
//                         },
//                         Labels = new Dictionary<string, string>
//                         {
//                             { "dock8s.user-id", userId },
//                             { "dock8s.managed", "true" },
//                             { "dock8s.created-at", DateTime.UtcNow.ToString("o") },
//                             { "dock8s.expires-at", DateTime.UtcNow.AddMinutes(ttlMinutes).ToString("o") }
//                         }
//                     });

//                 var containerId = createResponse.ID;

//                 // Start container
//                 await _dockerClient.Containers.StartContainerAsync(containerId, new ContainerStartParameters());

//                 Console.WriteLine($"✅ Container created: {containerName} ({containerId})");

//                 // Create session record
//                 var session = new ContainerSession
//                 {
//                     UserId = userId,
//                     ContainerId = containerId,
//                     ContainerName = containerName,
//                     CreatedAt = DateTime.UtcNow,
//                     ExpiresAt = DateTime.UtcNow.AddMinutes(ttlMinutes),
//                     LastActivityAt = DateTime.UtcNow,
//                     CpuLimit = _config.CpuLimit,
//                     MemoryLimit = _config.MemoryLimitBytes,
//                     Metadata = request.Metadata ?? new Dictionary<string, string>()
//                 };

//                 _dbContext.ContainerSessions.Add(session);
//                 await _dbContext.SaveChangesAsync();

//                 return MapToResponse(session);
//             }
//             catch (Exception ex)
//             {
//                 Console.WriteLine($"❌ Failed to create container: {ex.Message}");
//                 throw;
//             }
//         }

//         public async Task<bool> ExtendSessionAsync(string jwtToken, int additionalMinutes)
//         {
//             var userId = _jwtService.ValidateTokenAndGetUserId(jwtToken);
//             if (string.IsNullOrEmpty(userId))
//             {
//                 throw new UnauthorizedAccessException("Invalid token");
//             }

//             var session = await _dbContext.ContainerSessions
//                 .Where(s => s.UserId == userId && s.Status == "active")
//                 .FirstOrDefaultAsync();

//             if (session == null)
//             {
//                 return false;
//             }

//             session.ExpiresAt = session.ExpiresAt.AddMinutes(additionalMinutes);
//             session.LastActivityAt = DateTime.UtcNow;
//             await _dbContext.SaveChangesAsync();

//             Console.WriteLine($"⏱️  Extended session for {userId} by {additionalMinutes} minutes");
//             return true;
//         }

//         public async Task<bool> TerminateContainerAsync(string jwtToken)
//         {
//             var userId = _jwtService.ValidateTokenAndGetUserId(jwtToken);
//             if (string.IsNullOrEmpty(userId))
//             {
//                 throw new UnauthorizedAccessException("Invalid token");
//             }

//             var session = await _dbContext.ContainerSessions
//                 .Where(s => s.UserId == userId && s.Status == "active")
//                 .FirstOrDefaultAsync();

//             if (session == null)
//             {
//                 return false;
//             }

//             await CleanupContainerAsync(session);
//             return true;
//         }

//         private async Task CleanupContainerAsync(ContainerSession session)
//         {
//             Console.WriteLine($"🧹 Cleaning up container for user: {session.UserId}");

//             try
//             {
//                 // 1. Delete all port mappings/routes
//                 var routes = await _routerService.GetRoutesAsync(session.UserId);
//                 foreach (var route in routes)
//                 {
//                     try
//                     {
//                         await _routerService.DeleteRouteAsync(route.Id, session.UserId);
//                     }
//                     catch (Exception ex)
//                     {
//                         Console.WriteLine($"⚠️  Failed to delete route {route.Id}: {ex.Message}");
//                     }
//                 }

//                 // 2. Stop container
//                 try
//                 {
//                     await _dockerClient.Containers.StopContainerAsync(
//                         session.ContainerId,
//                         new ContainerStopParameters { WaitBeforeKillSeconds = 10 });
//                 }
//                 catch (Exception ex)
//                 {
//                     Console.WriteLine($"⚠️  Failed to stop container: {ex.Message}");
//                 }

//                 // 3. Remove container
//                 try
//                 {
//                     await _dockerClient.Containers.RemoveContainerAsync(
//                         session.ContainerId,
//                         new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
//                 }
//                 catch (Exception ex)
//                 {
//                     Console.WriteLine($"⚠️  Failed to remove container: {ex.Message}");
//                 }

//                 // 4. Update session status
//                 session.Status = "terminated";
//                 await _dbContext.SaveChangesAsync();

//                 Console.WriteLine($"✅ Cleanup completed for user: {session.UserId}");
//             }
//             catch (Exception ex)
//             {
//                 Console.WriteLine($"❌ Cleanup error: {ex.Message}");
//                 throw;
//             }
//         }

//         private async void CleanupExpiredContainers(object? state)
//         {
//             try
//             {
//                 var now = DateTime.UtcNow;
                
//                 // Find expired sessions
//                 var expiredSessions = await _dbContext.ContainerSessions
//                     .Where(s => s.Status == "active" && s.ExpiresAt <= now)
//                     .ToListAsync();

//                 foreach (var session in expiredSessions)
//                 {
//                     Console.WriteLine($"⏰ Session expired for user: {session.UserId}");
//                     await CleanupContainerAsync(session);
//                 }

//                 // Check for idle sessions
//                 var idleThreshold = now.AddMinutes(-_config.IdleTimeoutMinutes);
//                 var idleSessions = await _dbContext.ContainerSessions
//                     .Where(s => s.Status == "active" && 
//                                s.LastActivityAt.HasValue && 
//                                s.LastActivityAt.Value <= idleThreshold)
//                     .ToListAsync();

//                 foreach (var session in idleSessions)
//                 {
//                     Console.WriteLine($"💤 Idle session timeout for user: {session.UserId}");
//                     await CleanupContainerAsync(session);
//                 }
//             }
//             catch (Exception ex)
//             {
//                 Console.WriteLine($"❌ Cleanup task error: {ex.Message}");
//             }
//         }

//         private async Task<bool> VerifyContainerExistsAsync(string containerId)
//         {
//             try
//             {
//                 await _dockerClient.Containers.InspectContainerAsync(containerId);
//                 return true;
//             }
//             catch
//             {
//                 return false;
//             }
//         }

//         private ContainerResponse MapToResponse(ContainerSession session)
//         {
//             var remainingMinutes = (int)(session.ExpiresAt - DateTime.UtcNow).TotalMinutes;
            
//             return new ContainerResponse
//             {
//                 SessionId = session.Id,
//                 ContainerId = session.ContainerId,
//                 ContainerName = session.ContainerName,
//                 ExpiresAt = session.ExpiresAt,
//                 RemainingMinutes = Math.Max(0, remainingMinutes),
//                 Status = session.Status
//             };
//         }

//         private string SanitizeUserId(string userId)
//         {
//             return userId.Replace("@", "-")
//                         .Replace(".", "-")
//                         .Replace("_", "-")
//                         .ToLower()
//                         .Trim('-');
//         }

//         public void Dispose()
//         {
//             _cleanupTimer?.Dispose();
//             _dockerClient?.Dispose();
//         }
//     }
// }
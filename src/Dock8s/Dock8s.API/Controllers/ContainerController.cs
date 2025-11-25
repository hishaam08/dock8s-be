using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Dock8s.API.Service;
using Microsoft.EntityFrameworkCore;

namespace Dock8s.API.Controllers
{
    [ApiController]
    [Route("api/container")]
    public class ContainerController : ControllerBase
    {
        private readonly DindContainerManager _containerManager;
        private readonly DindDbContext _dbContext;

        public ContainerController(
            DindContainerManager containerManager,
            DindDbContext dbContext)
        {
            _containerManager = containerManager;
            _dbContext = dbContext;
        }

        /// <summary>
        /// Get or create a DinD container for the authenticated user
        /// </summary>
        [HttpPost("session")]
        public async Task<IActionResult> GetOrCreateSession([FromBody] CreateContainerRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.JwtToken))
                {
                    return BadRequest(new { error = "JWT token is required" });
                }

                var response = await _containerManager.GetOrCreateContainerAsync(request);
                return Ok(response);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error creating/getting session: {ex}");
                return StatusCode(500, new { error = "Internal server error", details = ex.Message });
            }
        }

        /// <summary>
        /// Get current session status
        /// </summary>
        [HttpGet("session")]
        public async Task<IActionResult> GetSessionStatus([FromHeader(Name = "Authorization")] string authorization)
        {
            try
            {
                var token = ExtractBearerToken(authorization);
                if (string.IsNullOrEmpty(token))
                {
                    return BadRequest(new { error = "Bearer token required" });
                }

                var request = new CreateContainerRequest { JwtToken = token };
                var response = await _containerManager.GetOrCreateContainerAsync(request);
                
                return Ok(response);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error getting session: {ex}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Extend session TTL
        /// </summary>
        [HttpPost("session/extend")]
        public async Task<IActionResult> ExtendSession(
            [FromHeader(Name = "Authorization")] string authorization,
            [FromBody] ExtendSessionRequest request)
        {
            try
            {
                var token = ExtractBearerToken(authorization);
                if (string.IsNullOrEmpty(token))
                {
                    return BadRequest(new { error = "Bearer token required" });
                }

                var additionalMinutes = request.AdditionalMinutes > 0 ? request.AdditionalMinutes : 30;
                var success = await _containerManager.ExtendSessionAsync(token, additionalMinutes);

                if (!success)
                {
                    return NotFound(new { error = "No active session found" });
                }

                return Ok(new { 
                    message = "Session extended successfully",
                    additionalMinutes = additionalMinutes
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error extending session: {ex}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Manually terminate session and cleanup resources
        /// </summary>
        [HttpDelete("session")]
        public async Task<IActionResult> TerminateSession([FromHeader(Name = "Authorization")] string authorization)
        {
            try
            {
                var token = ExtractBearerToken(authorization);
                if (string.IsNullOrEmpty(token))
                {
                    return BadRequest(new { error = "Bearer token required" });
                }

                var success = await _containerManager.TerminateContainerAsync(token);

                if (!success)
                {
                    return NotFound(new { error = "No active session found" });
                }

                return Ok(new { message = "Session terminated successfully" });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error terminating session: {ex}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get session history for authenticated user
        /// </summary>
        [HttpGet("history")]
        public async Task<IActionResult> GetSessionHistory(
            [FromHeader(Name = "Authorization")] string authorization,
            [FromQuery] int limit = 10)
        {
            try
            {
                var token = ExtractBearerToken(authorization);
                if (string.IsNullOrEmpty(token))
                {
                    return BadRequest(new { error = "Bearer token required" });
                }

                var jwtService = HttpContext.RequestServices.GetRequiredService<JwtService>();
                var userId = jwtService.ValidateTokenAndGetUserId(token);

                if (string.IsNullOrEmpty(userId))
                {
                    return Unauthorized(new { error = "Invalid token" });
                }

                var sessions = await _dbContext.ContainerSession
                    .Where(s => s.UserId == userId)
                    .OrderByDescending(s => s.CreatedAt)
                    .Take(limit)
                    .Select(s => new
                    {
                        s.Id,
                        s.ContainerName,
                        s.CreatedAt,
                        s.ExpiresAt,
                        s.Status,
                        DurationMinutes = (s.ExpiresAt - s.CreatedAt).TotalMinutes
                    })
                    .ToListAsync();

                return Ok(sessions);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error getting history: {ex}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Heartbeat endpoint to keep session alive
        /// </summary>
        [HttpPost("heartbeat")]
        public async Task<IActionResult> Heartbeat([FromHeader(Name = "Authorization")] string authorization)
        {
            try
            {
                var token = ExtractBearerToken(authorization);
                if (string.IsNullOrEmpty(token))
                {
                    return BadRequest(new { error = "Bearer token required" });
                }

                var jwtService = HttpContext.RequestServices.GetRequiredService<JwtService>();
                var userId = jwtService.ValidateTokenAndGetUserId(token);

                if (string.IsNullOrEmpty(userId))
                {
                    return Unauthorized(new { error = "Invalid token" });
                }

                var session = await _dbContext.ContainerSession
                    .Where(s => s.UserId == userId && s.Status == "active")
                    .FirstOrDefaultAsync();

                if (session == null)
                {
                    return NotFound(new { error = "No active session" });
                }

                session.LastActivityAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();

                var remaining = (int)(session.ExpiresAt - DateTime.UtcNow).TotalMinutes;

                return Ok(new
                {
                    sessionId = session.Id,
                    remainingMinutes = Math.Max(0, remaining),
                    status = "alive"
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error in heartbeat: {ex}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private string? ExtractBearerToken(string? authorization)
        {
            if (string.IsNullOrEmpty(authorization))
                return null;

            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return authorization.Substring("Bearer ".Length).Trim();
            }

            return authorization;
        }
    }

    public class ExtendSessionRequest
    {
        public int AdditionalMinutes { get; set; } = 30;
    }
}
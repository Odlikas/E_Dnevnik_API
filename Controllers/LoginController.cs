using E_Dnevnik_API.Database;
using E_Dnevnik_API.Database.Models;
using E_Dnevnik_API.Models.ScrapeSubjects;
using E_Dnevnik_API.ScrapingServices;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Mvc;

namespace E_Dnevnik_API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class LoginController : ControllerBase
    {
        private readonly SessionStore _sessionStore;
        private readonly LoginBruteForceProtection _bruteForce;
        private readonly ILogger<LoginController> _logger;
        private readonly AppDbContext _db;

        public LoginController(
            SessionStore sessionStore,
            LoginBruteForceProtection bruteForce,
            ILogger<LoginController> logger,
            AppDbContext db
        )
        {
            _sessionStore = sessionStore;
            _bruteForce = bruteForce;
            _logger = logger;
            _db = db;
        }

        [HttpPost]
        public async Task<ActionResult> Login([FromBody] ScrapeRequest request)
        {
            if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password))
                return BadRequest("Email i lozinka moraju biti uneseni.");

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (_bruteForce.IsBlocked(request.Email))
            {
                _logger.LogWarning(
                    "login blokiran (brute force) | email={Email} ip={Ip}",
                    request.Email,
                    ip
                );
                return StatusCode(
                    StatusCodes.Status429TooManyRequests,
                    "previše neuspjelih pokušaja prijave. pokušaj ponovo za 15 minuta."
                );
            }

            LoginResult result;
            try
            {
                result = await EduHrLoginService.LoginAsync(request.Email, request.Password);
            }
            catch (CarnetPausedException ex)
            {
                // nije neuspjela prijava korisnika - ne broji se u brute force
                Response.Headers.RetryAfter = ex.RetryAfterSeconds.ToString();
                return StatusCode(ex.StatusCode, ex.Message);
            }

            if (result.Client is null)
            {
                _bruteForce.RecordFailure(request.Email);
                _logger.LogWarning(
                    "login neuspješan | email={Email} ip={Ip} status={Status}",
                    request.Email,
                    ip,
                    result.StatusCode
                );
                return StatusCode(result.StatusCode, result.Error);
            }

            _bruteForce.RecordSuccess(request.Email);
            _logger.LogInformation("login uspješan | email={Email} ip={Ip}", request.Email, ip);
            result.Client.Dispose();

            var token = _sessionStore.CreateSession(result.Cookies!, request.Email);

            // Store token in Postgres so the background job can identify active sessions
            var cache = await _db.StudentCache.FindAsync(request.Email);
            if (cache == null)
            {
                cache = new StudentCache { Email = request.Email };
                _db.StudentCache.Add(cache);
            }
            cache.ActiveToken = token;
            cache.TokenStoredAt = DateTime.UtcNow;
            cache.LastActiveAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            // Generate Firebase custom token so Flutter can sign into Firebase Auth.
            // UID = sha256(email)[..28] — stable, non-PII, consistent across logins.
            string? firebaseToken = null;
            var uid = Convert
                .ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(request.Email.ToLowerInvariant())
                    )
                )
                .ToLower()[..28];

            if (FirebaseApp.DefaultInstance != null)
            {
                try
                {
                    firebaseToken = await FirebaseAuth.DefaultInstance.CreateCustomTokenAsync(uid);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "Firebase custom token generation failed: {Message}",
                        ex.Message
                    );
                }
            }

            return Ok(
                new
                {
                    token,
                    firebaseToken,
                    uid,
                    isOdlikasPlus = cache.IsOdlikasPlus,
                }
            );
        }

        [HttpDelete]
        public ActionResult Logout()
        {
            var authHeader = Request.Headers.Authorization.ToString();
            if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Unauthorized("Authorization header s Bearer tokenom je obavezan.");

            var token = authHeader["Bearer ".Length..].Trim();
            _sessionStore.RemoveSession(token);

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            _logger.LogInformation("odjava | ip={Ip}", ip);

            return Ok();
        }
    }
}

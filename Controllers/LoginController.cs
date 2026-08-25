using IndxCloudApi.Data;
using IndxCloudApi.Models;
using IndxCloudApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;


namespace IndxCloudApi.Controllers
{
    /// <summary>
    /// Endpoint to provide JWT bearer token based authentication
    /// </summary>
    [Route("api/login")]
    [ApiController]
    // Failed logins answer with RFC 9457 ProblemDetails carrying a "code" extension:
    // 401 invalidCredentials / userNotFound, 400 invalidArgument / passwordChangeFailed.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public class LoginController : ControllerBase
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IConfiguration _config;
        /// <summary>
        /// Constructor used by Identity sub system
        /// </summary>
        /// <param name="signInManager"></param>
        /// <param name="userManager"></param>
        /// <param name="config"></param>
        public LoginController(
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            IConfiguration config)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _config = config;
        }

        /// <summary>
        /// Get JWT token for the currently authenticated user (for Swagger/API testing).
        /// This endpoint is for users already logged in via the web UI who want to test the API.
        /// </summary>
        /// <returns>JWT token if user is authenticated</returns>
        [Authorize]
        [HttpGet("token")]
        public async Task<IActionResult> GetTokenAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return ApiProblems.Problem(StatusCodes.Status401Unauthorized, "userNotFound", "Authentication failed", "The authenticated user no longer exists.");

            // Generate JWT token for the current user
            var loginInfo = new LoginInfo
            {
                UserEmail = user.Email ?? "",
                UserPassWord = "" // Not needed for token generation
            };

            var tokenstring = GenerateJasonWebToken(loginInfo, user);
            return Ok(new { token = tokenstring });
        }

        /// <summary>
        /// After registering email and password, verification of email, and granted access by Indx,
        /// the user must start every session by this login method. Upon success it will return a JWT token.
        /// Every other endpoint of the search API will require this token passed for authentication.
        /// </summary>
        /// <param name="info">Login credentials containing UserEmail and UserPassWord</param>
        /// <returns>JWT token if successful</returns>
        [AllowAnonymous]
        [HttpPost]
        public async Task<IActionResult> PostAsync([FromBody] LoginInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.UserEmail) || string.IsNullOrEmpty(info.UserPassWord))
                return ApiProblems.InvalidArgument("UserEmail and UserPassWord are required.");

            // Find user by email
            var user = await _userManager.FindByEmailAsync(info.UserEmail);
            if (user == null)
                return ApiProblems.InvalidCredentials();

            // Verify password without signing in (no cookie created)
            var isPasswordValid = await _userManager.CheckPasswordAsync(user, info.UserPassWord);
            if (!isPasswordValid)
                return ApiProblems.InvalidCredentials();

            // Check if email is confirmed (based on your Identity configuration)
            //if (!user.EmailConfirmed)
            //    return Unauthorized("Email not confirmed");

            // Generate JWT token
            var tokenstring = GenerateJasonWebToken(info, user);

            return Ok(new { token = tokenstring, mustChangePassword = user.MustChangePassword });
        }

        /// <summary>
        /// Allows a user flagged with MustChangePassword to rotate to a new password
        /// of their choosing. On success the gate is lifted and a fresh full-access token
        /// can be obtained by re-logging in.
        /// </summary>
        /// <param name="request">Current and new password</param>
        /// <returns>Ok on success, BadRequest with Identity errors otherwise.</returns>
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [HttpPost("/api/change-password")]
        public async Task<IActionResult> ChangePasswordAsync([FromBody] ChangePasswordRequest request)
        {
            if (request == null
                || string.IsNullOrEmpty(request.CurrentPassword)
                || string.IsNullOrEmpty(request.NewPassword))
            {
                return ApiProblems.InvalidArgument("CurrentPassword and NewPassword are required.");
            }

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return ApiProblems.Problem(StatusCodes.Status401Unauthorized, "userNotFound", "Authentication failed", "The authenticated user no longer exists.");

            var result = await _userManager.ChangePasswordAsync(
                user, request.CurrentPassword, request.NewPassword);
            if (!result.Succeeded)
            {
                var problem = ApiProblems.Problem(StatusCodes.Status400BadRequest, "passwordChangeFailed", "Password change failed",
                    string.Join(" ", result.Errors.Select(e => e.Description)));
                ((Microsoft.AspNetCore.Mvc.ProblemDetails)problem.Value!).Extensions["errors"] = result.Errors.Select(e => e.Description).ToArray();
                return problem;
            }

            user.MustChangePassword = false;
            await _userManager.UpdateAsync(user);
            return Ok(new { changed = true });
        }

        /// <summary>Legacy route for <see cref="GetTokenAsync"/>.</summary>
        [Authorize]
        [HttpGet("GetToken"), ApiExplorerSettings(IgnoreApi = true)]
        public Task<IActionResult> GetTokenLegacyAsync() => GetTokenAsync();

        /// <summary>Legacy route for <see cref="ChangePasswordAsync"/>.</summary>
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [HttpPost("/api/changePassword"), ApiExplorerSettings(IgnoreApi = true)]
        public Task<IActionResult> ChangePasswordLegacyAsync([FromBody] ChangePasswordRequest request)
            => ChangePasswordAsync(request);

        private string GenerateJasonWebToken(LoginInfo info, ApplicationUser user)
        {
            var jwtKey = _config["Jwt:Key"];
            if (string.IsNullOrEmpty(jwtKey))
                throw new InvalidOperationException("JWT Key is not configured");

            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
    {
        new Claim(ClaimTypes.Email, info.UserEmail),
        new Claim(ClaimTypes.NameIdentifier, user.Id),
        new Claim(ClaimTypes.Name, user.UserName ?? info.UserEmail),
        new Claim(JwtRegisteredClaimNames.Sub, user.Id),
        new Claim(JwtRegisteredClaimNames.Email, info.UserEmail),
        new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
    };

            // Add roles if needed
            var roles = _userManager.GetRolesAsync(user).Result;
            foreach (var role in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            // Restricted-token claim - the password-change gate middleware reads this
            // to block calls to anything other than the password change endpoint.
            if (user.MustChangePassword)
            {
                claims.Add(new Claim("must_change_password", "true"));
            }

            var token = new JwtSecurityToken(
                issuer: _config["Jwt:Issuer"],
                audience: _config["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddHours(24),
                signingCredentials: credentials
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
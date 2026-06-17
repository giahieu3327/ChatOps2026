using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ChatOps.Models;
using ChatOps.Data;
using AppContext = ChatOps.Data.AppContext;
using ChatOps.Services.RedisService;

namespace ChatOps.Controllers
{
    [ApiController]
    [Route("api/login")]
    public class LoginController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IConfiguration _config;

        public LoginController(AppDbContext db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        [HttpPost]
        public async Task<IActionResult> Login([FromBody] LoginModel login)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u =>
                u.Username.ToLower() == login.Username.ToLower() ||
                (!string.IsNullOrEmpty(u.Email) && u.Email.ToLower() == login.Username.ToLower())
            );

            if (user == null || !BCrypt.Net.BCrypt.Verify(login.Password, user.PasswordHash))
            {
                return Unauthorized(new { message = "❌ Sai tài khoản hoặc mật khẩu." });
            }

            string cleanUsername = user.Username.Trim();
            string localServerIp = AppContext.ServerIP;

            // =====================================================
            // KIỂM TRA SESSION TRÊN TOÀN BỘ CỤM MULTI-NODE (CLUSTER)
            // =====================================================
            (bool hasSession, var sessionData) = await RedisUserSessionService.GetUserSessionAsync(cleanUsername);

            if (hasSession && sessionData != null && sessionData.Count > 0)
            {
                var activeNodes = sessionData.Values.ToList();
                return StatusCode(429, new
                {
                    message = $"❌ Tài khoản này đang hoạt động tại máy chủ có ID: {string.Join(", ", activeNodes)}"
                });
            }

            // =====================================================
            // KIỂM TRA TỔNG GIỚI HẠN KẾT NỐI TOÀN HỆ THỐNG
            // =====================================================
            (bool success, var sessionCounts) = await RedisUserSessionService.GetUserSessionCountAsync(isGetAll: true);

            if (success && sessionCounts != null)
            {
                long totalActiveSessions = sessionCounts.Values
                    .Select(v => long.TryParse(v, out var count) ? count : 0)
                    .Sum();

                if (totalActiveSessions >= 100)
                {
                    return StatusCode(503, new { message = "❌ Toàn hệ thống Cluster đã đạt giới hạn tối đa 100 kết nối đồng thời. Vui lòng thử lại sau." });
                }
            }

            // =====================================================
            // KHỞI TẠO JWT TOKEN ĐỊNH DANH (Chuẩn hóa Trim() cho Role)
            // =====================================================
            var claims = new[]
            {
                new Claim(ClaimTypes.Name, cleanUsername),
                new Claim(ClaimTypes.Role, user.Role.Trim()), 
                new Claim("userid", user.Id.ToString()),
                new Claim("email", user.Email?.Trim() ?? "")
            };

            var jwtKey = _config["Jwt:Key"] ?? throw new Exception("❌ Cấu hình mã hóa Jwt:Key bị thiếu trên hệ thống.");
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _config["Jwt:Issuer"],
                audience: _config["Jwt:Audience"],
                claims: claims,
                expires: DateTime.Now.AddDays(1),
                signingCredentials: creds
            );

            var jwt = new JwtSecurityTokenHandler().WriteToken(token);

            // =====================================================
            // ĐĂNG KÝ VÀO REDIS: ĐÁNH DẤU USER THUỘC QUẢN LÝ CỦA NODE NÀY
            // =====================================================
            try
            {
                await RedisUserSessionService.InsertUserSessionAsync(cleanUsername, localServerIp);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Redis Login Error] Không thể đăng ký phiên làm việc cho user {cleanUsername} lên Cluster: {ex.Message}");
            }

            return Ok(new { token = jwt });
        }
    }
}
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ChatOps.Data;
using AppContext = ChatOps.Data.AppContext;
using ChatOps.Services.RedisService;

namespace ChatOps.Controllers
{
    [ApiController]
    [Route("api/logout")]
    [Authorize] 
    public class LogoutController : ControllerBase
    {
        private readonly AppDbContext _db;

        public LogoutController(AppDbContext dbContext)
        {
            _db = dbContext;
        }

        [HttpPost]
        public async Task<IActionResult> Logout()
        {
            var username = User?.Identity?.Name;

            if (string.IsNullOrEmpty(username))
            {
                return BadRequest(new { message = "❌ Xác thực không hợp lệ hoặc phiên đã hết hạn." });
            }

            // Đồng bộ định dạng chuỗi: Sử dụng Trim() giống hệt cấu trúc lưu Token tại LoginController
            string cleanUsername = username.Trim();

            var currentUser = await _db.Users.FirstOrDefaultAsync(x => x.Username.ToLower() == cleanUsername.ToLower());
            if (currentUser == null)
            {
                return NotFound(new { message = "❌ Người dùng không tồn tại trên hệ thống." });
            }

            try
            {
                // Lấy IP cấu hình của Node hiện tại để định danh xóa chính xác trường dữ liệu trong Hash Redis
                string localIp = AppContext.ServerIP;

                // =====================================================
                // GIẢI PHÓNG PHIÊN TRÊN TOÀN CỤM CLUSTER (ĐỒNG BỘ REDIS)
                // =====================================================
                await RedisUserSessionService.DeleteUserSessionAsync(
                    username: cleanUsername
                );

                // Xóa lịch sử câu lệnh đệm trong Redis của User này
                await RedisHistoryService.DeleteHistoryAsync(cleanUsername);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [REDIS-LOGOUT-CLUSTER-ERROR] Lỗi giải phóng phiên cho {cleanUsername}: {ex.Message}");
            }

            return Ok(new { message = "✅ Đăng xuất thành công và đã giải phóng tài nguyên trên Cluster." });
        }
    }
}
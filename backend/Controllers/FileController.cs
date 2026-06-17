using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using ChatOps.Data;
using AppContext = ChatOps.Data.AppContext;
using ChatOps.Services.FileService;
using Microsoft.EntityFrameworkCore;
using ChatOps.Services.RedisService;

namespace ChatOps.Controllers
{
    [ApiController]
    [Route("api/file")]
    [Authorize]
    public class FileController : ControllerBase
    {
        #region ThamSo
        private readonly AppDbContext _db;
        #endregion

        #region Constructor
        public FileController(AppDbContext db)
        {
            _db = db;
        }
        #endregion

        [HttpGet("get")]
        public async Task<IActionResult> GetFile([FromQuery] string name)
        {
            var username = User.Identity?.Name;
            if (string.IsNullOrEmpty(username)) 
                return Unauthorized(new { message = $"❌ [Node {AppContext.ServerID}] Không xác thực được danh tính người dùng." });

            var cleanedUsername = username.Trim();
            var user = await _db.Users.FirstOrDefaultAsync(x => x.Username == cleanedUsername);
            if (user == null) 
                return Unauthorized(new { message = $"❌ [Node {AppContext.ServerID}] Tài khoản không tồn tại trên hệ thống." });

            var role = User.Claims.FirstOrDefault(x => x.Type == ClaimTypes.Role)?.Value ?? string.Empty;
            if (!HasFilePermission("get", role))
            {
                return StatusCode(403, new { message = $"❌ [Node {AppContext.ServerID}] Tài khoản thuộc nhóm [{role.ToUpper()}] không có quyền đọc file." });
            }

            string result = "";
                var (success, nodes) = await RedisContainerService.GetContainerAsync(containerName: name);

                if (success && nodes != null && nodes.Count > 0)
                {
                    string nodeIp = nodes.Keys.FirstOrDefault() ?? string.Empty;
                    string rawMemberInfo = nodes.Values.FirstOrDefault() ?? string.Empty;

                    if (!string.IsNullOrEmpty(nodeIp) && !string.IsNullOrEmpty(rawMemberInfo))
                    {
                        if(nodeIp == AppContext.ServerIP)
                        {
                            result = await FileWebContainer.GetHTML(name);
                        }
                        else
                        {
                            result = await RedisService.SendGetFileWebRequestAsync(nodeIp, name);
                        }
                    }
                }

            if (result.StartsWith("Error"))
                return BadRequest(new { message = $"❌ [Node {AppContext.ServerID}] Lỗi khi tải file: {result}" });

            return Ok(new { message = $"✅ [Node {AppContext.ServerID}] Tải dữ liệu file thành công.", data = result });
        }

        [HttpPost("save")]
        public async Task<IActionResult> SaveFile([FromQuery] string name, [FromBody] string content)
        {
            var username = User.Identity?.Name;
            var role = User.Claims.FirstOrDefault(x => x.Type == ClaimTypes.Role)?.Value ?? string.Empty;

            if (string.IsNullOrEmpty(username)) 
                return Unauthorized(new { message = $"❌ [Node {AppContext.ServerID}] Không xác thực được danh tính người dùng." });

            if (!HasFilePermission("save", role))
            {
                return StatusCode(403, new { message = $"❌ [Node {AppContext.ServerID}] Tài khoản thuộc nhóm [{role.ToUpper()}] không có quyền chỉnh sửa file." });
            }

            if (string.IsNullOrEmpty(name))
            {
                return BadRequest(new { message = $"❌ [Node {AppContext.ServerID}] Tên file target không được để trống." });
            }

            try
            {
                string result = "";
                var (success, nodes) = await RedisContainerService.GetContainerAsync(containerName: name);

                if (success && nodes != null && nodes.Count > 0)
                {
                    string nodeIp = nodes.Keys.FirstOrDefault() ?? string.Empty;
                    string rawMemberInfo = nodes.Values.FirstOrDefault() ?? string.Empty;

                    if (!string.IsNullOrEmpty(nodeIp) && !string.IsNullOrEmpty(rawMemberInfo))
                    {
                        if(nodeIp == AppContext.ServerIP)
                        {
                            result = await FileWebContainer.SetHTML(name, content);
                        }
                        else
                        {
                            result = await RedisService.SendUpdateFileWebRequestAsync(nodeIp, name, content);
                        }
                    }
                }

                if (result != null && result.StartsWith("Error"))
                {
                    return BadRequest(new { message = $"❌ [Node {AppContext.ServerID}] Ghi file thất bại: {result}" });
                }

                return Ok(new { message = $"✅ [Node {AppContext.ServerID}] Cập nhật dữ liệu file thành công." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"❌ [Node {AppContext.ServerID}] Lỗi hệ thống khi ghi file: {ex.Message}" });
            }
        }

        #region HamPhuTro
        private bool HasFilePermission(string action, string role)
        {
            if (string.IsNullOrWhiteSpace(role)) return false;
            
            string userRole = role.Trim().ToLower();
            if (userRole == "admin") return true;

            if (action == "get")
            {
                return userRole == "dev" || userRole == "ops" || userRole == "manager";
            }
            if (action == "save")
            {
                return userRole == "dev" || userRole == "ops";
            }

            return false;
        }
        #endregion
    }
}
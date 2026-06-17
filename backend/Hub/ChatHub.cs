using Microsoft.AspNetCore.SignalR;
using AppContext = ChatOps.Data.AppContext;
using System.Security.Claims;
using ChatOps.Services.RedisService;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ChatOps.Hubs
{
    public class ChatHub : Hub
    {
        public override async Task OnConnectedAsync()
        {
            var username = Context.User?.Identity?.Name;
            var role = Context.User?.Claims.FirstOrDefault(x => x.Type == ClaimTypes.Role)?.Value ?? string.Empty;
            
            if (!string.IsNullOrEmpty(username))
            {   
                try
                {   
                    string localIp = AppContext.ServerIP;
                    string cleanUser = username.Trim();

                    await RedisUserSessionService.InsertUserSessionAsync(cleanUser, localIp);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [SignalR Hub Error] Lỗi đăng ký Session khi kết nối ({username}): {ex.Message}");
                }

                Console.WriteLine($"🌐 [SignalR] Connected: {username} (Node: {AppContext.ServerIP})");
            }
            await base.OnConnectedAsync();
        }

        public async Task RefreshSession()
        {
            var username = Context.User?.Identity?.Name;
            if (!string.IsNullOrEmpty(username))
            {
                try
                {
                    string cleanUser = username.Trim();

                    await RedisUserSessionService.UpdateUserSessionValueAsync(cleanUser);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"🔄 [SignalR Hub Error] Lỗi gia hạn làm mới Session ({username}): {ex.Message}");
                }
            }
        }
        
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var username = Context.User?.Identity?.Name;
            if (!string.IsNullOrEmpty(username))
            {
                try
                {
                    string cleanUser = username.Trim();

                    await RedisUserSessionService.UpdateUserSessionValueAsync(cleanUser);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"🗑️ [SignalR Hub Error] Lỗi hủy bỏ Session khi ngắt kết nối ({username}): {ex.Message}");
                }
                
                Console.WriteLine($"🔌 [SignalR] Disconnected: {username} (Node: {AppContext.ServerIP})");
            }
            await base.OnDisconnectedAsync(exception);
        }
    }
}
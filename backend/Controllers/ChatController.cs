using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;
using ChatOps.Models;
using ChatOps.Data;
using AppContext = ChatOps.Data.AppContext;
using ChatOps.Services.ChatService;
using Microsoft.EntityFrameworkCore;
using ChatOps.Hubs;
using ChatOps.Services.RedisService;
using ChatOps.Services.CommandParserService;
using ChatOps.Services;

namespace ChatOps.Controllers
{
    [ApiController]
    [Route("api/chat")]
    [Authorize]
    public class ChatController : ControllerBase
    {
        #region ThamSo
        private readonly AppDbContext _db;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        #endregion

        #region Constructor
        public ChatController(AppDbContext db, IServiceScopeFactory serviceScopeFactory)
        {
            _db = db;
            _serviceScopeFactory = serviceScopeFactory;
        }
        #endregion

        [HttpGet("history")]
        public async Task<IActionResult> GetChatHistory()
        {
            var username = User.Identity?.Name;
            if (string.IsNullOrEmpty(username)) return Unauthorized();

            var cleanedUsername = username.Trim();

            var user = await _db.Users.FirstOrDefaultAsync(x => x.Username == cleanedUsername);
            if (user == null) return Unauthorized();

            var history = await RedisHistoryService.GetHistoryAsync(cleanedUsername);
            var historylist = new List<string>();

            if (history.nodes != null && history.nodes.TryGetValue(cleanedUsername, out var rawHistory))
            {
                if (!string.IsNullOrEmpty(rawHistory))
                {
                    historylist = rawHistory.Split(new[] { "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
                }
            }

            return Ok(historylist);
        }

        [HttpPost]
        public async Task<IActionResult> Chat([FromBody] ChatRequest req)
        {
            if (string.IsNullOrEmpty(req.Command) || string.IsNullOrEmpty(req.ConnectionId))
            {
                return BadRequest(new { message = $"❌ [Node {AppContext.ServerID}] Dữ liệu lệnh hoặc ConnectionId không hợp lệ." });
            }

            var username = User.Identity?.Name;
            var role = User.Claims.FirstOrDefault(x => x.Type == ClaimTypes.Role)?.Value ?? string.Empty;

            if (string.IsNullOrEmpty(username)) return Unauthorized(new { message = $"❌ [Node {AppContext.ServerID}] Không xác thực được danh tính người dùng." });

            UserSession session = new UserSession()
            {
                Username = username.Trim(),
                Role = role.Trim(),
                Debug = req.Debug
            };

            string cleanCommand = req.Command.Trim();
            string cleanConnectionId = req.ConnectionId.Trim();

            _ = Task.Run(() => HandleDockerLogicAsync(cleanCommand, cleanConnectionId, session));

            return Ok(new { message = $"✅ [Node {AppContext.ServerID}] Hệ thống đã tiếp nhận yêu cầu xử lý lệnh." });
        }

        #region HamPhuTro
        private async Task HandleDockerLogicAsync(string rawCommand, string connectionId, UserSession session)
        {
            try
            {
                await _serviceScopeFactory.WithDb<AppDbContext>(async db =>
                {
                    await Task.Delay(1000);
                    var user = await db.Users.FirstOrDefaultAsync(x => x.Username == session.Username);
                    if (user == null)
                    {
                        await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"❌ [Node {AppContext.ServerID}] Tài khoản không tồn tại hoặc đã bị xóa khỏi hệ thống.");
                        return;
                    }

                    string cmd = rawCommand?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(cmd))
                    {
                        await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"❌ [Node {AppContext.ServerID}] Cú pháp lệnh trống, vui lòng nhập lại.");
                        return;
                    }

                    if (!cmd.StartsWith("history", StringComparison.OrdinalIgnoreCase))
                    {
                        await RedisHistoryService.InsertHistoryAsync(session.Username);
                        await RedisHistoryService.UpdateHistoryValueAsync(session.Username, cmd);
                    }

                    await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang phân tích cú pháp lệnh: {cmd}");
                    var parsed = CommandParserService.Parse(cmd);
                    if (parsed.ContainsKey("error"))
                    {
                        await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"❌ [Node {AppContext.ServerID}] Lỗi cấu trúc lệnh: {parsed["error"]}");
                        return;
                    }

                    string action = parsed.GetValueOrDefault("base_command", "").Trim().ToLower();
                    
                    await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Kiểm tra quyền hạn phân phối lệnh: {action}");
                    if (!await HasPermission(action, session.Role))
                    {
                        await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"❌ [Node {AppContext.ServerID}] Tài khoản thuộc nhóm [{session.Role.ToUpper()}] không có quyền thực thi lệnh này.");
                        return;
                    }

                    string n = parsed.GetValueOrDefault("n", "").Trim().ToLower();
                    int neededCount = 0;
                    if (action == "deploy")
                    {
                        string serviceType = parsed.GetValueOrDefault("serviceType", "").Trim().ToLower();  
                        if(serviceType == "db")
                            neededCount = 0;
                        else
                            neededCount = 1;
                    }
                    else
                    {
                        neededCount = action switch
                        {
                            "deploy-git" or "deploy-compose" => 1,
                            _ => string.IsNullOrEmpty(n) ? 1 : int.Parse(n)
                        };
                    }
                    
                    await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🌐 [Node {AppContext.ServerID}] Đang kiểm tra định tuyến trên Cluster...");

                    RoutingResult routingResult = await ClusterRoutingService.HandleRoutingAsync(action, parsed, connectionId, neededCount, session);
                    string result = "";
                    if (routingResult != null)
                    {
                        if (routingResult.IsError)
                        {
                            await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"❌ [Node {AppContext.ServerID}] Lỗi định tuyến Cluster: {routingResult.ErrorMessage}");
                            return;
                        }

                        if (routingResult.IsForwarding)
                        {
                            if (routingResult.TargetNodeIp == "ALL_NODES")
                            {
                                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Lệnh đã được hệ thống định tuyến tự động sang tất cả Node trong Cluster.");
                                var nodeResult = await RedisNodeService.GetNodeAsync(isGetAll: true);
                                
                                if (nodeResult.nodes != null && nodeResult.nodes.Any())
                                {
                                    var completedResults = new List<(string NodeIdOrIp, string Result)>();

                                    foreach (var node in nodeResult.nodes)
                                    {
                                        string targetIp = node.Key;
                                        string nodeName = node.Value;

                                        if (nodeName == AppContext.ServerID || targetIp == AppContext.ServerIP)
                                        {
                                            try
                                            {
                                                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Bắt đầu thực thi cục bộ tại Node hiện tại...");
                                                string localResult = await ExecuteCommandRouteAsync(action, parsed, session, connectionId);
                                                completedResults.Add((AppContext.ServerID, localResult));
                                            }
                                            catch (Exception ex)
                                            {
                                                completedResults.Add((AppContext.ServerID, $"❌ Lỗi thực thi cục bộ: {ex.Message}"));
                                            }
                                        }
                                        else
                                        {
                                            try
                                            {
                                                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Bắt đầu gửi lệnh thực thi từ xa đến mục tiêu: {nodeName}...");
                                                string remoteResult = await RedisService.ExecuteDockerRemoteAsync(action, parsed, session, connectionId, targetIp);
                                                completedResults.Add((nodeName, remoteResult));
                                            }
                                            catch (Exception ex)
                                            {
                                                completedResults.Add((nodeName, $"❌ Lỗi thực thi từ xa: {ex.Message}"));
                                            }
                                        }

                                        await Task.Delay(300); 
                                    }

                                    var aggregateBuilder = new System.Text.StringBuilder();
                                    aggregateBuilder.AppendLine($"📊 [Node {AppContext.ServerID}] TỔNG HỢP KẾT QUẢ THỰC THI TOÀN CỤM:");
                                    aggregateBuilder.AppendLine("==================================================");

                                    foreach (var res in completedResults)
                                    {
                                        aggregateBuilder.AppendLine($"📍 [Node {res.NodeIdOrIp}]:");
                                        aggregateBuilder.AppendLine(res.Result);
                                        aggregateBuilder.AppendLine("--------------------------------------------------");
                                    }
                                    result = aggregateBuilder.ToString();
                                }
                                else
                                {
                                    await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"❌ [Node {AppContext.ServerID}] Hệ thống không tìm thấy bất kỳ Node nào khả dụng trong Registry.");
                                    return;
                                }
                            }
                            else if (routingResult.TargetNodeIp != AppContext.ServerIP)
                            {
                                var noderesult = await RedisNodeService.GetNodeAsync(routingResult.TargetNodeIp);
                                string nodeid = noderesult.nodes[$"{routingResult.TargetNodeIp}"];
                                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Lệnh đã được hệ thống định tuyến tự động sang Node từ xa: {nodeid}");
                                result = await RedisService.ExecuteDockerRemoteAsync(action, parsed, session, connectionId, routingResult.TargetNodeIp);
                            }
                            else
                            {
                                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🚀 [Node {AppContext.ServerID}] Đang tiến hành thực thi cục bộ: {action}...");
                                result = await ExecuteCommandRouteAsync(action, parsed, session, connectionId);
                            }
                        }
                        else
                        {
                            await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🚀 [Node {AppContext.ServerID}] Đang tiến hành thực thi cục bộ: {action}...");
                            result = await ExecuteCommandRouteAsync(action, parsed, session, connectionId);
                        }
                    }
                    else
                    {
                        await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🚀 [Node {AppContext.ServerID}] Đang tiến hành thực thi cục bộ: {action}...");
                        result = await ExecuteCommandRouteAsync(action, parsed, session, connectionId);
                    }
                    
                    if (!result.StartsWith("❌") && !result.StartsWith("✅"))
                    {
                        result = $"✅ [Node {AppContext.ServerID}] Kết quả thực thi lệnh:\n{result}";
                    }
                    await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"{result}");
                });
            }
            catch (Exception ex)
            {
                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"❌ [Node {AppContext.ServerID}] Hệ thống gặp lỗi cục bộ tại tầng xử lý: {ex.Message}");
            }
        }

        internal async Task<string> ExecuteCommandRouteAsync(string action, Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            return action switch
            {
                "register" => await ChatAuthService.Register(parsed, session, db, connectionId),
                "whoami" => await ChatAuthService.GetCurrentUser(session, connectionId),
                "setupasswd" => await ChatAuthService.ChangePasswd(parsed, session, db, connectionId),
                "setuname" => await ChatAuthService.ChangeUsername(parsed, session, db, connectionId),
                "setuemail" => await ChatAuthService.ChangeEmail(parsed, session, db, connectionId),
                "seturole" => await ChatAuthService.SetRole(parsed, session, db, connectionId),
                "deluser" => await ChatAuthService.DelUser(parsed, session, db, connectionId),
                "users" => await ChatAuthService.GetUsers(session, db, connectionId),

                "deploy" => await ChatDeployServiceDeploy.Deploy(parsed, session, db, connectionId),
                "attach-db" => await ChatDeployServiceAtDetachDB.AttachDB(parsed, session, connectionId),
                "detach-db" => await ChatDeployServiceAtDetachDB.DetachDB(parsed, session, connectionId),
                "deploy-git" => await ChatDeployServiceDeploy.DeployGit(parsed, session, db, connectionId),
                "release" => await ChatDeployServiceDeployRelease.Release(parsed, session, connectionId),
                "list-release" => await ChatDeployServiceDeployRelease.ListRelease(parsed, session, connectionId),
                "unrelease" => await ChatDeployServiceDeployRelease.Unrelease(parsed, session, connectionId),
                "deploy-compose" => await ChatDeployServiceDeploy.DeployCompose(parsed, session, db, connectionId),

                "ps" => await ChatContainerService.ListContainer(parsed, session, connectionId),
                "start" => await ChatContainerService.Start(parsed, session, connectionId),
                "stop" => await ChatContainerService.Stop(parsed, session, connectionId),
                "kill" => await ChatContainerService.Kill(parsed, session, connectionId),
                "restart" => await ChatContainerService.Restart(parsed, session, connectionId),
                "rm" => await ChatContainerService.Remove(parsed, session, connectionId),
                "rmi" => await ChatContainerService.RemoveImage(parsed, session, connectionId),
                "setcname" => await ChatContainerService.Rename(parsed, session, connectionId),
                "setcusername" => await ChatContainerService.Setcusername(parsed, session, connectionId),
                "setcdomain" => await ChatContainerService.Setcdomain(parsed, session, connectionId),
                "inspect" => await ChatContainerService.Inspect(parsed, session, connectionId),

                "openweb" => await ChatAccessService.OpenWeb(parsed, session, connectionId),
                "opentool" => await ChatAccessService.OpenTool(parsed, session, connectionId),
                "editweb" => await ChatAccessService.EditWeb(parsed, session, connectionId),

                "logs" => await ChatDebugService.GetLogs(parsed, session, connectionId),

                "backup" => await ChatBackUpService.CreateBackup(parsed, session, connectionId),
                "list-backup" => await ChatBackUpService.ListBackup(parsed, session, connectionId),
                "rollback" => await ChatBackUpService.ExecuteRollback(parsed, session, connectionId),
                "delete-backup" => await ChatBackUpService.DeleteBackup(parsed, session, connectionId),

                "stats" => await ChatMonitorService.GetStats(parsed, session, connectionId),
                "df" => await ChatMonitorService.GetDiskUsage(session, connectionId),
                "health" => await ChatMonitorService.CheckHealth(parsed, session, connectionId),
                "instances" => await ChatMonitorService.ListInstanceApps(session, connectionId),
                "appservices" => await ChatMonitorService.ListAppServices(session, connectionId),
                "imageservices" => await ChatMonitorService.ListImageServices(session, connectionId),

                "scale" => await ChatDevOpsService.ScaleService(parsed, session, connectionId),
                "set-alert" => await ChatDevOpsService.SetAlert(parsed, session, connectionId),
                "rm-alert" => await ChatDevOpsService.RemoveAlert(parsed, session, connectionId),

                "prune" => await ChatCleanUpService.SystemPrune(parsed, session, connectionId),

                "images" => await ChatSupportService.ListLocalImages(session, connectionId),
                "ports" => await ChatSupportService.ListOccupiedPorts(session, connectionId),
                "nodes" => await ChatSupportService.ListNodes(session, connectionId),
                "history" => await ChatSupportService.GetCommandHistory(session, connectionId),
                "help" => await ChatSupportService.GetHelpQuick(parsed, session, connectionId),
                "clear" => await ChatSupportService.ClearHistory(session, connectionId),

                _ => $"❌ [Node {AppContext.ServerID}] Lệnh không tồn tại trên hệ thống Cluster. Gõ [help] để xem danh sách lệnh được hỗ trợ."
            };
        }       

        public static async Task<bool> HasPermission(string action, string role)
        {
            if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(role))
                return false;

            string command = action.Trim().ToLower();
            string userRole = role.Trim().ToLower();

            // 🔴 1. ADMIN - Toàn quyền hệ thống
            if (userRole == "admin") return true;

            // 🟢 2. USER - Nhóm lệnh cơ bản nhất (View Only)
            var userCommands = new HashSet<string>
            {
                "logout", "whoami", "setupasswd", "ps", "inspect", 
                "openweb", "opentool", "logs", "stats", "health", 
                "history", "clear", "help"
            };

            if (userRole == "user") 
                return userCommands.Contains(command);

            // 🟠 3. MANAGER - Quản lý user và giám sát trạng thái hệ thống
            if (userRole == "manager")
            {
                var managerCommands = new HashSet<string>
                {
                    "register", "setuname", "setuemail", "seturole", "deluser", "users",
                    "list-release", "instances", "appservices", "imageservices", 
                    "df", "images", "ports", "nodes"
                };
                return userCommands.Contains(command) || managerCommands.Contains(command);
            }

            // 🛠️ Tập lệnh dùng chung cho hạ tầng kỹ thuật (Dev & Ops Shared)
            var devOpsShared = new HashSet<string>
            {
                "users", // Bổ sung theo phân hệ mới của Dev và Ops
                "deploy", "attach-db", "detach-db", "list-release", 
                "start", "stop", "kill", "restart", "rm", "rmi", 
                "setcname", "setcusername", "setcdomain", "editweb", 
                "backup", "list-backup", "rollback", "delete-backup", 
                "instances", "appservices", "imageservices", "df", "health", // Đồng bộ nhóm Monitor
                "scale", "set-alert", "rm-alert", "prune", 
                "images", "ports", "nodes"
            };

            // 🟡 4. DEV - Môi trường phát triển & kiểm thử
            if (userRole == "dev")
            {
                var devOnly = new HashSet<string> { "deploy-git", "release", "unrelease" };
                return userCommands.Contains(command) || devOpsShared.Contains(command) || devOnly.Contains(command);
            }

            // 🔵 5. OPS - Môi trường vận hành Production
            if (userRole == "ops")
            {
                var opsOnly = new HashSet<string> { "deploy-compose" };
                return userCommands.Contains(command) || devOpsShared.Contains(command) || opsOnly.Contains(command);
            }

            return false;
        }
        #endregion
    }
}

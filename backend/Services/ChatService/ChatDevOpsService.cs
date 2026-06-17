using System.Text.Json;
using ChatOps.Models;
using ChatOps.Services.RedisService;
using ChatOps.Services.SystemService;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.ChatService
{
    public static class ChatDevOpsService
    {
        private static async Task SendLogWithDelayAsync(bool debug, string connectionId, string message)
        {
            await Task.Delay(100);
            await RedisChannelService.SendMessageToClientAsync(debug, connectionId, message);
        }

        public static async Task<string> ScaleService(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang trích xuất tham số hệ thống hạ tầng...");

            string instance = parsed.GetValueOrDefault("instance", "").Trim().ToLower();
            string typeStr = parsed.GetValueOrDefault("type", "all").Trim().ToLower();
            string countStr = parsed.GetValueOrDefault("n", "").Trim();

            if (string.IsNullOrEmpty(instance))
            {
                return "❌ Thiếu tham số tên ứng dụng (instance).";
            }

            if (!int.TryParse(countStr, out int target))
            {
                return "❌ Số lượng container cần scale (n) không hợp lệ.";
            }

            if (target < 1 || target > 10)
            {
                return "❌ Số lượng scale nằm ngoài phạm vi cho phép (Yêu cầu cấu hình tối thiểu là 1 và tối đa là 10 container).";
            }

            bool isAuthorized = session.Role == "admin" || session.Role == "manager" || session.Role == "ops" || session.Role == "dev";
            if (!isAuthorized)
            {
                return $"❌ Từ chối truy cập: Tài khoản {session.Username} không đủ thẩm quyền thực thi lệnh scale hạ tầng.";
            }

            var instances = await RedisInstanceService.GetInstanceAsync(instance);
            if (!instances.success || instances.nodes == null || instances.nodes.Count == 0)
            {
                return $"❌ Không tìm thấy thực thể triển khai (instance) `{instance}` trên hệ thống quản lý.";
            }

            string app = instance.EndsWith("_trial")
                ? instance.Substring(0, instance.LastIndexOf("_trial"))
                : instance;

            var appservice = await RedisAppService.GetAppAsync(app);
            if (!appservice.success || appservice.services == null || appservice.services.Count == 0)
            {
                await SendLogWithDelayAsync(session.Debug, connectionId, $"❌ Không tìm thấy thông tin cấu hình hoặc ứng dụng gốc `{app}` không tồn tại trên hệ thống quản lý Redis.");
                return $"❌ Không tìm thấy thông tin cấu hình hoặc ứng dụng gốc `{app}` không tồn tại trên hệ thống quản lý Redis.";
            }

            var existingServiceTypes = new HashSet<string>();
            foreach (var service in appservice.services)
            {
                try
                {
                    var redisData = JsonSerializer.Deserialize<JsonElement>(service.Value);
                    string servicetype = redisData.TryGetProperty("ServiceType", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                    if (!string.IsNullOrEmpty(servicetype))
                    {
                        var types = servicetype.Split(',', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var type in types)
                        {
                            existingServiceTypes.Add(type.ToLower().Trim());
                        }
                    }
                }
                catch { }
            }

            var scalableCoreServices = existingServiceTypes.Where(t => t != "db").ToList();

            if (typeStr != "all")
            {
                if (typeStr == "db")
                {
                    return "❌ Từ chối thực thi: Hệ thống không cho phép scale các dịch vụ thuộc nhóm cơ sở dữ liệu (`db`) để đảm bảo toàn vẹn dữ liệu.";
                }

                if (typeStr != "lb" && !scalableCoreServices.Contains(typeStr))
                {
                    return $"❌ Không thể scale: Loại dịch vụ `{typeStr.ToUpper()}` không tồn tại hoặc không hợp lệ trong ứng dụng `{app}`.";
                }
            }
            else
            {
                if (scalableCoreServices.Count == 0)
                {
                    return $"❌ Không tìm thấy bất kỳ dịch vụ Core App nào hợp lệ (ngoại trừ `db`) thuộc dự án `{app}` để thực hiện scale.";
                }
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang xác định hồ sơ tệp cấu hình của cụm dịch vụ {app}...");

            string runtimeDir = $"/home/ubuntu/ChatOps/docker/Apps/{instance}";
            string coreComposeFile = "docker-registry.yml";

            if (!File.Exists(Path.Combine(runtimeDir, coreComposeFile)))
            {
                coreComposeFile = "docker-git.yml";
                if (!File.Exists(Path.Combine(runtimeDir, coreComposeFile)))
                {
                    return $"❌ Không tìm thấy tệp cấu hình Docker Compose Core App (`docker-registry.yml` hoặc `docker-git.yml`) tại không gian `{runtimeDir}`.";
                }
            }

            string lbComposeFile = "docker-compose-lb.yml";
            if (!File.Exists(Path.Combine(runtimeDir, lbComposeFile)))
            {
                return $"❌ Không tìm thấy tệp cấu hình Load Balancer `{lbComposeFile}` tại không gian ứng dụng `{runtimeDir}`.";
            }

            var result = await ExecuteScaleLogicAsync(
                instance, typeStr, target, scalableCoreServices,
                runtimeDir, coreComposeFile, lbComposeFile,
                session, connectionId
            );

            return result.Log;
        }

        public static async Task<string> SetAlert(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang xử lý thiết lập hệ thống giám sát...");

            string app = parsed.GetValueOrDefault("instance", "").Trim().ToLower();
            if (string.IsNullOrEmpty(app))
            {
                return "❌ Thiếu tham số tên ứng dụng (instance).";
            }

            bool isAuthorized = session.Role == "admin" || session.Role == "manager" || session.Role == "ops" || session.Role == "dev";
            if (!isAuthorized)
            {
                return $"❌ Từ chối truy cập: Tài khoản {session.Username} không đủ thẩm quyền thực thi lệnh cấu hình alert.";
            }

            if (AppContext.AlertTargets.Contains(app))
            {
                return $"⚠️ Ứng dụng `{app}` hiện đã nằm trong danh sách theo dõi alert từ trước.";
            }

            AppContext.AlertTargets.Add(app);

            return $"🚨 Alert enabled: Kích hoạt giám sát và cảnh báo thành công cho ứng dụng `{app.ToUpper()}`.";
        }

        public static async Task<string> RemoveAlert(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang xử lý gỡ bỏ hệ thống giám sát...");

            string app = parsed.GetValueOrDefault("instance", "").Trim().ToLower();
            if (string.IsNullOrEmpty(app))
            {
                return "❌ Thiếu tham số tên ứng dụng (instance).";
            }

            bool isAuthorized = session.Role == "admin" || session.Role == "manager" || session.Role == "ops" || session.Role == "dev";
            if (!isAuthorized)
            {
                return $"❌ Từ chối truy cập: Tài khoản {session.Username} không đủ thẩm quyền thực thi lệnh cấu hình alert.";
            }

            if (AppContext.AlertTargets.Remove(app))
            {
                return $"✅ Removed alert: Đã gỡ bỏ thành công ứng dụng `{app.ToUpper()}` ra khỏi danh sách nhận cảnh báo.";
            }
            else
            {
                return $"❌ Thất bại: Ứng dụng `{app}` hiện không nằm trong danh sách theo dõi alert của hệ thống.";
            }
        }

        public static async Task<(bool Success, string Log)> ExecuteScaleLogicAsync(string app, string typeStr, int target, List<string> scalableCoreServices, string runtimeDir, string coreComposeFile, string lbComposeFile, UserSession session, string? connectionId = null)
        {
            string log = $"⚙️ **[SCALE HẠ TẦNG] APP: {app.ToUpper()}**\n" +
                         $"🎯 Target quy mô: {target} container(s)\n" +
                         $"📦 Phân loại yêu cầu: {typeStr.ToUpper()}\n" +
                         $"📄 Core Profile: `{coreComposeFile}`\n" +
                         $"⚖️ LB Profile: `{lbComposeFile}`\n\n";

            string cdCmd = $"cd {runtimeDir}";
            bool needScaleLb = (typeStr == "lb" || typeStr == "all");
            int[] ports = Array.Empty<int>();

            if (needScaleLb)
            {
                if (!string.IsNullOrEmpty(connectionId))
                {
                    await SendLogWithDelayAsync(session.Debug, connectionId, "⚡ [Docker Engine] Đang cấu hình lại quy mô Load Balancer. Tiến hành dò tìm cổng tự do...");
                }

                string? freePortsStr = await ChatContainerService.GetFreePorts(target, session, connectionId ?? "");
                if (string.IsNullOrEmpty(freePortsStr))
                {
                    return (false, "❌ Lỗi hạ tầng: Không tìm thấy dải gồm cổng kết nối trống liên tiếp trên Node hệ thống.");
                }

                var assignedPorts = freePortsStr.Split(',').Select(int.Parse).OrderBy(p => p).ToList();
                int startPort = assignedPorts.First();
                int endPort = assignedPorts.Last();
                ports = assignedPorts.ToArray();

                if (!string.IsNullOrEmpty(connectionId))
                {
                    await SendLogWithDelayAsync(session.Debug, connectionId, $"📝 Đồng bộ dải cổng mới [{startPort} - {endPort}] vào tệp môi trường runtime...");
                }

                try
                {
                    string envPath = Path.Combine(runtimeDir, ".env");
                    if (File.Exists(envPath))
                    {
                        var envLines = await File.ReadAllLinesAsync(envPath);
                        var updatedLines = envLines
                            .Where(line => !line.StartsWith("START_PORT=") && !line.StartsWith("END_PORT="))
                            .ToList();

                        updatedLines.Add($"START_PORT={startPort}");
                        updatedLines.Add($"END_PORT={endPort}");

                        await File.WriteAllLinesAsync(envPath, updatedLines);
                    }
                }
                catch (Exception ex)
                {
                    return (false, $"❌ Thất bại khi ghi nhận cấu hình môi trường (.env): {ex.Message}");
                }
            }

            string coreScaleArgs = "";
            if (typeStr != "all" && typeStr != "lb")
            {
                coreScaleArgs = $"--scale {typeStr}={target}";
                log += $"🌐 Đang scale nhóm dịch vụ Core: **{typeStr.ToUpper()}** lên số lượng: {target}\n";
            }
            else if (typeStr == "all")
            {
                var activeCoreScales = new List<string>();
                foreach (var svc in scalableCoreServices)
                {
                    activeCoreScales.Add($"--scale {svc}={target}");
                    log += $"📦 Đang chuẩn bị scale dịch vụ Core: **{svc.ToUpper()}** → {target}\n";
                }
                coreScaleArgs = string.Join(" ", activeCoreScales);
            }

            if (!string.IsNullOrEmpty(coreScaleArgs))
            {
                if (!string.IsNullOrEmpty(connectionId))
                {
                    await SendLogWithDelayAsync(session.Debug, connectionId, $"🚀 Triển khai thiết lập quy mô hạ tầng Core Services qua `{coreComposeFile}`...");
                }
                string coreCmd = $"{cdCmd} && docker compose -f {coreComposeFile} up -d {coreScaleArgs} 2>&1";
                string coreResult = await SystemCommandService.RunAsync(coreCmd);

                log += "\n=== CORE APPLICATION EXECUTION RESULT ===\n";
                log += $"{coreResult.Trim()}\n";
            }

            if (needScaleLb)
            {
                if (!string.IsNullOrEmpty(connectionId))
                {
                    await SendLogWithDelayAsync(session.Debug, connectionId, "⚖️ Khởi chạy tái thiết lập quy mô cụm Load Balancer biên dịch riêng...");
                }

                string lbCmd = $"{cdCmd} && docker compose -f {lbComposeFile} up -d --force-recreate --scale lb={target} 2>&1";
                string lbResult = await SystemCommandService.RunAsync(lbCmd);

                log += "\n=== LOAD BALANCER EXECUTION RESULT ===\n";
                log += $"{lbResult.Trim()}\n";

                await RedisPortService.ReleasePortsAsync(AppContext.ServerIP, ports);
            }

            return (true, log);
        }
    }
}
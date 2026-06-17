using System.Text.Json;
using ChatOps.Models;
using ChatOps.Services.RedisService;
using ChatOps.Services.SystemService;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.ChatService
{
    public static class ChatMonitorService
    {
        private static async Task SendLogWithDelayAsync(bool debug, string connectionId, string message)
        {
            await Task.Delay(100);
            await RedisChannelService.SendMessageToClientAsync(debug, connectionId, message);
        }

        public static async Task<string> GetStats(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang trích xuất tham số lệnh stats...");

            bool isAll = parsed.TryGetValue("all", out var allVal) && allVal == "true";
            parsed.TryGetValue("instance", out string? instanceFilter);
            parsed.TryGetValue("container", out string? containerFilter);

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔑 [Node {AppContext.ServerID}] Đang kiểm tra phân quyền user {session.Username}...");

            bool isFullAccess = session.Role == "admin" || session.Role == "manager" || session.Role == "dev" || session.Role == "ops";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] Đang kết nối hạ tầng Docker thu thập thông số tài nguyên...");

            string filterTarget = "";

            if (!string.IsNullOrWhiteSpace(containerFilter))
            {
                filterTarget = containerFilter;
            }
            else if (!string.IsNullOrWhiteSpace(instanceFilter))
            {
                List<DockerContainer> containers = await DockerService.Read.DockerReadContainer.GetContainersAsync();
                var matchedNames = new List<string>();

                foreach (var container in containers)
                {
                    if (container == null) continue;

                    var metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(container.Name);
                    if (metadata != null && string.Equals(metadata.AppProject, instanceFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!isFullAccess && (metadata.Owners == null || !metadata.Owners.Contains(session.Username)))
                        {
                            continue;
                        }

                        matchedNames.Add(container.Name);
                    }
                }

                if (matchedNames.Count == 0)
                {
                    return $"⚠️ Không tìm thấy container nào đang vận hành thuộc instance app '{instanceFilter}'.";
                }

                filterTarget = string.Join(" ", matchedNames);
            }
            else if (isAll)
            {
                if (!isFullAccess)
                {
                    List<DockerContainer> containers = await DockerService.Read.DockerReadContainer.GetContainersAsync();
                    var matchedNames = new List<string>();

                    foreach (var container in containers)
                    {
                        if (container == null) continue;
                        var metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(container.Name);
                        if (metadata != null && metadata.Owners != null && metadata.Owners.Contains(session.Username))
                        {
                            matchedNames.Add(container.Name);
                        }
                    }

                    if (matchedNames.Count == 0) return "⚠️ Bạn hiện không sở hữu container nào đang chạy để xem stats.";
                    filterTarget = string.Join(" ", matchedNames);
                }
                else
                {
                    List<DockerContainer> containers = await DockerService.Read.DockerReadContainer.GetContainersAsync();
                    var matchedNames = new List<string>();

                    foreach (var container in containers)
                    {
                        if (container == null) continue;

                        var metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(container.Name);
                        if (!isFullAccess)
                        {
                            if (metadata != null && metadata.Owners != null && metadata.Owners.Contains(session.Username))
                            {
                                matchedNames.Add(container.Name);
                            }
                        }
                        else
                        {
                            matchedNames.Add(container.Name);
                        }
                    }

                    if (matchedNames.Count == 0) return "ℹ️ Không có container nào đang chạy để thu thập dữ liệu.";
                    filterTarget = string.Join(" ", matchedNames);
                }
            }
            else
            {
                List<DockerContainer> containers = await DockerService.Read.DockerReadContainer.GetContainersAsync();
                var matchedNames = new List<string>();

                foreach (var container in containers)
                {
                    if (container == null || !container.IsRunning) continue;

                    var metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(container.Name);
                    if (!isFullAccess)
                    {
                        if (metadata != null && metadata.Owners != null && metadata.Owners.Contains(session.Username))
                        {
                            matchedNames.Add(container.Name);
                        }
                    }
                    else
                    {
                        matchedNames.Add(container.Name);
                    }
                }

                if (matchedNames.Count == 0) return "ℹ️ Không có container nào đang chạy để thu thập dữ liệu.";
                filterTarget = string.Join(" ", matchedNames);
            }

            string cmdResult = await SystemCommandService.RunAsync($"docker stats {filterTarget} --no-stream --format \"table {{{{.Container}}}} | {{{{.CPUPerc}}}} | {{{{.MemUsage}}}} | {{{{.MemPerc}}}}\"");

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Hoàn tất xử lý tài nguyên. Đang đóng gói kết quả...");

            if (string.IsNullOrWhiteSpace(cmdResult))
                return "❌ Không có dữ liệu stats trả về. Vui lòng kiểm tra lại trạng thái container hoặc bộ lọc.";

            return $"📊 THÔNG SỐ TÀI NGUYÊN HỆ THỐNG [Local Node: {AppContext.ServerID}]\n" +
                   $"--------------------------------------------------\n" +
                   $"{cmdResult}";
        }

        public static async Task<string> GetDiskUsage(UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] Đang kiểm tra dung lượng lưu trữ phân vùng Docker local...");

            string dfResult = await SystemCommandService.RunAsync("docker system df");

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Đồng bộ dữ liệu bộ nhớ hoàn tất.");

            return $"💾 DUNG LƯỢNG LƯU TRỮ DOCKER [Local Node: {AppContext.ServerID}]\n" +
                   $"--------------------------------------------------\n" +
                   $"{dfResult}";
        }

        public static async Task<string> CheckHealth(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang bóc tách tham số health check...");

            parsed.TryGetValue("instance", out string? instanceApp);
            if (string.IsNullOrWhiteSpace(instanceApp))
            {
                return "❌ Thiếu tham số 'instance' bắt buộc.";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang quét trạng thái sức khỏe các cụm dịch vụ của '{instanceApp}'...");

            List<DockerContainer> containers = await DockerService.Read.DockerReadContainer.GetContainersAsync();
            var matchedContainers = new List<DockerContainer>();

            foreach (var container in containers)
            {
                if (container == null) continue;
                var metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(container.Name);
                if (metadata != null && string.Equals(metadata.AppProject, instanceApp, StringComparison.OrdinalIgnoreCase))
                {
                    matchedContainers.Add(container);
                }
            }

            if (matchedContainers.Count == 0)
            {
                return $"⚠️ Không tìm thấy bất kỳ container/dịch vụ nào đang chạy thuộc instance '{instanceApp}'.";
            }

            string report = $"🏥 HEALTH CHECK REPORT FOR INSTANCE: {instanceApp.ToUpper()}\n" +
                            $"--------------------------------------------------\n";

            foreach (var con in matchedContainers)
            {
                string healthStatus = con.IsRunning ? "🟢 HEALTHY" : "🔴 UNHEALTHY/STOPPED";
                report += $"  - Container: {con.Name}\n" +
                          $"    + Status: {con.Status}\n" +
                          $"    + Telemetry: {healthStatus}\n" +
                          $"    + Ports Bound: {con.Ports}\n\n";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Đã xuất báo cáo trạng thái vận hành.");
            return report;
        }

        public static async Task<string> ListInstanceApps(UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] Đang truy vấn cấu trúc cụm Cluster từ Redis...");

            (bool success, Dictionary<string, string> nodes) instances = await RedisInstanceService.GetInstanceAsync(isGetAll: true);

            if (!instances.success || instances.nodes == null || instances.nodes.Count == 0)
            {
                return "ℹ️ Không tìm thấy dữ liệu active instances nào trên cụm hạ tầng Redis.";
            }

            string result = "🌐 DANH SÁCH INSTANCE NODES CLUSTER\n" +
                            "NODE/IP          | MEMBERS/APPS DEPLOYED\n" +
                            "--------------------------------------------------\n";

            foreach (var node in instances.nodes)
            {
                string nodeIp = node.Key.Replace("instance:", "");
                string members = node.Value;

                result += $"{nodeIp,-16} | {members}\n";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Hoàn tất xuất cấu trúc Node mạng.");
            return result;
        }

        public static async Task<string> ListAppServices(UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔑 [Node {AppContext.ServerID}] Xác thực phân quyền truy cập danh mục Repository App...");

            (bool success, Dictionary<string, string> services) apps = await RedisAppService.GetAppAsync(isGetAll: true);

            if (!apps.success || apps.services == null || apps.services.Count == 0)
            {
                return "ℹ️ Danh mục phân phối ứng dụng (AppServices) hiện tại trống.";
            }

            string result = "🚀 REGISTERED APP SERVICES CATALOG\n" +
                            "APP NAME   | RELEASED | SERVICES TYPES       | REPOSITORY URL\n" +
                            "--------------------------------------------------------------------------------------\n";

            foreach (var app in apps.services)
            {
                string appName = app.Key;
                string jsonRaw = app.Value;

                try
                {
                    using JsonDocument doc = JsonDocument.Parse(jsonRaw);
                    JsonElement root = doc.RootElement;

                    string url = root.GetProperty("Url").GetString() ?? "N/A";
                    string serviceType = root.GetProperty("ServiceType").GetString() ?? "N/A";
                    bool isReleased = root.GetProperty("IsReleased").GetBoolean();

                    result += $"{appName,-10} | {(isReleased ? "🟢 YES" : "🔴 NO"),-8} | {serviceType,-20} | {url}\n";
                }
                catch
                {
                    result += $"{appName,-10} | {"ERR",-8} | {"Data Malformed",-20} | {jsonRaw}\n";
                }
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Đã hoàn thành tải danh mục dịch vụ ứng dụng.");
            return result;
        }

        public static async Task<string> ListImageServices(UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang nạp danh mục Docker Blueprint Images từ lõi quản trị...");

            (bool success, Dictionary<string, string> services) images = await RedisImageService.GetImageAsync(isGetAll: true);

            if (!images.success || images.services == null || images.services.Count == 0)
            {
                return "ℹ️ Không tìm thấy cấu hình Image mẫu nào trong cơ sở dữ liệu hạ tầng.";
            }

            string result = "🖼️ SYSTEM DOCKER IMAGE BLUEPRINTS\n" +
                            "IMAGE TARGET             | TYPE     | INPORT | TARGET NETWORK | SCALE | BASE ENV\n" +
                            "-------------------------------------------------------------------------------------------------------\n";

            foreach (var img in images.services)
            {
                string jsonRaw = img.Value;

                try
                {
                    using JsonDocument doc = JsonDocument.Parse(jsonRaw);
                    JsonElement root = doc.RootElement;

                    string imageName = root.GetProperty("Image").GetString() ?? img.Key;
                    string network = root.GetProperty("Network").GetString() ?? "N/A";
                    string inPort = root.GetProperty("InPort").GetString() ?? "N/A";
                    string type = root.GetProperty("Type").GetString() ?? "N/A";
                    string env = root.GetProperty("Env").GetString() ?? "";
                    string neededCount = root.GetProperty("neededCount").GetString() ?? "1";

                    string envShort = env.Length > 25 ? env.Substring(0, 22) + "..." : (string.IsNullOrWhiteSpace(env) ? "None" : env);

                    result += $"{imageName,-24} | {type,-8} | {inPort,-6} | {network,-14} | {neededCount,-5} | {envShort}\n";
                }
                catch
                {
                    result += $"{img.Key,-24} | {"ERR",-8} | {"N/A",-6} | {"Data Error",-14} | {"N/A",-5} | {jsonRaw}\n";
                }
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Hoàn tất kết xuất dữ liệu Blueprint.");
            return result;
        }
    }
}
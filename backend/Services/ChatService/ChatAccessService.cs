using ChatOps.Models;
using AppContext = ChatOps.Data.AppContext;
using ChatOps.Services.RedisService;

namespace ChatOps.Services.ChatService
{
    public static class ChatAccessService
    {
        private static async Task SendLogWithDelayAsync(bool debug, string connectionId, string message)
        {
            await Task.Delay(100);
            await RedisChannelService.SendMessageToClientAsync(debug, connectionId, message);
        }
        public static async Task<string> OpenWeb(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline lệnh openweb...");

            parsed.TryGetValue("instance", out string? instanceFilter);
            parsed.TryGetValue("container", out string? containerFilter);

            if (string.IsNullOrWhiteSpace(instanceFilter) && string.IsNullOrWhiteSpace(containerFilter))
            {
                return "❌ Thiếu tham số 'instance' hoặc 'container' bắt buộc.";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔑 [Validation] Đang thẩm định phân quyền tài khoản cho user '{session.Username}'...");

            bool isFullAccess =
                session.Role == "admin" ||
                session.Role == "manager" ||
                session.Role == "dev" ||
                session.Role == "ops";

            string? targetContainer = "";
            string? targetImage = "";

            if (!string.IsNullOrWhiteSpace(containerFilter))
            {
                await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Docker API] Chế độ lọc theo Container. Đang truy vấn chi tiết `{containerFilter}`...");

                DockerContainer? container = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(containerFilter);

                if (container != null &&
                    !string.IsNullOrWhiteSpace(container.Name) &&
                    !container.Name.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                    !container.Name.Contains("<no value>"))
                {
                    targetContainer = container.Name;
                    targetImage = container.Image;
                }
                else
                {
                    await SendLogWithDelayAsync(session.Debug, connectionId, $"⚠️ [Docker API] Không tìm thấy thực thể container tương thích với từ khóa `{containerFilter}`.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(instanceFilter))
            {
                await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Docker API] Chế độ lọc theo App Instance `{instanceFilter}`. Đang quét danh sách container hệ thống...");

                List<DockerContainer> containers = await DockerService.Read.DockerReadContainer.GetContainersAsync();

                var validContainers = containers
                    .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Name))
                    .ToList();

                await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Parallel Cluster] Đang truy vấn song song siêu dữ liệu (Metadata) của {validContainers.Count} container...");

                var metadataTasks = validContainers.Select(async c =>
                {
                    var meta = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(c.Name);
                    return new { Container = c, Metadata = meta };
                });
                var results = await Task.WhenAll(metadataTasks);

                string? fallbackWebContainer = null;
                string? fallbackWebImage = null;

                foreach (var item in results)
                {
                    if (item.Metadata == null) continue;

                    string app = item.Metadata.AppProject ?? "";
                    string role = item.Metadata.ComposeService ?? "";

                    if (string.Equals(app, instanceFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(role, "lb", StringComparison.OrdinalIgnoreCase))
                        {
                            targetContainer = item.Container.Name;
                            targetImage = item.Container.Image;
                            await SendLogWithDelayAsync(session.Debug, connectionId, $"🎯 [Routing] Đã phát hiện container Load Balancer (lb) chủ đạo: `{targetContainer}`.");
                            break;
                        }

                        if (string.Equals(role, "web", StringComparison.OrdinalIgnoreCase))
                        {
                            fallbackWebContainer = item.Container.Name;
                            fallbackWebImage = item.Container.Image;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(targetContainer) && !string.IsNullOrWhiteSpace(fallbackWebContainer))
                {
                    targetContainer = fallbackWebContainer;
                    targetImage = fallbackWebImage;
                    await SendLogWithDelayAsync(session.Debug, connectionId, $"⚠️ [Routing] Không tìm thấy thực thể 'lb'. Tự động hạ cấp sang sử dụng web container dự phòng: `{targetContainer}`.");
                }
            }

            if (string.IsNullOrWhiteSpace(targetContainer))
            {
                return "❌ Không tìm thấy web container hoặc ứng dụng dịch vụ phù hợp với điều kiện lọc.";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Security] Thẩm định siêu dữ liệu và phân quyền sở hữu tài nguyên trên `{targetContainer}`...");

            ContainerMetadata? containermetadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(targetContainer);
            if (containermetadata == null)
            {
                return "❌ Không thể nạp thông tin cấu hình (Metadata) của container.";
            }

            if (!isFullAccess)
            {
                var ownerList = containermetadata.Owners;
                if (ownerList == null || !ownerList.Contains(session.Username))
                {
                    return "❌ Từ chối truy cập! Bạn không có quyền sở hữu để mở liên kết của hệ thống container này.";
                }
            }

            if (targetImage == null)
            {
                return "❌ Từ chối! Không tìm thấy Image";
            }

            string roleType = containermetadata.ComposeService ?? "";
            bool isWeb =
                roleType == "web" ||
                roleType == "lb" ||
                targetImage.Contains("nginx", StringComparison.OrdinalIgnoreCase) ||
                targetImage.Contains("httpd", StringComparison.OrdinalIgnoreCase);

            if (!isWeb)
            {
                return "❌ Từ chối! Container đích không phải là một dịch vụ giao diện Web.";
            }

            string? domain = containermetadata?.Domain;

            if (string.IsNullOrWhiteSpace(domain))
            {
                return $"❌ Container '{targetContainer}' chưa được cấu hình định danh Domain trỏ tới. Không thể tạo liên kết mở Web.";
            }

            string url = domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? $"{domain}:880"
                : $"http://{domain}:880";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Redirect] Khởi tạo URL điều hướng thành công: {url}. Đang phản hồi Client...");

            return $"GOTO|{url}\n" +
                   $"🚀 Đang mở Web...\n" +
                   $"🌐 Link: {url}\n" +
                   $"📦 Container: {targetContainer}";
        }

        public static async Task<string> OpenTool(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline lệnh opentool...");

            parsed.TryGetValue("container", out string? containerFilter);

            if (string.IsNullOrWhiteSpace(containerFilter))
            {
                return "❌ Thiếu tham số 'container' bắt buộc.";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔑 [Validation] Đang xác thực phân quyền tài khoản cho user '{session.Username}'...");

            bool isFullAccess = session.Role == "admin" || session.Role == "manager" || session.Role == "dev" || session.Role == "ops";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Docker API] Đang truy vấn Docker Daemon cho thực thể tool: `{containerFilter}`...");

            DockerContainer? container = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(containerFilter);

            if (container == null || string.IsNullOrWhiteSpace(container.Name) || container.Name.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                return $"❌ Không tìm thấy quản trị tool container nào có tên: {containerFilter}";
            }

            string targetContainer = container.Name;
            string targetImage = container.Image ?? "";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Security] Thẩm định Metadata và quyền sở hữu trên `{targetContainer}`...");

            ContainerMetadata? metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(targetContainer);
            if (metadata == null)
            {
                return "❌ Không thể nạp thông tin cấu hình (Metadata) của container.";
            }

            if (!isFullAccess && (metadata.Owners == null || !metadata.Owners.Contains(session.Username)))
            {
                return "❌ Từ chối truy cập! Bạn không có quyền sở hữu để mở liên kết của hệ thống tool này.";
            }

            bool isTool = targetContainer.Contains("phpmyadmin", StringComparison.OrdinalIgnoreCase) ||
                          targetContainer.Contains("pgadmin", StringComparison.OrdinalIgnoreCase) ||
                          targetImage.Contains("phpmyadmin", StringComparison.OrdinalIgnoreCase) ||
                          targetImage.Contains("pgadmin", StringComparison.OrdinalIgnoreCase);

            if (!isTool)
            {
                return "❌ Từ chối! Container đích không phải là một dịch vụ quản trị dữ liệu (Tool).";
            }

            string account = targetImage.Contains("phpmyadmin", StringComparison.OrdinalIgnoreCase)
                ? "\n🔑 User: root | Pass: root / 123"
                : "\n🔑 Login: admin@admin.com / 123";

            if (string.IsNullOrWhiteSpace(metadata.Domain))
            {
                return $"❌ Container '{targetContainer}' chưa cấu hình Domain.";
            }

            string url = (metadata.Domain.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? metadata.Domain : $"http://{metadata.Domain}") + ":880";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Redirect] Khởi tạo URL quản trị cơ sở dữ liệu: {url}...");

            return $"GOTO|{url}\n🚀 Đang mở Tool...\n🌐 Link: {url}\n📦 Container: {targetContainer}{account}";
        }

        public static async Task<string> EditWeb(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline lệnh editweb...");

            parsed.TryGetValue("container", out string? containerFilter);

            if (string.IsNullOrWhiteSpace(containerFilter))
            {
                return "❌ Thiếu tham số 'container' bắt buộc.";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔑 [Validation] Xác thực quyền thực thi của user '{session.Username}'...");

            bool isFullAccess = session.Role == "admin" || session.Role == "manager" || session.Role == "dev" || session.Role == "ops";

            DockerContainer? container = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(containerFilter);

            if (container == null || string.IsNullOrWhiteSpace(container.Name))
            {
                return $"❌ Không tìm thấy web container nào có tên: {containerFilter}";
            }

            string targetContainer = container.Name;
            string targetImage = container.Image ?? "";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Security] Thẩm định Metadata cấu hình môi trường cho `{targetContainer}`...");

            ContainerMetadata? metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(targetContainer);
            if (metadata == null)
            {
                return "❌ Không thể nạp thông tin Metadata.";
            }

            if (!isFullAccess && (metadata.Owners == null || !metadata.Owners.Contains(session.Username)))
            {
                return "❌ Từ chối truy cập! Bạn không có quyền sở hữu để chỉnh sửa web của container này.";
            }

            bool isWeb = targetImage.Contains("nginx", StringComparison.OrdinalIgnoreCase) ||
                         targetImage.Contains("httpd", StringComparison.OrdinalIgnoreCase);

            if (!isWeb)
            {
                return "❌ Từ chối! Container đích không phải là một dịch vụ giao diện Web.";
            }

            string editUrl = $"http://{AppContext.ServerDomain}:880/edit.html?name={targetContainer}";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Redirect] Khởi tạo môi trường, chuyển hướng đến trình soạn thảo trực tuyến...");

            return $"GOTO|{editUrl}\n🚀 Đang mở trình soạn thảo code...\n🌐 Link: {editUrl}\n📦 Container: {targetContainer}";
        }
    }
}
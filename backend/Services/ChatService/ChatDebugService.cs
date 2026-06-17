using ChatOps.Models;
using ChatOps.Services.DockerService.Read;
using ChatOps.Services.RedisService;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.ChatService
{
    public static class ChatDebugService
    {
        private static async Task SendLogWithDelayAsync(bool debug, string connectionId, string message)
        {
            await Task.Delay(100);
            await RedisChannelService.SendMessageToClientAsync(debug, connectionId, message);
        }

        public static async Task<string> GetLogs(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang trích xuất tham số hệ thống logs...");

            string instanceTarget = parsed.GetValueOrDefault("instance", "").Trim();
            string containerTarget = parsed.GetValueOrDefault("container", "").Trim();
            string slimit = parsed.GetValueOrDefault("lines", "100").Trim();

            if (string.IsNullOrWhiteSpace(instanceTarget) && string.IsNullOrWhiteSpace(containerTarget))
            {
                return "❌ Thiếu tham số 'instance' hoặc 'container' bắt buộc.";
            }

            int limit = 100;
            if (int.TryParse(slimit, out var customLimit) && customLimit > 0)
            {
                limit = customLimit;
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔑 [Node {AppContext.ServerID}] Đang xác thực phân quyền tài khoản {session.Username}...");

            bool isFullAccess =
                session.Role == "admin" ||
                session.Role == "manager" ||
                session.Role == "dev" ||
                session.Role == "ops";

            if (!string.IsNullOrEmpty(instanceTarget))
            {
                await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang truy quét file log tích lũy cho Instance: '{instanceTarget}' (Giới hạn: {limit} dòng)...");

                var metrics = LogService.LogService.ReadAppLog(instanceTarget, limit);

                await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Hoàn tất xử lý. Đang đóng gói dữ liệu Instance Logs...");

                if (metrics != null && metrics.Any())
                {
                    return $"📋 [INSTANCE LOGS: {instanceTarget} - {limit} LINES]\n" + string.Join("\n", metrics);
                }

                return $"❌ Không tìm thấy dữ liệu hoặc tệp tin nhật ký hoạt động cho Instance: {instanceTarget}";
            }

            if (!string.IsNullOrEmpty(containerTarget))
            {
                if (!isFullAccess)
                {
                    await SendLogWithDelayAsync(session.Debug, connectionId, $"🔒 [Node {AppContext.ServerID}] Đang đối chiếu quyền sở hữu Container cho user '{session.Username}'...");

                    ContainerMetadata? metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(containerTarget);
                    var ownerList = metadata?.Owners;

                    if (ownerList == null || !ownerList.Contains(session.Username))
                    {
                        return $"❌ Từ chối truy cập: Tài khoản {session.Username} không có quyền xem log của container '{containerTarget}'.";
                    }
                }

                await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] Đang thực thi lệnh kết nối Docker Daemon đọc live log container '{containerTarget}'...");

                string dockerLogs = await DockerReadLogsStats.GetContainerLogsAsync(containerTarget, limit);

                await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Đang kết xuất luồng dữ liệu Live Container Logs...");

                if (string.IsNullOrWhiteSpace(dockerLogs))
                {
                    return $"❌ Container '{containerTarget}' đang không phản hồi log hoặc rỗng.";
                }

                var formatted = dockerLogs
                    .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => $"[{containerTarget}] {x}");

                return $"🐳 [CONTAINER LIVE LOGS: {containerTarget} - LAST {limit} LINES]\n" + string.Join("\n", formatted);
            }

            return "❌ Lệnh thực thi không hợp lệ hoặc tham số truyền vào bị lỗi.";
        }
    }
}
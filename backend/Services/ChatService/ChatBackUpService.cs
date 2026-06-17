using ChatOps.Models;
using ChatOps.Services.RedisService;
using ChatOps.Services.SystemService;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.ChatService
{
    public static class ChatBackUpService
    {
        private static async Task SendLogWithDelayAsync(bool debug, string connectionId, string message)
        {
            await Task.Delay(100);
            await RedisChannelService.SendMessageToClientAsync(debug, connectionId, message);
        }
        public static async Task<string> CreateBackup(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline lệnh tạo bản sao lưu (CreateBackup)...");

            string instanceTarget = parsed.GetValueOrDefault("instance", "").Trim();
            string tag = parsed.GetValueOrDefault("tag", "").Trim();

            if (string.IsNullOrWhiteSpace(instanceTarget) || string.IsNullOrWhiteSpace(tag))
            {
                return "❌ Thiếu tham số 'instance' hoặc 'tag' bắt buộc.";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔑 [Validation] Đang thẩm định phân quyền tài khoản cho user '{session.Username}'...");

            bool isFullAccess =
                session.Role == "admin" ||
                session.Role == "manager" ||
                session.Role == "dev" ||
                session.Role == "ops";

            if (!isFullAccess)
            {
                ContainerMetadata? metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(instanceTarget);
                var ownerList = metadata?.Owners;

                if (ownerList == null || !ownerList.Contains(session.Username))
                {
                    return $"❌ Từ chối truy cập: Tài khoản {session.Username} không có quyền thực hiện tác vụ backup trên instance '{instanceTarget}'.";
                }
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Docker API] Đang quét các thực thể Database thuộc AppProject `{instanceTarget}`...");

            List<DockerContainer> containers = await DockerService.Read.DockerReadContainer.GetContainersAsync();
            DockerContainer? dbContainer = null;
            string detectedDbType = string.Empty;

            foreach (var c in containers)
            {
                ContainerMetadata? meta = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(c.Name);
                if (meta != null && meta.AppProject == instanceTarget && !string.IsNullOrEmpty(meta.DbType))
                {
                    dbContainer = c;
                    detectedDbType = meta.DbType.ToLower();
                    break;
                }
            }

            if (dbContainer == null)
            {
                return $"❌ Không tìm thấy container Database hợp lệ nào thuộc AppProject: {instanceTarget}";
            }

            string containerName = dbContainer.Name;

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Storage] Đã phát hiện DB [{detectedDbType.ToUpper()}] (`{containerName}`). Đang khởi tạo tiến trình trích xuất dữ liệu...");

            try
            {
                string result = await ChatOps.Services.DockerService.Update.DockerUpdateStorageApp.CreateInstanceBackupAsync(instanceTarget, tag, containerName, detectedDbType);

                if (result.StartsWith("❌"))
                {
                    return result;
                }

                await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Storage] Hoàn tất trích xuất dữ liệu. Đang đồng bộ cấu trúc lưu trữ và đóng gói tệp tin...");

                return $"✅ [INSTANCE BACKUP SUCCESS: {instanceTarget.ToUpper()}]\n{result}";
            }
            catch (Exception ex)
            {
                return $"❌ Lỗi hệ thống phát sinh trong quá trình chuyển tiếp tiến trình: {ex.Message}";
            }
        }

        public static async Task<string> ListBackup(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 Đang kết nối phân vùng lưu trữ để truy vấn danh sách bản sao lưu...");

            string instanceTarget = parsed.GetValueOrDefault("instance", "").Trim();

            if (string.IsNullOrWhiteSpace(instanceTarget))
            {
                return "❌ Thiếu tham số 'instance' bắt buộc.";
            }

            ContainerMetadata? metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(instanceTarget);

            string backupFolder = Path.Combine("/home/ubuntu/ChatOps/docker/Apps", instanceTarget, "backups");
            if (!Directory.Exists(backupFolder)) return $"❌ Chưa có bản backup nào cho: {instanceTarget}";

            var files = Directory.GetFiles(backupFolder, "*.sql")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .ToList();

            if (!files.Any()) return $"❌ Thư mục backup trống.";

            var listLines = files.Select(f => {
                string[] parts = f.Name.Replace(".sql", "").Split('_');
                string tag = parts.Length >= 3 ? parts[2] : "unknown";
                return $"🏷️ Tag: `[{tag}]` | 📄 File: `{f.Name}` | 🕒 {f.CreationTime:MM/dd HH:mm}";
            });

            return $"📋 [BACKUP LIST: {instanceTarget.ToUpper()}]\n" + string.Join("\n", listLines);
        }

        public static async Task<string> ExecuteRollback(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline phục hồi dữ liệu (ExecuteRollback)...");

            string instanceTarget = parsed.GetValueOrDefault("instance", "").Trim();
            string tag = parsed.GetValueOrDefault("tag", "").Trim();

            if (string.IsNullOrWhiteSpace(instanceTarget) || string.IsNullOrWhiteSpace(tag))
            {
                return "❌ Thiếu tham số 'instance' hoặc 'tag' bắt buộc.";
            }

            string backupFolder = Path.Combine("/home/ubuntu/ChatOps/docker/Apps", instanceTarget, "backups");

            var backupFile = Directory.GetFiles(backupFolder, $"*{tag}*.sql")
                                    .Select(f => new FileInfo(f))
                                    .OrderByDescending(f => f.CreationTime)
                                    .FirstOrDefault();

            if (backupFile == null) return $"❌ Không tìm thấy bản backup với tag: {tag}";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Docker API] Đang tìm kiếm container DB mục tiêu của instance `{instanceTarget}`...");

            List<DockerContainer> containers = await DockerService.Read.DockerReadContainer.GetContainersAsync();
            DockerContainer? dbContainer = null;
            string detectedDbType = string.Empty;

            foreach (var c in containers)
            {
                ContainerMetadata? meta = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(c.Name);
                if (meta != null && meta.AppProject == instanceTarget && !string.IsNullOrEmpty(meta.DbType))
                {
                    dbContainer = c;
                    detectedDbType = meta.DbType.ToLower();
                    break;
                }
            }

            if (dbContainer == null)
            {
                return $"❌ Không tìm thấy container Database hợp lệ nào thuộc AppProject: {instanceTarget}";
            }

            string containerName = dbContainer.Name;

            await SendLogWithDelayAsync(session.Debug, connectionId, $"⚡ [Database] Đang nạp và áp dụng bộ lọc xử lý chuỗi lệnh khôi phục về tag `{tag}`...");

            try
            {
                string importRes = string.Empty;

                if (detectedDbType.Contains("mysql") || detectedDbType.Contains("mariadb"))
                {
                    string importCmd = $"bash -c \"cat '{backupFile.FullName}' | sed '/^Warning:/d; /^In order to/d; /^mysqldump: \\[Warning\\]/d; s/SET @@GLOBAL.GTID_PURGED=/-- SET @@GLOBAL.GTID_PURGED=/g' | docker exec -i {containerName} mysql -u root -proot shopdb\"";
                    importRes = await SystemCommandService.RunAsync(importCmd);

                    if (importRes.Contains("Error") || importRes.Contains("failed"))
                    {
                        return $"❌ Tiến trình nạp đè cấu trúc trực tiếp thất bại:\n{importRes}";
                    }
                }
                else if (detectedDbType.Contains("postgres") || detectedDbType.Contains("postgresql"))
                {
                    string cleanCmd = $"docker exec -i -e PGPASSWORD=root {containerName} psql -U postgres -d shopdb -c \"DROP SCHEMA public CASCADE; CREATE SCHEMA public;\"";
                    await SystemCommandService.RunAsync(cleanCmd);

                    string importCmd = $"bash -c \"cat '{backupFile.FullName}' | docker exec -i -e PGPASSWORD=root {containerName} psql -U postgres\"";
                    importRes = await SystemCommandService.RunAsync(importCmd);
                    Console.WriteLine($"importRes: {importRes}");

                    var cleanLog = string.Join("\n", importRes.Split('\n')
                        .Where(line => !line.Contains("already exists") && !line.Contains("NOTICE:")));

                    if (cleanLog.Contains("Error") || cleanLog.Contains("failed") || cleanLog.Contains("FATAL") || cleanLog.Contains("password authentication"))
                    {
                        return $"❌ Khôi phục Postgres trực tiếp thất bại:\n{importRes}";
                    }
                }

                await SendLogWithDelayAsync(session.Debug, connectionId, $"✅ [Database] Phục hồi cấu trúc và dữ liệu thành công từ tệp tin: {backupFile.Name}");
                return $"✅ Rollback thành công instance {instanceTarget} với tag {tag}";
            }
            catch (Exception ex)
            {
                return $"❌ Lỗi hệ thống phát sinh khi thực thi chuỗi tiến trình trực tiếp: {ex.Message}";
            }
        }

        public static async Task<string> DeleteBackup(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline xóa bản sao lưu (DeleteBackup)...");

            string instanceTarget = parsed.GetValueOrDefault("instance", "").Trim();
            string tag = parsed.GetValueOrDefault("tag", "").Trim();

            if (string.IsNullOrWhiteSpace(instanceTarget) || string.IsNullOrWhiteSpace(tag))
            {
                return "❌ Thiếu tham số 'instance' hoặc 'tag' bắt buộc.";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔑 [Validation] Đang xác thực phân quyền tài khoản cho user '{session.Username}'...");

            bool isFullAccess =
                session.Role == "admin" ||
                session.Role == "manager" ||
                session.Role == "dev" ||
                session.Role == "ops";

            if (!isFullAccess)
            {
                ContainerMetadata? metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(instanceTarget);
                if (metadata == null || metadata.Owners == null || !metadata.Owners.Contains(session.Username))
                {
                    return $"❌ Từ chối truy cập: Bạn không có quyền xóa bản sao lưu của instance '{instanceTarget}'.";
                }
            }

            string backupFolder = Path.Combine("/home/ubuntu/ChatOps/docker/Apps", instanceTarget, "backups");
            if (!Directory.Exists(backupFolder))
            {
                return $"❌ Thư mục backup cho instance '{instanceTarget}' không tồn tại.";
            }

            var filesToDelete = Directory.GetFiles(backupFolder, $"*{tag}*.sql")
                .Select(f => new FileInfo(f))
                .ToList();

            if (!filesToDelete.Any())
            {
                return $"❌ Không tìm thấy bản backup nào với tag: {tag}";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🗑️ [Storage] Tiến hành xóa bỏ tệp sao lưu ra khỏi hệ thống phân vùng...");

            try
            {
                int count = 0;
                foreach (var file in filesToDelete)
                {
                    file.Delete();
                    count++;
                }

                await SendLogWithDelayAsync(session.Debug, connectionId, $"🧹 [Storage] Hoàn tất dọn dẹp các tệp tin backup liên quan đến tag '{tag}' khỏi ổ đĩa.");

                return $"✅ Đã xóa thành công {count} bản backup có tag '{tag}' cho instance '{instanceTarget}'.";
            }
            catch (Exception ex)
            {
                return $"❌ Lỗi hệ thống khi xóa file: {ex.Message}";
            }
        }
    }
}
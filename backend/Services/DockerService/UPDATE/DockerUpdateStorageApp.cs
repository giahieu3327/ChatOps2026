using ChatOps.Services.SystemService;

namespace ChatOps.Services.DockerService.Update
{
    public static class DockerUpdateStorageApp
    {
        public static async Task<string> CopyFileToContainerAsync(string hostPath, string containerName, string containerPath)
        {
            if (string.IsNullOrWhiteSpace(hostPath) || string.IsNullOrWhiteSpace(containerName) || string.IsNullOrWhiteSpace(containerPath))
            {
                return "❌ Thiếu thông tin đường dẫn nguồn, tên container hoặc đường dẫn đích.";
            }

            return await Task.Run(async () => 
                await SystemCommandService.RunAsync($"docker cp {hostPath.Trim()} {containerName.Trim()}:{containerPath.Trim()} 2>&1")
            );
        }

        public static async Task<string> CreateSymlinkAsync(string targetPath, string linkPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(linkPath))
            {
                return "❌ Đường dẫn gốc hoặc đường dẫn liên kết không hợp lệ.";
            }

            return await Task.Run(async () => 
                await SystemCommandService.RunAsync($"ln -sf {targetPath.Trim()} {linkPath.Trim()} 2>&1")
            );
        }

        public static async Task<string> LoadPgAdminServersAsync(string containerName, string adminEmail)
        {
            if (string.IsNullOrWhiteSpace(containerName) || string.IsNullOrWhiteSpace(adminEmail))
            {
                return "❌ Thiếu thông tin Container PgAdmin hoặc Email Quản trị.";
            }

            string cmd = $"docker exec -u root " +
                         $"-e PGADMIN_SERVER_JSON_MODIFY=1 " +
                         $"-e PGADMIN_SETUP_EMAIL={adminEmail.Trim()} " +
                         $"-e PGADMIN_SETUP_PASSWORD=123 " +
                         $"{containerName.Trim()} " +
                         $"/venv/bin/python3 /pgadmin4/setup.py load-servers /pgadmin4/servers.json --user {adminEmail.Trim()} --replace 2>&1";

            return await Task.Run(async () => await SystemCommandService.RunAsync(cmd));
        }

        public static async Task CheckDatabaseAsync(string app)
        {
            if (string.IsNullOrWhiteSpace(app)) return;
            app = app.Trim();

            string dbCheck = await Task.Run(async () => 
                await SystemCommandService.RunAsync($"docker ps --format '{{{{.Names}}}}' | grep '^{app}-db' || true")
            );

            if (!string.IsNullOrWhiteSpace(dbCheck)) return;

            LogService.LogService.WriteAppLog(app, "🚨 DATABASE DOWN → ATTEMPTING RESTART VIA DOCKER COMPOSE");

            string runtimeDir = $"/home/ubuntu/ChatOps/docker/Apps/{app}";
            if (!Directory.Exists(runtimeDir)) return;

            string gitCompose = Path.Combine(runtimeDir, "docker-git.yml");
            string registryCompose = Path.Combine(runtimeDir, "docker-registry.yml");
            string targetFile = File.Exists(gitCompose) ? "docker-git.yml" : (File.Exists(registryCompose) ? "docker-registry.yml" : "");

            if (string.IsNullOrEmpty(targetFile)) return;

            await Task.Run(async () => 
                await SystemCommandService.RunAsync($"cd {runtimeDir} && docker compose -f {targetFile} up -d --no-recreate db 2>&1")
            );
        }

        public static async Task<string> CreateInstanceBackupAsync(string instanceTarget, string tag, string containerId, string dbType)
        {
            string backupFolder = Path.Combine("/home/ubuntu/ChatOps/docker/Apps", instanceTarget, "backups");
            if (!Directory.Exists(backupFolder))
            {
                Directory.CreateDirectory(backupFolder);
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"{instanceTarget}_{dbType}_{tag}_{timestamp}.sql";
            string backupPath = Path.Combine(backupFolder, fileName);
            string backupCommand = "";

            if (dbType == "mysql" || dbType == "mariadb")
            {
                string rootPass = "root";
                string dbName = "shopdb";
                backupCommand = $"docker exec -t {containerId} mysqldump -u root -p{rootPass} {dbName} > \"{backupPath}\"";
            }
            else if (dbType == "postgres" || dbType == "postgresql")
            {
                // ✅ Sửa user quản trị tối cao từ "root" thành "postgres"
                string pgUser = "postgres"; 
                string pgPass = "root";
                
                // ✅ Sử dụng biến môi trường PGPASSWORD để bypass password prompt của Postgres mà không bị treo
                backupCommand = $"docker exec -t -e PGPASSWORD={pgPass} {containerId} pg_dumpall -U {pgUser} > \"{backupPath}\"";
            }
            else
            {
                return $"❌ Hệ thống hiện tại chưa hỗ trợ tự động kết xuất dữ liệu cho cấu trúc DB: {dbType}";
            }

            try
            {
                string res = await SystemCommandService.RunAsync(backupCommand);

                // ✅ Bổ sung kiểm tra "password authentication failed" đặc trưng của Postgres trong log lỗi
                if (res.Contains("Error") || res.Contains("failed") || res.Contains("password authentication") || !File.Exists(backupPath) || new FileInfo(backupPath).Length == 0)
                {
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                    return $"❌ Tiến trình tác vụ Backup thất bại:\n{res}";
                }

                return $"✅ [DOCKER BACKUP DONE]\n📦 File: `{fileName}`\n📍 Path: `{backupPath}`\n💾 Size: {new FileInfo(backupPath).Length / 1024} KB";
            }
            catch (Exception ex)
            {
                if (File.Exists(backupPath)) File.Delete(backupPath);
                return $"❌ Lỗi hệ thống phát sinh trong quá trình xử lý CLI: {ex.Message}";
            }
        }
    }
}
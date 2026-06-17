using ChatOps.Services.SystemService;

namespace ChatOps.Services.FileService
{
    /// <summary>
    /// Lớp chuyên trách xử lý các tệp cấu hình bổ trợ cho các Admin Tool (pgAdmin)
    /// </summary>
    public static class PgAdminFileConfigurator
    {
        /// <summary>
        /// Khởi tạo thư mục làm việc và kết xuất tệp cấu hình tự động liên kết máy chủ hàng loạt cho pgAdmin.
        /// Hỗ trợ xử lý bất đồng bộ thực sự (True Async I/O).
        /// </summary>
        public static async Task<string> GenerateBulkServersConfiguration(string containerName, List<string> dbHosts)
        {
            if (dbHosts == null)
            {
                return "❌ Danh sách DB máy chủ trống.";
            }

            string baseDir = Path.Combine("/home/ubuntu/ChatOps/docker/Containers", containerName, "pgadmin-data");
            if (!Directory.Exists(baseDir))
            {
                Directory.CreateDirectory(baseDir);
            }

            string jsonFile = Path.Combine(baseDir, "servers.json");

            // Xây dựng cấu trúc Servers mới hoàn toàn dựa trên danh sách dbHosts truyền vào
            var serversMap = new Dictionary<string, object>();
            int index = 1;

            foreach (var dbHost in dbHosts)
            {
                if (string.IsNullOrWhiteSpace(dbHost)) continue;

                string pureHost = dbHost.Trim();
                int purePort = 5432; // Cổng mặc định của PostgreSQL

                // Xử lý tách Host và Port nếu có dạng IP:Port hoặc Host:Port
                if (pureHost.Contains(":"))
                {
                    var parts = pureHost.Split(':');
                    pureHost = parts[0].Trim();
                    if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int parsedPort))
                    {
                        purePort = parsedPort;
                    }
                }

                // Định dạng cấu hình chuẩn hóa bắt buộc phải có thuộc tính Port
                var serverNode = new
                {
                    Name = pureHost.Contains(".") ? $"Postgres_{dbHost.Trim()}" : $"Postgres_{pureHost}",
                    Group = "Servers",
                    Host = pureHost,
                    Port = purePort,
                    MaintenanceDB = "postgres",
                    Username = "postgres",
                    SSLMode = "prefer"
                };

                serversMap.Add(index.ToString(), serverNode);
                index++;
            }

            // Đóng gói cấu trúc JSON tổng thể
            var rootConfig = new { Servers = serversMap };
            
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            string finalJsonContent = System.Text.Json.JsonSerializer.Serialize(rootConfig, options);

            // Ghi đè toàn bộ file để đồng bộ chính xác dữ liệu (Xóa sạch các node cũ đã detach)
            await File.WriteAllTextAsync(jsonFile, finalJsonContent);

            // Mở quyền file rộng để pgAdmin container có thể truy xuất mà không bị kẹt Permission
            try
            {
                await SystemCommandService.RunAsync($"chmod 666 {jsonFile}");
            }
            catch { }

            return jsonFile;
        }
        /// <summary>
        /// Đọc tệp cấu hình servers.json và bóc tách chính xác dựa trên định dạng của trường Host.
        /// Chuyển dịch toàn bộ sang Async I/O thuần túy.
        /// </summary>
        public static async Task<List<string>> GetAttachedHosts(string containerName)
        {
            var attachedDbs = new List<string>();
            string jsonFile = Path.Combine("/home/ubuntu/ChatOps/docker/Containers", containerName, "pgadmin-data", "servers.json");

            if (!System.IO.File.Exists(jsonFile))
            {
                return attachedDbs;
            }

            try
            {
                string existingContent = await System.IO.File.ReadAllTextAsync(jsonFile);
                var jsonDoc = System.Text.Json.Nodes.JsonNode.Parse(existingContent);
                var serversObj = jsonDoc?["Servers"]?.AsObject();

                if (serversObj != null)
                {
                    foreach (var item in serversObj)
                    {
                        string host = item.Value?["Host"]?.ToString() ?? "";
                        string portStr = item.Value?["Port"]?.ToString() ?? "5432";

                        if (!string.IsNullOrWhiteSpace(host))
                        {
                            host = host.Trim();

                            // TH 1: Nếu Host có chứa dấu "." -> Xác định là IP vật lý, bốc kèm Port kết hợp lại thành IP:Port
                            if (host.Contains("."))
                            {
                                attachedDbs.Add($"{host}:{portStr.Trim()}");
                            }
                            // TH 2: Nếu Host không chứa dấu "." -> Xác định là Container Name thuần túy nội bộ
                            else
                            {
                                attachedDbs.Add(host);
                            }
                        }
                    }
                }
            }
            catch
            {
                return new List<string>();
            }

            return attachedDbs;
        }
    
    }
}
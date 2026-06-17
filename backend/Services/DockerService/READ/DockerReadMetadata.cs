using System.Text.Json;
using ChatOps.Models;
using ChatOps.Services.SystemService;

namespace ChatOps.Services.DockerService.Read
{
    /// <summary>
    /// Lớp chuyên trách truy vấn chuyên sâu về Metadata (Labels, Biến môi trường, Cấu hình hệ thống) của Container
    /// </summary>
    public static class DockerReadMetadata
    {
        /// <summary>
        /// Truy vấn TOÀN BỘ Metadata của một Container bằng Name hoặc ID chỉ với 1 lần thực thi lệnh hệ thống (Bất đồng bộ)
        /// </summary>
        public static async Task<ContainerMetadata?> GetMetadataAsync(string containerName)
        {
            if (string.IsNullOrWhiteSpace(containerName)) return null;

            containerName = containerName.Trim();

            // Sử dụng dấu nháy đơn `'` bọc ngoài template format trên Linux 
            // để tránh xung đột với dấu ngoặc kép `"` cấu trúc bên trong JSON.
            string format = "{\"ContainerName\":\"{{.Name}}\",\"RestartPolicy\":\"{{.HostConfig.RestartPolicy.Name}}\",\"Labels\":{{json .Config.Labels}},\"Env\":{{json .Config.Env}}}";
            string cmd = $"docker inspect --format '{format}' {containerName} 2>&1";

            string output = await Task.Run(async () => await SystemCommandService.RunAsync(cmd));

            if (output.Contains("error", StringComparison.OrdinalIgnoreCase) || output.Contains("No such", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                // Cơ chế phòng vệ: Nếu đầu ra vẫn bị lỗi chứa ký tự bọc `\`, tiến hành dọn sạch trước khi Parse
                if (output.StartsWith("{\\", StringComparison.Ordinal) || output.Contains("\\\""))
                {
                    output = output.Replace("\\\"", "\"").Replace("\\", "");
                }

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;

                var metadata = new ContainerMetadata
                {
                    ContainerName = root.GetProperty("ContainerName").GetString()?.TrimStart('/') ?? containerName,
                    RestartPolicy = root.GetProperty("RestartPolicy").GetString() ?? "no"
                };

                // 1. Phân tích các nhãn Labels
                if (root.TryGetProperty("Labels", out var labelsElement) && labelsElement.ValueKind == JsonValueKind.Object)
                {
                    var labels = new Dictionary<string, string>();
                    foreach (var prop in labelsElement.EnumerateObject())
                    {
                        labels[prop.Name] = prop.Value.GetString() ?? "";
                    }

                    metadata.Service = labels.GetValueOrDefault("service", "");
                    metadata.AppProject = labels.GetValueOrDefault("com.docker.compose.project", "");
                    metadata.ComposeService = labels.GetValueOrDefault("com.docker.compose.service", "");
                    metadata.ScaleIndex = labels.GetValueOrDefault("com.docker.compose.container-number", "");
                    metadata.DependsOn = labels.GetValueOrDefault("com.docker.compose.depends_on", "");
                    metadata.DbType = labels.GetValueOrDefault("dbtype", "");
                    metadata.Domain = labels.GetValueOrDefault("domain", "");

                    string ownerRaw = labels.GetValueOrDefault("owner", "");
                    if (!string.IsNullOrWhiteSpace(ownerRaw))
                    {
                        metadata.Owners = ownerRaw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                 .Select(o => o.Trim()).ToList();
                    }
                }

                // 2. Phân tích các biến môi trường Env (Chuyển mảng ["KEY=VALUE"] sang Dictionary)
                if (root.TryGetProperty("Env", out var envElement) && envElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var envItem in envElement.EnumerateArray())
                    {
                        string envStr = envItem.GetString() ?? "";
                        int idx = envStr.IndexOf('=');
                        if (idx > 0)
                        {
                            string key = envStr.Substring(0, idx);
                            string val = envStr.Substring(idx + 1);
                            metadata.Environments[key] = val;
                        }
                    }
                }

                return metadata;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [GetMetadataAsync] Lỗi Parse JSON: {ex.Message}. Raw Output: {output}");
                return null;
            }
        }

        /// <summary>
        /// Đọc trực tiếp một biến môi trường thời gian thực (Live Runtime Env) bên trong Container bằng cách thực thi lệnh (Bất đồng bộ)
        /// </summary>
        public static async Task<string> GetLiveEnvAsync(string containerName, string envName)
        {
            if (string.IsNullOrWhiteSpace(containerName) || string.IsNullOrWhiteSpace(envName)) return string.Empty;

            // Dùng docker exec chạy printenv trong container
            return await Task.Run(async () =>
                await SystemCommandService.RunAsync($"docker exec {containerName.Trim()} sh -c \"printenv {envName.Trim()}\" 2>/dev/null")
            );
        }

        /// <summary>
        /// Kiểm tra nhanh một Domain cấu hình ở Label xem có đang tồn tại trên hệ thống hay không (Bất đồng bộ)
        /// </summary>
        public static async Task<bool> DomainExistsAsync(string targetDomain)
        {
            if (string.IsNullOrWhiteSpace(targetDomain)) return false;

            string result = await Task.Run(async () =>
                await SystemCommandService.RunAsync($"docker ps -a -q --filter \"label=domain={targetDomain.Trim()}\"")
            );
            return !string.IsNullOrEmpty(result);
        }

        /// <summary>
        /// Tiện ích bóc tách nhanh Domain từ chuỗi Json Labels có sẵn
        /// </summary>
        public static string ExtractDomainFromLabels(string labelsJson)
        {
            if (string.IsNullOrWhiteSpace(labelsJson)) return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(labelsJson);
                return doc.RootElement.TryGetProperty("domain", out var element) ? element.GetString() ?? "" : "";
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Quét toàn bộ Docker Engine thời gian thực để thu thập bức tranh các dịch vụ đang chạy theo Domain (Bất đồng bộ)
        /// </summary>
        public static async Task<Dictionary<string, ServiceConfig>> GetLatestDockerServicesAsync()
        {
            var freshServices = new Dictionary<string, ServiceConfig>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // 1. Chỉ gọi duy nhất 1 lệnh hệ thống để lấy toàn bộ các Container đang chạy
                var runningContainers = await DockerReadContainer.GetContainersAsync(showAll: false);

                foreach (var container in runningContainers)
                {
                    // Đọc thông tin chi tiết (Metadata bao gồm Labels) của từng container từ kết quả quét
                    var containerMeta = await GetMetadataAsync(container.Id);
                    if (containerMeta == null) continue;

                    // Kiểm tra container có nhãn Domain hay không
                    string domain = containerMeta.Domain;
                    if (string.IsNullOrEmpty(domain)) continue;

                    // Bóc tách CHÍNH XÁC các cổng Public được mở ra ngoài Node (Bỏ qua private port)
                    var discoveredPorts = DockerReadNetwork.ParsePorts(container.Ports);
                    if (discoveredPorts.Count == 0) continue;

                    // 2. Gom cụm và hợp nhất (Merge) dữ liệu cấu hình theo tên miền
                    if (!freshServices.TryGetValue(domain, out var existingConfig))
                    {
                        bool IsAppService = false;
                        string AppService = "";
                        if(!string.IsNullOrWhiteSpace(containerMeta.AppProject))
                        {
                            IsAppService = true;
                            AppService = containerMeta.AppProject;
                        }
                        freshServices[domain] = new ServiceConfig
                        {
                            Domain = domain,
                            ContainerName = container.Name,
                            IsApp = IsAppService, // Xác định nếu là cụm Load Balancer ứng dụng
                            AppService = AppService,
                            Ports = discoveredPorts.Distinct().ToList()
                        };
                    }
                    else
                    {
                        // Nếu nhiều container (scale replica) chạy chung một domain -> Hợp nhất danh sách Port an toàn
                        existingConfig.Ports = existingConfig.Ports.Concat(discoveredPorts).Distinct().ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [GetLatestDockerServicesAsync] Lỗi quét Docker Engine: {ex.Message}");
            }

            return freshServices;
        }    
    }
}
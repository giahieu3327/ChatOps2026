using System.Text.Json;
using System.Text.RegularExpressions;
using ChatOps.Models;
using ChatOps.Services.SystemService;

namespace ChatOps.Services.DockerService.Read
{
    /// <summary>
    /// Lớp chuyên trách truy vấn chuyên sâu về trạng thái và cấu hình Mạng (Network/Ports) của Container
    /// </summary>
    public static class DockerReadNetwork
    {
        /// <summary>
        /// Truy vấn TOÀN BỘ cấu hình mạng và Port của một Container chỉ với 1 lần thực thi lệnh hệ thống (Bất đồng bộ)
        /// </summary>
        public static async Task<ContainerNetworkDetails?> GetContainerNetworkDetailsAsync(string containerName)
        {
            if (string.IsNullOrWhiteSpace(containerName)) return null;

            containerName = containerName.Trim();

            // Cấu trúc chuỗi JSON tùy biến để lấy Networks, ExposedPorts, PortBindings và Links cùng một lúc
            string format = "{\"Networks\":{{json .NetworkSettings.Networks}},\"ExposedPorts\":{{json .Config.ExposedPorts}},\"Ports\":{{json .NetworkSettings.Ports}},\"Links\":{{json .HostConfig.Links}}}";
            string cmd = $"docker inspect --format '{format}' {containerName} 2>&1";
            string output = await Task.Run(async () => await SystemCommandService.RunAsync(cmd));

            if (output.Contains("error", StringComparison.OrdinalIgnoreCase) || output.Contains("No such", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;

                var details = new ContainerNetworkDetails { ContainerName = containerName };

                // 1. Phân tích danh sách Networks kết nối
                if (root.TryGetProperty("Networks", out var netElement) && netElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in netElement.EnumerateObject())
                    {
                        details.Networks.Add(prop.Name);
                    }
                }

                // 2. Phân tích các cổng nội bộ ExposedPorts
                if (root.TryGetProperty("ExposedPorts", out var expElement) && expElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in expElement.EnumerateObject())
                    {
                        // Chuỗi gốc: "80/tcp" -> lấy "80"
                        string cleanPort = prop.Name.Split('/')[0];
                        details.AllInPorts.Add(cleanPort);
                    }
                }

                // Xác định cổng chính ưu tiên (giữ nguyên logic nghiệp vụ: ưu tiên cổng 80)
                if (details.AllInPorts.Contains("80")) details.PrimaryInPort = "80";
                else if (details.AllInPorts.Count > 0) details.PrimaryInPort = details.AllInPorts[0];

                // 3. Phân tích Bản đồ Port mappings sinh ra chuỗi hiển thị gọn gàng
                if (root.TryGetProperty("Ports", out var portsElement) && portsElement.ValueKind == JsonValueKind.Object)
                {
                    var mappings = new List<string>();
                    foreach (var prop in portsElement.EnumerateObject())
                    {
                        string containerPort = prop.Name; // "80/tcp"
                        if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() > 0)
                        {
                            var firstBinding = prop.Value[0];
                            string hostIp = firstBinding.GetProperty("HostIp").GetString() ?? "";
                            string hostPort = firstBinding.GetProperty("HostPort").GetString() ?? "";
                            mappings.Add($"{containerPort} -> {hostIp}:{hostPort}");
                        }
                        else
                        {
                            mappings.Add($"{containerPort} -> internal");
                        }
                    }
                    details.RawPortMappings = string.Join(" | ", mappings);
                }

                // 4. Phân tích Container Links (Nếu có)
                if (root.TryGetProperty("Links", out var linksElement) && linksElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var linkItem in linksElement.EnumerateArray())
                    {
                        string linkStr = linkItem.GetString() ?? "";
                        // Dạng chuỗi: "/target_container:/source_container/link_name"
                        string leftPart = linkStr.Split(':').FirstOrDefault()?.Replace("/", "") ?? "";
                        if (!string.IsNullOrWhiteSpace(leftPart) && !details.Links.Contains(leftPart))
                        {
                            details.Links.Add(leftPart);
                        }
                    }
                }

                return details;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Kiểm tra nhanh xem một Docker Network có tồn tại trên Host hay không (Bất đồng bộ)
        /// </summary>
        public static async Task<bool> DoesNetworkExistAsync(string networkName)
        {
            if (string.IsNullOrWhiteSpace(networkName)) return false;

            string output = await Task.Run(async () => 
                await SystemCommandService.RunAsync($"docker network ls --filter name=^{networkName.Trim()}$ --format \"{{{{.Name}}}}\"")
            );
            return output == networkName.Trim();
        }

        /// <summary>
        /// Kiểm tra nhanh một Container có đang được nối vào một Vùng mạng cụ thể hay không (Bất đồng bộ)
        /// </summary>
        public static async Task<bool> IsContainerConnectedToNetworkAsync(string containerName, string networkName)
        {
            if (string.IsNullOrWhiteSpace(containerName) || string.IsNullOrWhiteSpace(networkName)) return false;

            var details = await GetContainerNetworkDetailsAsync(containerName);
            if (details == null) return false;

            return details.Networks.Contains(networkName.Trim());
        }

        /// <summary>
        /// Tiện ích bóc tách danh sách số hiệu cổng (Port) từ chuỗi Mapping thô
        /// </summary>
public static List<string> ParsePorts(string portMappings)
{
    if (string.IsNullOrWhiteSpace(portMappings)) return new List<string>();

    var publicPorts = new List<string>();

    // Chuỗi mẫu của Docker: "0.0.0.0:7001->80/tcp, :::7001->80/tcp"
    // Regex này bắt cụm số nằm sau dấu hai chấm ':' và đứng trước '->' 
    var matches = Regex.Matches(portMappings, @":(\d+)->");

    foreach (Match match in matches)
    {
        if (match.Groups.Count > 1)
        {
            string portStr = match.Groups[1].Value;
            publicPorts.Add(portStr);
        }
    }

    return publicPorts.Distinct().ToList();
}
    }
}
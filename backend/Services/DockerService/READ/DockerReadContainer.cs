using System.Text.Json;
using ChatOps.Models;
using ChatOps.Services.SystemService;

namespace ChatOps.Services.DockerService.Read
{
    /// <summary>
    /// Lớp chuyên trách truy vấn (Read) thông tin trạng thái toàn bộ Docker Container trên Host
    /// </summary>
    public static class DockerReadContainer
    {
        /// <summary>
        /// Lấy thông tin chi tiết của DUY NHẤT một Container bằng Name hoặc ID (Bất đồng bộ)
        /// </summary>
        public static async Task<DockerContainer?> GetContainerDetailsAsync(string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId)) return null;

            // Sử dụng docker inspect xuất trực tiếp định dạng JSON cấu trúc gọn để parse
            string format = "{\"Id\":\"{{.Id}}\",\"Name\":\"{{.Name}}\",\"Image\":\"{{.Config.Image}}\",\"Status\":\"{{.State.Status}}\",\"Running\":{{.State.Running}},\"Labels\":{{json .Config.Labels}},\"Ports\":\"{{range $p, $conf := .NetworkSettings.Ports}}{{if $conf}}{{range $conf}}{{.HostIp}}:{{.HostPort}}->{{$p}}, {{end}}{{else}}{{$p}}, {{end}}{{end}}\"}";
            string cmd = $"docker inspect --format '{format}' {nameOrId.Trim()} 2>&1";

            string output = await Task.Run(async () => await SystemCommandService.RunAsync(cmd));

            if (output.Contains("error", StringComparison.OrdinalIgnoreCase) || output.Contains("No such", StringComparison.OrdinalIgnoreCase))
                return null;

            return ParseJsonToModel(output);
        }

        /// <summary>
        /// Lấy danh sách TOÀN BỘ Container đang tồn tại trên Host (Bất đồng bộ)
        /// </summary>
        /// <param name="showAll">true: Lấy tất cả | false: Chỉ lấy container đang chạy</param>
        public static async Task<List<DockerContainer>> GetContainersAsync(bool showAll = true)
        {
            string cmd = $"docker ps {(showAll ? "-a" : "")} --format \"{{{{.ID}}}}|{{{{.Names}}}}|{{{{.Image}}}}|{{{{.State}}}}|{{{{.Ports}}}}\"";
            string output = await Task.Run(async () => await SystemCommandService.RunAsync(cmd));

            if (string.IsNullOrWhiteSpace(output) || IsDockerDaemonDown(output)) 
                return new List<DockerContainer>();

            var list = new List<DockerContainer>();
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var parts = line.Split('|');
                if (parts.Length < 5) continue;

                var statusRaw = parts[3].Trim().ToLower();
                var (inPort, outPort) = ParsePorts(parts[4].Trim());

                list.Add(new DockerContainer
                {
                    Id = parts[0].Trim(),
                    Name = parts[1].Trim(),
                    Image = parts[2].Trim(),
                    Status = statusRaw,
                    IsRunning = statusRaw == "running",
                    Ports = parts[4].Trim(),
                    InPorts = inPort,
                    OutPorts = outPort
                });
            }
            return list;
        }

        /// <summary>
        /// Lấy danh sách TOÀN BỘ Container đang ở trạng thái DỪNG (Stopped/Exited/Created) trên Host (Bất đồng bộ)
        /// </summary>
        public static async Task<List<DockerContainer>> GetStoppedContainersAsync()
        {
            // Sử dụng các bộ lọc status của Docker để chỉ lấy container không chạy
            // status=exited (bị stop), status=created (mới tạo chưa start), status=dead (bị lỗi nặng)
            string cmd = "docker ps -a -f \"status=exited\" -f \"status=created\" -f \"status=dead\" --format \"{{.ID}}|{{.Names}}|{{.Image}}|{{.State}}|{{.Ports}}\"";
            string output = await Task.Run(async () => await SystemCommandService.RunAsync(cmd));

            if (string.IsNullOrWhiteSpace(output) || IsDockerDaemonDown(output)) 
                return new List<DockerContainer>();

            var list = new List<DockerContainer>();
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var parts = line.Split('|');
                if (parts.Length < 5) continue;

                var statusRaw = parts[3].Trim().ToLower();
                var (inPort, outPort) = ParsePorts(parts[4].Trim());

                list.Add(new DockerContainer
                {
                    Id = parts[0].Trim(),
                    Name = parts[1].Trim(),
                    Image = parts[2].Trim(),
                    Status = statusRaw,
                    IsRunning = false, // Chắc chắn là false vì đã lọc từ lệnh docker
                    Ports = parts[4].Trim(),
                    InPorts = inPort,
                    OutPorts = outPort
                });
            }

            return list;
        }

        /// <summary>
        /// Lọc danh sách container dựa trên tiền tố Name (Prefix) hoặc tên App (Bất đồng bộ)
        /// </summary>
        public static async Task<List<DockerContainer>> GetContainersByFilterAsync(string appFilter, string? typeFilter = null)
        {
            var allContainers = await GetContainersAsync(showAll: true);
            
            // Nếu có cả type (ví dụ: app="shop", type="db" -> tìm kiếm "^shop-db-")
            if (!string.IsNullOrEmpty(typeFilter))
            {
                string targetPrefix = $"{appFilter}-{typeFilter}-";
                return allContainers.Where(c => c.Name.StartsWith(targetPrefix)).OrderBy(c => c.Name).ToList();
            }

            // Ngược lại thì tìm kiếm chứa từ khóa (Grep)
            return allContainers.Where(c => c.Name.Contains(appFilter)).ToList();
        }

        /// <summary>
        /// Quét và lấy danh sách Container IDs đang chạy dựa theo một Label cụ thể (Phục vụ Auto-scaling)
        /// </summary>
        /// <returns>Trả về null nếu Docker Daemon bị sập, trả về danh sách IDs nếu thành công</returns>
        public static async Task<List<string>?> GetRunningContainerIdsWithLabelAsync(string label)
        {
            string cmd = $"docker ps -q -f \"label={label}\" 2>&1";
            string output = await Task.Run(async () => await SystemCommandService.RunAsync(cmd));

            if (IsDockerDaemonDown(output)) return null; // Báo hiệu Docker sập để tránh xóa nhầm dữ liệu cụm

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(id => id.Trim()).ToList();
        }

        /// <summary>
        /// Kiểm tra nhanh xem một Container có tồn tại trên Host dựa vào tên chính xác hay không (Bất đồng bộ).
        /// </summary>
        /// <param name="containerName">Tên chính xác của container cần kiểm tra.</param>
        /// <returns>True nếu container tồn tại (kể cả đang chạy hay đang dừng); ngược lại là False.</returns>
        public static async Task<bool> IsContainerExistsAsync(string containerName)
        {
            if (string.IsNullOrWhiteSpace(containerName)) return false;

            // Sử dụng filter với regex khớp chính xác tên (^name$) để tránh tìm kiếm nhầm các container chứa tên tương tự
            string cmd = $"docker ps -a -f \"name=^{containerName.Trim()}$\" --format \"{{{{.Names}}}}\" 2>&1";
            string output = await Task.Run(async () => await SystemCommandService.RunAsync(cmd));

            if (IsDockerDaemonDown(output) || string.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            // Nếu output trả về đúng tên container thì xác nhận là có tồn tại
            return output.Split('\n').Any(name => name.Trim() == containerName.Trim());
        }

        #region --- TRỢ THỦ PHÂN TÍCH (HELPERS) ---

        private static bool IsDockerDaemonDown(string output)
        {
            return output.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("daemon", StringComparison.OrdinalIgnoreCase);
        }

        private static (string InPorts, string OutPorts) ParsePorts(string rawPorts)
        {
            if (string.IsNullOrWhiteSpace(rawPorts)) return (string.Empty, string.Empty);

            // Phân tách chuỗi dạng: "0.0.0.0:8080->80/tcp, :::8080->80/tcp"
            // Hoặc dạng: "80/tcp"
            var outPorts = new List<string>();
            var inPorts = new List<string>();

            var items = rawPorts.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var item in items)
            {
                if (item.Contains("->"))
                {
                    var mainParts = item.Split("->");
                    var hostPart = mainParts[0].Split(':').LastOrDefault(); // Lấy 8080
                    var containerPart = mainParts[1].Split('/').FirstOrDefault(); // Lấy 80

                    if (!string.IsNullOrEmpty(hostPart) && !outPorts.Contains(hostPart)) outPorts.Add(hostPart);
                    if (!string.IsNullOrEmpty(containerPart) && !inPorts.Contains(containerPart)) inPorts.Add(containerPart);
                }
                else
                {
                    var containerPart = item.Split('/').FirstOrDefault();
                    if (!string.IsNullOrEmpty(containerPart) && !inPorts.Contains(containerPart)) inPorts.Add(containerPart);
                }
            }

            return (string.Join(",", inPorts), string.Join(",", outPorts));
        }

        private static DockerContainer ParseJsonToModel(string jsonString)
        {
            using JsonDocument doc = JsonDocument.Parse(jsonString);
            JsonElement root = doc.RootElement;

            string rawPorts = root.GetProperty("Ports").GetString() ?? "";
            var (inPort, outPort) = ParsePorts(rawPorts.Replace("->", "->"));

            var container = new DockerContainer
            {
                Id = root.GetProperty("Id").GetString()?.Replace("sha256:", "").Substring(0, 12) ?? "",
                Name = root.GetProperty("Name").GetString()?.TrimStart('/') ?? "",
                Image = root.GetProperty("Image").GetString() ?? "",
                Status = root.GetProperty("Status").GetString()?.ToLower() ?? "",
                IsRunning = root.GetProperty("Running").GetBoolean(),
                Ports = rawPorts,
                InPorts = inPort,
                OutPorts = outPort
            };

            if (root.TryGetProperty("Labels", out JsonElement labelsElement) && labelsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in labelsElement.EnumerateObject())
                {
                    container.Labels[prop.Name] = prop.Value.GetString() ?? "";
                }
            }

            return container;
        }
        #endregion
    }
}
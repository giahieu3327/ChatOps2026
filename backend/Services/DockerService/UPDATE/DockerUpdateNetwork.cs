using ChatOps.Services.SystemService;

namespace ChatOps.Services.DockerService.Update
{
    /// <summary>
    /// Lớp chuyên trách cấu hình, kết nối (Connect) các vùng mạng Docker Network cho Container
    /// </summary>
    public static class DockerUpdateNetwork
    {
        /// <summary>
        /// Kết nối một Container vào một Docker Network cụ thể kèm theo các biệt danh định danh (Aliases) tùy chọn (Bất đồng bộ)
        /// </summary>
        /// <param name="networkName">Tên vùng mạng muốn kết nối (Ví dụ: ChatOps-net)</param>
        /// <param name="containerName">Tên hoặc ID của Container</param>
        /// <param name="aliases">Danh sách các biệt danh định danh trong mạng (Tùy chọn, có thể truyền bao nhiêu tùy ý)</param>
        /// <returns>Nhật ký kết quả trả về từ Docker Engine (Gồm cả thông báo lỗi nếu có)</returns>
        public static async Task<string> ConnectNetworkAsync(string networkName, string containerName, params string[] aliases)
        {
            if (string.IsNullOrWhiteSpace(networkName) || string.IsNullOrWhiteSpace(containerName))
            {
                return "❌ Thiếu thông tin tên Network hoặc tên Container.";
            }

            networkName = networkName.Trim();
            containerName = containerName.Trim();

            // Khởi tạo tiền tố lệnh kết nối
            string cmd = "docker network connect ";

            // Duyệt qua mảng aliases được truyền vào để build cờ --alias tự động
            if (aliases != null && aliases.Length > 0)
            {
                foreach (var alias in aliases)
                {
                    if (!string.IsNullOrWhiteSpace(alias))
                    {
                        cmd += $"--alias {alias.Trim()} ";
                    }
                }
            }

            // Hoàn thiện câu lệnh nối mạng và gom luồng lỗi 2>&1
            cmd += $"{networkName} {containerName} 2>&1";

            // Thực thi ngầm chặn treo luồng ChatOps Bot
            return await Task.Run(async () => await SystemCommandService.RunAsync(cmd));
        }
    }
}
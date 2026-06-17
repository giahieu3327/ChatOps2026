using ChatOps.Services.SystemService;

namespace ChatOps.Services.DockerService.Create
{
    /// <summary>
    /// Lớp chuyên trách khởi tạo và quản lý Docker Network cho hệ thống ChatOps Cluster
    /// </summary>
    public static class DockerCreateNetwork
    {
        /// <summary>
        /// Kiểm tra và tự động khởi tạo Docker Network nếu chưa tồn tại (Bất đồng bộ)
        /// </summary>
        /// <param name="networkName">Tên vùng mạng cần khởi tạo (Mặc định: ChatOps-net)</param>
        /// <returns>Chuỗi rỗng nếu thành công hoặc đã tồn tại, chuỗi thông báo lỗi nếu thất bại</returns>
        public static async Task<string> InitializeDockerNetworkAsync(string networkName = "ChatOps-net")
        {
            if (string.IsNullOrWhiteSpace(networkName))
            {
                return "❌ Tên network không được để trống.";
            }

            networkName = networkName.Trim();

            // 1. Kiểm tra xem Network đã tồn tại trên Host chưa
            string networkCheck = await Task.Run(async () => 
                await SystemCommandService.RunAsync($"docker network ls --filter name=^{networkName}$ --format \"{{{{.Name}}}}\"")
            );

            // SỬA TẠI ĐÂY: Loại bỏ toàn bộ ký tự xuống dòng (\n, \r) và khoảng trắng dư thừa
            networkCheck = networkCheck?.Replace("\r", "").Replace("\n", "").Trim() ?? string.Empty;

            // 2. Nếu chưa tồn tại thì tiến hành tạo mới
            if (networkCheck != networkName)
            {
                string createResult = await Task.Run(async () => 
                    await SystemCommandService.RunAsync($"docker network create {networkName}")
                );

                // Kiểm tra mã lỗi trả về từ Docker Daemon
                if (createResult.Contains("error", StringComparison.OrdinalIgnoreCase) || 
                    createResult.Contains("failed", StringComparison.OrdinalIgnoreCase))
                {
                    // FALLBACK: Nếu vì lý do bất đồng bộ nào đó mạng đã tồn tại, ta vẫn chấp nhận là thành công
                    if (createResult.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                    {
                        return string.Empty;
                    }

                    return $"❌ Không tạo được Docker Network '{networkName}':\n{createResult}";
                }
            }

            return string.Empty; // Trả về rỗng đồng nghĩa với việc Network đã sẵn sàng hoạt động
        }
    }
}
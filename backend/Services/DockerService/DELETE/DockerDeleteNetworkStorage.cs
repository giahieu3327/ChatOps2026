using ChatOps.Services.SystemService;

namespace ChatOps.Services.DockerService.Delete
{
    /// <summary>
    /// Lớp chuyên trách gỡ bỏ và dọn dẹp tài nguyên Network, Volume của Docker trên Host
    /// </summary>
    public static class DockerDeleteNetworkStorage
    {
        /// <summary>
        /// Xóa một hoặc nhiều Docker Volume dựa trên danh sách tên phân tách bằng dấu cách (Bất đồng bộ)
        /// </summary>
        public static async Task<string> RemoveVolumesAsync(string volumesSpaceSeparated)
        {
            if (string.IsNullOrWhiteSpace(volumesSpaceSeparated))
            {
                return "❌ Danh sách tên Volume không được để trống.";
            }

            // Thêm cờ -f (force) nếu Docker Engine phiên bản mới có hỗ trợ, hoặc giữ nguyên rm
            return await Task.Run(async () => await SystemCommandService.RunAsync($"docker volume rm {volumesSpaceSeparated.Trim()}"));
        }

        /// <summary>
        /// Ngắt kết nối (Disconnect) một Container ra khỏi một vùng mạng Docker Network (Bất đồng bộ)
        /// </summary>
        public static async Task<string> DisconnectNetworkAsync(string networkName, string containerName)
        {
            if (string.IsNullOrWhiteSpace(networkName) || string.IsNullOrWhiteSpace(containerName))
            {
                return "❌ Thiếu thông tin tên Network hoặc tên Container.";
            }

            // Gom luồng lỗi 2>&1 để lấy trọn log nếu container vốn dĩ đã không nằm trong mạng đó
            return await Task.Run(async () => 
                await SystemCommandService.RunAsync($"docker network disconnect {networkName.Trim()} {containerName.Trim()} 2>&1")
            );
        }

        /// <summary>
        /// Xóa trực tiếp một Docker Network cụ thể trên hệ thống (Bất đồng bộ)
        /// </summary>
        public static async Task<string> RemoveNetworkAsync(string networkName)
        {
            if (string.IsNullOrWhiteSpace(networkName))
            {
                return "❌ Tên network cần xóa không được để trống.";
            }

            return await Task.Run(async () => 
                await SystemCommandService.RunAsync($"docker network rm {networkName.Trim()} 2>&1")
            );
        }

        /// <summary>
        /// Dọn dẹp hệ thống, xóa tất cả các Docker Network không có container nào sử dụng (Bất đồng bộ)
        /// </summary>
        public static async Task<string> PruneNetworksAsync()
        {
            // Cờ -f để tự động xác nhận bypass prompt của terminal
            return await Task.Run(async () => await SystemCommandService.RunAsync("docker network prune -f"));
        }
    }
}
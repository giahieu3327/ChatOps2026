using ChatOps.Services.SystemService;

namespace ChatOps.Services.DockerService.Delete
{
    /// <summary>
    /// Lớp chuyên trách gỡ bỏ (Delete/Remove) các Container và Cluster trong hệ thống ChatOps
    /// </summary>
    public static class DockerDeleteContainer
    {
        /// <summary>
        /// Xóa cưỡng chế (Force Remove) một container đơn lẻ bằng Name hoặc ID (Bất đồng bộ)
        /// </summary>
        /// <param name="nameOrId">Tên hoặc ID của Container cần xóa</param>
        /// <returns>Log kết quả trả về từ Docker hoặc chuỗi thông báo lỗi</returns>
        public static async Task<string> RemoveContainerForceAsync(string nameOrId)
        {
            // 1. Kiểm tra tính hợp lệ của tham số đầu vào
            if (string.IsNullOrWhiteSpace(nameOrId))
            {
                return "❌ LỖI: Tên hoặc ID Container không được để trống.";
            }

            string trimmedName = nameOrId.Trim();

            // 2. Thực thi lệnh shell bất đồng bộ (Thêm 2>&1 để gom cả luồng lỗi về chung một mối)
            string result = await Task.Run(async () => 
                await SystemCommandService.RunAsync($"docker rm -f {trimmedName} 2>&1")
            );

            // 3. Phân tích kết quả trả về từ Docker Engine
            // Nếu thành công, Docker sẽ trả về chính xác tên container (có thể kèm ký tự xuống dòng \n)
            if (!string.IsNullOrWhiteSpace(result) && result.Trim().Equals(trimmedName, StringComparison.OrdinalIgnoreCase))
            {
                return "SUCCESS"; // Trả về mã định danh thành công rõ ràng
            }

            // Trường hợp đặc biệt: Nếu container không tồn tại, lệnh docker rm -f vẫn có thể coi là "thành công" 
            // vì mục đích cuối cùng của ta là làm sạch môi trường, không để tên đó tồn tại nữa.
            if (result.Contains("No such container", StringComparison.OrdinalIgnoreCase))
            {
                return "SUCCESS"; 
            }

            // Nếu dính các lỗi hệ thống khác (ví dụ: lỗi driver storage, container đang bị lock cứng,...)
            return $"❌ DOCKER_ERROR: {result.Trim()}";
        }

        /// <summary>
        /// Xóa sạch toàn bộ một cụm Cluster (App + Load Balancer) dựa trên thư mục Runtime của App đó
        /// </summary>
        /// <param name="appName">Tên ứng dụng muốn gỡ bỏ hoàn toàn (Ví dụ: shop_trial)</param>
        /// <returns>Nhật ký log quá trình hạ và xóa thư mục</returns>
        public static async Task<string> RemoveClusterAsync(string appName)
        {
            if (string.IsNullOrWhiteSpace(appName))
            {
                return "❌ Tên ứng dụng để xóa không được để trống.";
            }

            appName = appName.Trim();
            string runtimeDir = $"/home/ubuntu/ChatOps/docker/Apps/{appName}";

            if (!Directory.Exists(runtimeDir))
            {
                return $"❌ Không tìm thấy thư mục runtime của ứng dụng '{appName}' trên host.";
            }

            string logs = $"🧹 **BẮT ĐẦU GỠ BỎ CLUSTER: {appName.ToUpper()}**\n\n";

            // 1. Hạ cụm Load Balancer trước
            string lbCompose = Path.Combine(runtimeDir, "docker-compose-lb.yml");
            if (File.Exists(lbCompose))
            {
                logs += "🛑 Đang dập cụm Load Balancer...\n";
                string downLbRes = await Task.Run(async () => await SystemCommandService.RunAsync($"cd {runtimeDir} && docker compose -f docker-compose-lb.yml down -v 2>&1"));
                logs += $"{downLbRes}\n";
            }

            // 2. Hạ cụm App (Thử cả 2 file cấu hình Git và Registry xem cái nào tồn tại thì dập cái đó)
            string gitCompose = Path.Combine(runtimeDir, "docker-git.yml");
            string registryCompose = Path.Combine(runtimeDir, "docker-registry.yml");
            string targetComposeFile = File.Exists(gitCompose) ? "docker-git.yml" : (File.Exists(registryCompose) ? "docker-registry.yml" : "");

            if (!string.IsNullOrEmpty(targetComposeFile))
            {
                logs += $"🛑 Đang dập cụm dịch vụ core ({targetComposeFile})...\n";
                string downAppRes = await Task.Run(async () => await SystemCommandService.RunAsync($"cd {runtimeDir} && docker compose -f {targetComposeFile} down -v 2>&1"));
                logs += $"{downAppRes}\n";
            }

            // 3. Xóa sạch thư mục cấu hình runtime vật lý để giải phóng ổ cứng
            try
            {
                logs += "📁 Đang dọn dẹp tệp tin cấu hình và log tại thư mục Apps...\n";
                await Task.Run(() => Directory.Delete(runtimeDir, true));
                logs += "✨ Đã xóa sạch thư mục runtime vật lý.\n";
            }
            catch (Exception ex)
            {
                logs += $"⚠️ Cảnh báo: Không thể xóa thư mục vật lý tự động do dính quyền: {ex.Message}\n";
                // Chữa cháy bằng lệnh hệ thống nếu Directory.Delete bị lỗi Node/Nginx lock file
                await Task.Run(async () => await SystemCommandService.RunAsync($"rm -rf {runtimeDir}"));
            }

            return logs + $"\n✅ Gỡ bỏ cụm ứng dụng `{appName}` thành công!";
        }
    }
}
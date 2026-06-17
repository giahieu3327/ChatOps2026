using ChatOps.Services.SystemService;

namespace ChatOps.Services.DockerService.Update
{
    /// <summary>
    /// Lớp chuyên trách điều khiển vòng đời hoạt động (Lifecycle) của các Docker Container
    /// </summary>
    public static class DockerUpdateLifecycle
    {
        /// <summary>
        /// Khởi động một hoặc nhiều Container đang tắt (Bất đồng bộ)
        /// </summary>
        public static async Task<string> StartContainerAsync(string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId)) return "❌ Tên hoặc ID Container không được để trống.";
            
            return await Task.Run(async () => 
                await SystemCommandService.RunAsync($"docker start {nameOrId.Trim()} 2>&1")
            );
        }

        /// <summary>
        /// Tắt an toàn (Graceful Stop) một hoặc nhiều Container (Bất đồng bộ)
        /// </summary>
        public static async Task<string> StopContainerAsync(string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId)) return "❌ Tên hoặc ID Container không được để trống.";

            return await Task.Run(async () => 
                await SystemCommandService.RunAsync($"docker stop {nameOrId.Trim()} 2>&1")
            );
        }

        /// <summary>
        /// Tắt cưỡng chế ngay lập tức (Force Kill) một Container bằng SIGKILL (Bất đồng bộ)
        /// </summary>
        public static async Task<string> KillContainerAsync(string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId)) return "❌ Tên hoặc ID Container không được để trống.";

            return await Task.Run(async () => 
                await SystemCommandService.RunAsync($"docker kill {nameOrId.Trim()} 2>&1")
            );
        }

        /// <summary>
        /// Khởi động lại (Restart) một hoặc nhiều Container (Bất đồng bộ)
        /// </summary>
        public static async Task<string> RestartContainerAsync(string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId)) return "❌ Tên hoặc ID Container không được để trống.";

            return await Task.Run(async () => 
                await SystemCommandService.RunAsync($"docker restart {nameOrId.Trim()} 2>&1")
            );
        }

        /// <summary>
        /// Đổi tên (Rename) một Docker Container đang tồn tại trên Host (Bất đồng bộ)
        /// </summary>
        public static async Task<string> RenameContainerAsync(string oldName, string newName)
        {
            // 1. Kiểm tra tính hợp lệ của tham số đầu vào
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
            {
                return "❌ LỖI: Vui lòng cung cấp đầy đủ tên cũ và tên mới cho Container.";
            }

            // 2. Thực thi lệnh shell bất đồng bộ
            string result = await Task.Run(async () => 
                await SystemCommandService.RunAsync($"docker rename {oldName.Trim()} {newName.Trim()} 2>&1")
            );

            // 3. Phân tích kết quả trả về từ Docker Engine
            // Bản chất: 'docker rename' nếu THÀNH CÔNG sẽ trả về chuỗi rỗng (chuỗi có độ dài bằng 0)
            if (string.IsNullOrWhiteSpace(result))
            {
                return "SUCCESS"; // Trả về mã định danh thành công rõ ràng
            }

            // Nếu có phản hồi chữ nghĩa từ Docker Engine (tức là đã dính lỗi hệ thống)
            return $"❌ DOCKER_ERROR: {result.Trim()}";
        }
    }
}
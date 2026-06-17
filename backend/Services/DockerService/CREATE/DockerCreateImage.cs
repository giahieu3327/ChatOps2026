using ChatOps.Services.SystemService;

namespace ChatOps.Services.DockerService.Create
{
    /// <summary>
    /// Lớp chuyên trách quản lý, tải (Pull) và đóng gói (Build) Docker Image cho hệ thống ChatOps
    /// </summary>
    public static class DockerCreateImage
    {
        /// <summary>
        /// Kéo (Pull) một Docker Image từ Docker Hub hoặc Private Registry về Host (Bất đồng bộ)
        /// </summary>
        /// <param name="dockerImage">Tên đầy đủ của Image kèm tag (Ví dụ: giahieu33271/shop-web:v1.0)</param>
        /// <returns>Chuỗi rỗng nếu thành công, chuỗi thông báo lỗi nếu thất bại</returns>
        public static async Task<string?> PullDockerImageAsync(string dockerImage)
        {
            if (string.IsNullOrWhiteSpace(dockerImage))
            {
                return "❌ Tên Docker Image không được để trống.";
            }

            try
            {
                // 1. Kiểm tra xem Image đã tồn tại ở local chưa
                string inspectResult = await Task.Run(async () => 
                    await SystemCommandService.RunAsync($"docker image inspect {dockerImage}"));

                if (!string.IsNullOrWhiteSpace(inspectResult) && 
                    !inspectResult.Contains("Error", StringComparison.OrdinalIgnoreCase))
                {
                    return null; // Image đã có sẵn, không cần làm gì thêm
                }

                // 2. Tiến hành pull với cơ chế tự động retry 3 lần
                int maxRetries = 3;
                int delayMilliseconds = 3000; // Chờ 3 giây trước khi thử lại
                string pullResult = string.Empty;

                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    pullResult = await Task.Run(async () => 
                        await SystemCommandService.RunAsync($"docker pull {dockerImage}"));

                    // Kiểm tra xem kết quả có chứa lỗi không
                    bool hasError = pullResult.Contains("error", StringComparison.OrdinalIgnoreCase) || 
                                    pullResult.Contains("failed", StringComparison.OrdinalIgnoreCase);

                    if (!hasError)
                    {
                        return null; // Pull thành công, thoát hàm
                    }

                    // Nếu bị lỗi và vẫn còn lượt retry, đợi một chút rồi thử lại
                    if (attempt < maxRetries)
                    {
                        // Bạn có thể log dòng này ra console để dễ theo dõi quá trình của Bot
                        Console.WriteLine($"⚠️ Pull thất bại (Lần {attempt}/{maxRetries}) do lỗi mạng/handshake. Thử lại sau 3s...");
                        await Task.Delay(delayMilliseconds);
                    }
                }

                // Nếu đã qua 3 lần thử mà vẫn thất bại thì trả về lỗi cuối cùng
                return $"❌ Tải Image thất bại sau {maxRetries} lần thử:\n{pullResult}";
            }
            catch (Exception ex)
            {
                return $"❌ Có lỗi hệ thống xảy ra: {ex.Message}";
            }
        }

        /// <summary>
        /// Hàm Build Docker Image độc nhất (Dùng cho cả build thư mục đơn khối lẫn submodule component của Release)
        /// </summary>
        /// <param name="imageName">Tên Image đích muốn đặt sau khi build (Ví dụ: giahieu33271/shop-backend:v1.0)</param>
        /// <param name="buildContextPath">Đường dẫn tuyệt đối đến thư mục chứa file Dockerfile cần build</param>
        /// <returns>Toàn bộ log sinh ra từ quá trình build (Bao gồm cả lỗi nếu có)</returns>
        public static async Task<string> BuildImageAsync(string imageName, string buildContextPath)
        {
            if (string.IsNullOrWhiteSpace(imageName) || string.IsNullOrWhiteSpace(buildContextPath))
            {
                return "❌ Thiếu thông tin tên Image hoặc đường dẫn Build Context.";
            }

            if (!Directory.Exists(buildContextPath))
            {
                return $"❌ Thư mục build context không tồn tại: {buildContextPath}";
            }

            // Gộp luồng lỗi 2>&1 để ChatOps bắt trọn log compile (lỗi nuget, npm, build code...)
            return await Task.Run(async () => await SystemCommandService.RunAsync($"docker build -t {imageName} {buildContextPath} 2>&1"));
        }
    }
}
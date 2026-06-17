using ChatOps.Services.SystemService;

namespace ChatOps.Services.DockerService.Update
{
    /// <summary>
    /// Lớp chuyên trách đẩy (Push) các Docker Image hoàn chỉnh lên Docker Hub hoặc Private Registry
    /// </summary>
    public static class DockerUpdateRegistry
    {
        /// <summary>
        /// Đẩy (Push) một Docker Image từ Local Host lên Registry một cách bất đồng bộ
        /// </summary>
        /// <param name="imageName">Tên đầy đủ của Image kèm tag cần push (Ví dụ: giahieu33271/shop-backend:v1.0)</param>
        /// <returns>Nhật ký log chi tiết quá trình đẩy từng layer của Image hoặc thông báo lỗi</returns>
        public static async Task<string> PushImageAsync(string imageName)
        {
            if (string.IsNullOrWhiteSpace(imageName))
            {
                return "❌ Tên Docker Image để đẩy lên Registry không được để trống.";
            }

            // Chạy lệnh push trên một luồng worker độc lập để giải phóng luồng chính của Bot ChatOps
            return await Task.Run(async () => 
                await SystemCommandService.RunAsync($"docker push {imageName.Trim()} 2>&1")
            );
        }
    }
}
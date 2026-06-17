using ChatOps.Services.SystemService;

namespace ChatOps.Services.DockerService.Delete
{
    /// <summary>
    /// Lớp chuyên trách gỡ bỏ (Delete) và dọn dẹp các Docker Image trên hệ thống Host
    /// </summary>
    public static class DockerDeleteImage
    {
        /// <summary>
        /// Xóa cưỡng chế một Docker Image bằng Tên hoặc ID (Bất đồng bộ)
        /// </summary>
        /// <param name="imageOrId">Tên Image kèm tag hoặc ID của Image (Ví dụ: giahieu33271/shop-web:v1.0)</param>
        /// <returns>Nhật ký log kết quả trả về từ Docker Engine</returns>
        public static async Task<string> RemoveImageAsync(string imageOrId)
        {
            if (string.IsNullOrWhiteSpace(imageOrId))
            {
                return "❌ Tên hoặc ID Docker Image không được để trống.";
            }

            // Sử dụng rmi -f để buộc xóa image kể cả khi có container cũ đang ngậm image đó
            return await Task.Run(async () => await SystemCommandService.RunAsync($"docker rmi -f {imageOrId.Trim()}"));
        }

        /// <summary>
        /// Dọn dẹp triệt để hệ thống, xóa toàn bộ các Dangling Images và Unused Images (Bất đồng bộ)
        /// </summary>
        /// <returns>Log danh sách các Image đã được giải phóng và dung lượng ổ cứng tiết kiệm được</returns>
        public static async Task<string> PruneImagesAsync()
        {
            // Lệnh prune -af giải phóng rất nhiều dung lượng bộ nhớ vật lý
            return await Task.Run(async () => await SystemCommandService.RunAsync("docker image prune -af"));
        }
    }
}
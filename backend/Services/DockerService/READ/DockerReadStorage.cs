using System.Text.Json;
using ChatOps.Models;
using ChatOps.Services.SystemService;

namespace ChatOps.Services.DockerService.Read
{
    /// <summary>
    /// Lớp chuyên trách truy vấn chuyên sâu về trạng thái lưu trữ (Volumes, Bind Mounts) của Container trên Host
    /// </summary>
    public static class DockerReadStorage
    {
        /// <summary>
        /// Truy vấn TOÀN BỘ danh sách thư mục gắn kết (Mounts/Volumes) của một Container (Bất đồng bộ)
        /// </summary>
        public static async Task<ContainerStorageDetails?> GetContainerStorageDetailsAsync(string containerName)
        {
            if (string.IsNullOrWhiteSpace(containerName)) return null;

            containerName = containerName.Trim();

            // Xuất trực tiếp cấu trúc mảng Mounts dưới dạng JSON sạch để parse
            string format = "{\"Mounts\":{{json .Mounts}}}";
            string cmd = $"docker inspect --format '{format}' {containerName} 2>&1";
            string output = await Task.Run(async () => await SystemCommandService.RunAsync(cmd));

            if (output.Contains("error", StringComparison.OrdinalIgnoreCase) || output.Contains("No such", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;

                var details = new ContainerStorageDetails { ContainerName = containerName };

                if (root.TryGetProperty("Mounts", out var mountsElement) && mountsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mountItem in mountsElement.EnumerateArray())
                    {
                        details.Mounts.Add(new StorageMountItem
                        {
                            Type = mountItem.GetProperty("Type").GetString() ?? "volume",
                            Source = mountItem.GetProperty("Source").GetString() ?? "",
                            Destination = mountItem.GetProperty("Destination").GetString() ?? "",
                            ReadOnly = mountItem.TryGetProperty("RW", out var rwElement) && !rwElement.GetBoolean()
                        });
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
        /// Lấy danh sách tên của tất cả các Docker Volume độc lập đang tồn tại trên Host (Bất đồng bộ)
        /// </summary>
        public static async Task<List<string>> GetVolumeListAsync()
        {
            // Cờ -q để chỉ trả về danh sách tên Volume gọn gàng
            string output = await Task.Run(async () => await SystemCommandService.RunAsync("docker volume ls -q"));

            if (string.IsNullOrWhiteSpace(output) || output.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                return new List<string>();
            }

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                         .Select(v => v.Trim())
                         .ToList();
        }
    }
}
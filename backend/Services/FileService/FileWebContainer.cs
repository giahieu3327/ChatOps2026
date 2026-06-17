using ChatOps.Data;
using ChatOps.Models;
using ChatOps.Services.SystemService;

namespace ChatOps.Services.FileService
{
    public static class FileWebContainer
    {
        public static async Task<string> GetHTML(string name)
        {
            try
            {
                DockerContainer? containerDetails = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(name);

                if (containerDetails == null || string.IsNullOrEmpty(containerDetails.Image))
                {
                    return $"ERROR: Không tìm thấy chi tiết thông tin cho container '{name}'";
                }

                string image = containerDetails.Image;

                if (!ImageCategories.ImageServices.TryGetValue(image, out var config))
                {
                    return $"ERROR: Không tìm thấy cấu hình danh mục cho hình ảnh '{image}'";
                }

                string targetDirectory = config.Path;
                if (string.IsNullOrEmpty(targetDirectory))
                {
                    return $"ERROR: Loại dịch vụ '{image}' không hỗ trợ tính năng quản lý giao diện web.";
                }

                string targetPath = Path.Combine(targetDirectory, "index.html").Replace("\\", "/");

                string output = await Task.Run(async () => 
                    await SystemCommandService.RunAsync($"docker exec {name.Trim()} cat {targetPath.Trim()} 2>&1")
                );

                if (string.IsNullOrWhiteSpace(output) || output.Contains("Error:") || output.Contains("No such file"))
                {
                    return $"ERROR: Không thể đọc file từ container. Chi tiết: {output}";
                }

                return output;
            }
            catch (Exception ex)
            {
                return $"ERROR: Hệ thống gặp lỗi khi thực thi thao tác lấy HTML: {ex.Message}";
            }
        }

        public static async Task<string> SetHTML(string name, string content)
        {
            // SỬA: Tạo một thư mục tạm riêng biệt, tên file phải cụ thể là index.html hoặc temp_index.html
            string tempDir = "/home/ubuntu/ChatOps/tmp";
            if (!Directory.Exists(tempDir))
                Directory.CreateDirectory(tempDir);

            // Đường dẫn tệp tạm cụ thể trên máy Host để tránh xung đột thư mục
            string tempFilePath = Path.Combine(tempDir, $"{name.Trim()}_index.html");

            try
            {
                DockerContainer? containerDetails = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(name);

                if (containerDetails == null || string.IsNullOrEmpty(containerDetails.Image))
                {
                    return $"ERROR: Không tìm thấy chi tiết thông tin cho container '{name}'";
                }

                string image = containerDetails.Image;

                if (!ImageCategories.ImageServices.TryGetValue(image, out var config))
                {
                    return $"ERROR: Không tìm thấy cấu hình danh mục cho hình ảnh '{image}'";
                }

                string targetDirectory = config.Path;
                if (string.IsNullOrEmpty(targetDirectory))
                {
                    return $"ERROR: Loại dịch vụ '{image}' không hỗ trợ tính năng quản lý giao diện web.";
                }

                string targetPath = Path.Combine(targetDirectory, "index.html").Replace("\\", "/");

                // Ghi dữ liệu thô đã Unescape vào tệp tạm trên Host
                await File.WriteAllTextAsync(tempFilePath, content);

                // Copy tệp tin tạm từ Host vào đúng vị trí đích trong Container
                string result = await CopyFileToContainerAsync(tempFilePath, name, targetPath);

                if (!string.IsNullOrWhiteSpace(result) && (result.Contains("ERROR") || result.Contains("❌") || result.Contains("Error") || result.Contains("failure")))
                {
                    return $"ERROR: Không thể sao chép file vào container. Chi tiết: {result}";
                }

                // FIX LỖI 403: Thực thi chmod 644 cấp quyền đọc cho Worker Nginx và chown cho đúng user bên trong Container
                await Task.Run(async () =>
                {
                    // Cấp quyền Read cho tất cả User đối với tệp tin vừa đẩy vào
                    await SystemCommandService.RunAsync($"docker exec {name.Trim()} chmod 644 {targetPath} 2>&1");
                    
                    // Thử nghiệm thay đổi owner về mặc định của dịch vụ web nếu image là nginx/linux thô
                    await SystemCommandService.RunAsync($"docker exec {name.Trim()} chown www-data:www-data {targetPath} 2>&1");
                    await SystemCommandService.RunAsync($"docker exec {name.Trim()} chown nginx:nginx {targetPath} 2>&1");
                });

                return $"Thành công: Đã cập nhật nội dung cho dịch vụ {image} (Container: {name})";
            }
            catch (Exception ex)
            {
                return $"ERROR: Hệ thống gặp lỗi khi thực thi thao tác lưu HTML: {ex.Message}";
            }
            finally
            {
                // Giải phóng file tạm trên máy Host để dọn dẹp hệ thống bộ nhớ đệm
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
        }

        public static async Task<string> CopyFileToContainerAsync(string hostPath, string containerName, string containerPath)
        {
            if (string.IsNullOrWhiteSpace(hostPath) || string.IsNullOrWhiteSpace(containerName) || string.IsNullOrWhiteSpace(containerPath))
            {
                return "❌ Thiếu thông tin đường dẫn nguồn, tên container hoặc đường dẫn đích.";
            }

            return await Task.Run(async () => 
                await SystemCommandService.RunAsync($"docker cp {hostPath.Trim()} {containerName.Trim()}:{containerPath.Trim()} 2>&1")
            );
        }
    }
}
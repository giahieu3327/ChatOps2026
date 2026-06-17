using System;
using System.Diagnostics;
using System.Threading.Tasks;
using ChatOps.Services.RedisService;

namespace ChatOps.Services.SystemService
{
    /// <summary>
    /// Lớp Core duy nhất chịu trách nhiệm giao tiếp trực tiếp với Shell (/bin/bash) của hệ điều hành Linux
    /// </summary>
    public static class SystemCommandService
    {
        /// <summary>
        /// Thực thi một câu lệnh Linux thô một cách BẤT ĐỒNG BỘ (Khuyên dùng cho ChatOps)
        /// </summary>
        public static async Task<string> RunAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return string.Empty;

            var escapedArgs = command.Replace("\"", "\\\"");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("/bin/bash", $"-c \"{escapedArgs}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            // Đọc song song cả 2 luồng hoàn toàn bất đồng bộ bằng Task
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync();

            string output = outputTask.Result;
            string error = errorTask.Result;

            return string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";
        }

        /// <summary>
        /// Kiểm tra xem một Cổng (Port) vật lý trên Host VPS đã bị ứng dụng nào chiếm dụng chưa
        /// </summary>
        public static async Task<bool> IsHostPortAvailableAsync(int port)
        {
            // Bước 1: Kiểm tra các tiến trình hoặc socket đang lắng nghe trực tiếp trên OS
            string usedBySystem = await RunAsync($"ss -tuln | grep -w ':{port}'");
            if (!string.IsNullOrWhiteSpace(usedBySystem?.Trim()))
            {
                return false; // Port đã bị chiếm bởi một service chạy nền hoặc container đang Up
            }

            // Bước 2: Kiểm tra tất cả container Docker (bao gồm cả Up, Created, Exited)
            // Câu lệnh này trả về danh sách các Public Port đang được gán cho toàn bộ container
            string dockerPortsRaw = await RunAsync("docker ps -a --format '{{.Ports}}'");
            
            if (!string.IsNullOrWhiteSpace(dockerPortsRaw))
            {
                // Tách chuỗi theo từng dòng tương ứng với thông tin port của từng container
                var lines = dockerPortsRaw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var line in lines)
                {
                    // Định dạng chuỗi ports của docker thường có dạng: "0.0.0.0:8082->80/tcp" hoặc "[::]:8082->80/tcp"
                    // Ta cần bắt chính xác số port nằm sau dấu hai chấm ':' và trước dấu mũi tên '->'
                    string pattern = $":{port}->";
                    
                    if (line.Contains(pattern))
                    {
                        return false; // Port này đã bị đặt chỗ trước bởi một container nào đó
                    }
                }
            }

            return true; // Port hoàn toàn sạch và sẵn sàng sử dụng
        }
        public static async Task<string> CheckDuplicatesAsync(string? containerName, string? fullDomain)
        {
            try
            {
                if(!string.IsNullOrWhiteSpace(containerName))
                {
                    var containerCheck = await RedisContainerService.GetContainerAsync(containerName: containerName);
                    if (containerCheck.success && containerCheck.nodes != null && containerCheck.nodes.Count > 0)
                    {
                        return $"❌ Tên Container '{containerName}' đã tồn tại hoặc đã được đăng ký trong cụm.";
                    }
                }
                if(!string.IsNullOrWhiteSpace(fullDomain))
                {
                    var domainCheck = await RedisDomainService.GetDomainAsync(domain: fullDomain);
                    if (domainCheck.success && domainCheck.nodes != null && domainCheck.nodes.Count > 0)
                    {
                        return $"❌ Tên miền (Domain) '{fullDomain}' đã tồn tại và đang được sử dụng trên hệ thống.";
                    }
                }
            }
            catch (Exception ex)
            {
                return $"❌ Lỗi xảy ra trong quá trình kiểm tra trùng lặp hạ tầng: {ex.Message}";
            }

            // Trả về chuỗi rỗng nếu không phát hiện bất kỳ sự trùng lặp nào
            return string.Empty;
        }
    }
}
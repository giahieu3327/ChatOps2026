using ChatOps.Services.SystemService;

namespace ChatOps.Services.DockerService.Read
{
    /// <summary>
    /// Lớp chuyên trách truy vấn Nhật ký (Logs) và Chỉ số hiệu năng (Stats/Metrics) của Container
    /// </summary>
    public static class DockerReadLogsStats
    {
        /// <summary>
        /// Lấy nhật ký hoạt động (Logs) của Container với số dòng giới hạn tùy chọn (Bất đồng bộ)
        /// </summary>
        /// <param name="containerName">Tên hoặc ID của Container</param>
        /// <param name="limitLines">Số dòng log gần nhất muốn lấy (Mặc định = 100 dòng để tránh tràn RAM)</param>
        public static async Task<string> GetContainerLogsAsync(string containerName, int limitLines = 100)
        {
            if (string.IsNullOrWhiteSpace(containerName)) return "❌ Tên container không được để trống.";

            // Sử dụng --tail để giới hạn dữ liệu tải về, gom luồng lỗi 2>&1
            string cmd = $"docker logs {containerName.Trim()} --tail {limitLines} 2>&1";
            
            return await Task.Run(async () => {
                try
                {
                    return await SystemCommandService.RunAsync(cmd);
                }
                catch (Exception ex)
                {
                    return $"❌ Lỗi khi đọc log hệ thống: {ex.Message}";
                }
            });
        }

        /// <summary>
        /// Lấy phần trăm sử dụng CPU hiện tại của một Container (Bất đồng bộ)
        /// </summary>
        public static async Task<double> GetContainerCpuAsync(string containerName)
        {
            if (string.IsNullOrWhiteSpace(containerName)) return 0;

            // docker stats --no-stream tốn khoảng 1-2s xử lý, bắt buộc phải chạy ngầm bằng Task.Run
            string cmd = $"docker stats {containerName.Trim()} --no-stream --format \"{{{{.CPUPerc}}}}\"";

            return await Task.Run(async () => {
                try
                {
                    string cpuRaw = await SystemCommandService.RunAsync(cmd);
                    string cpu = cpuRaw.Replace("%", "");
                    if (double.TryParse(cpu, out double cpuPercentage))
                    {
                        return cpuPercentage;
                    }
                }
                catch { /* Nuốt lỗi để tránh crash luồng quét nền */ }
                return 0;
            });
        }

        /// <summary>
        /// Tính toán số lượng Request mới (Delta) được gửi tới cụm thông qua Nginx Load Balancer (Bất đồng bộ)
        /// </summary>
        /// <param name="app">Tên ứng dụng (Ví dụ: shop_trial)</param>
        /// <param name="lbContainerName">Tên container Nginx Load Balancer đang chạy (Ví dụ: shop_trial-lb-1)</param>
        /// <param name="lastReqCountCache">Từ điển Cache lưu trữ số lượng request của chu kỳ trước nhằm tính toán Delta</param>
        public static async Task<long> GetRequestCountDeltaAsync(string app, string lbContainerName, Dictionary<string, long> lastReqCountCache)
        {
            if (string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(lbContainerName)) return 0;

            app = app.Trim();
            lbContainerName = lbContainerName.Trim();

            // Đổi từ /tmp/access.log thành /runtime/access.log cho khớp với file cấu hình sinh tự động ở câu 1
            string cmd = $"docker exec {lbContainerName} wc -l /runtime/access.log 2>&1";

            return await Task.Run(async () => {
                string reqRaw = await SystemCommandService.RunAsync(cmd);
                long currentTotalLines = 0;

                if (!string.IsNullOrWhiteSpace(reqRaw) && !reqRaw.Contains("error", StringComparison.OrdinalIgnoreCase))
                {
                    // Tách chuỗi lấy con số đầu tiên (Ví dụ từ log: "1542 /runtime/access.log" -> lấy "1542")
                    string firstPart = reqRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "0";
                    long.TryParse(firstPart, out currentTotalLines);
                }

                // Nếu app này chưa từng được lưu trong cache, khởi tạo giá trị gốc bằng số dòng hiện tại
                if (!lastReqCountCache.ContainsKey(app))
                {
                    lastReqCountCache[app] = currentTotalLines;
                }

                long previousTotalLines = lastReqCountCache[app];
                long deltaRequests = currentTotalLines - previousTotalLines;

                // Xử lý các tình huống đặc biệt (Ví dụ: log của Nginx bị truncate, logrotate xóa đi ghi lại từ đầu)
                if (deltaRequests < 0) 
                {
                    deltaRequests = currentTotalLines;
                }

                // Cập nhật lại trạng thái cache cho chu kỳ tiếp theo
                lastReqCountCache[app] = currentTotalLines;
                return deltaRequests;
            });
        }
    }
}
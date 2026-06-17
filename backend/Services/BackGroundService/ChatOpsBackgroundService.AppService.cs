using ChatOps.Services.RedisService;

namespace ChatOps.Services.BackGroundService
{
    public partial class ChatOpsBackgroundService
    {
        private static readonly TimeSpan AppHeartbeatInterval = TimeSpan.FromSeconds(30);
        private async Task ProcessAppServiceHeartbeat(CancellationToken stoppingToken)
        {
            Console.WriteLine($"🚀 [App Monitor] Khởi động vòng lặp Kiểm tra & Đồng bộ cấu hình 2 chiều (Chu kỳ: {AppHeartbeatInterval.TotalSeconds} giây)");

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RedisAppService.SyncAppServicesAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [App Monitor Error] Xảy ra lỗi trong chu kỳ đối chiếu cấu hình: {ex.Message}");
                }

                await Task.Delay(AppHeartbeatInterval, stoppingToken);
            }
        }
        public async Task ShutdownAppServiceCluster()
        {
            Console.WriteLine("🔒 [Shutdown-Job] Node đang dừng. Bảo lưu nguyên vẹn toàn bộ dữ liệu cấu hình App Services trên hệ thống Redis tập trung.");
            await Task.CompletedTask;
        }
    }
}
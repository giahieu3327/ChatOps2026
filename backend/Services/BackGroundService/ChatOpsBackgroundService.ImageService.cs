using ChatOps.Services.RedisService;

namespace ChatOps.Services.BackGroundService
{
    public partial class ChatOpsBackgroundService
    {
        private static readonly TimeSpan ImageHeartbeatInterval = TimeSpan.FromSeconds(30);

        private async Task ProcessImageServiceHeartbeat(CancellationToken stoppingToken)
        {
            Console.WriteLine($"🚀 [Image Monitor] Khởi động vòng lặp Đồng bộ cấu hình từ Redis về Local (Chu kỳ: {ImageHeartbeatInterval.TotalSeconds} giây)");

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RedisImageService.SyncImageServicesAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [Image Monitor Error] Xảy ra lỗi trong chu kỳ đối chiếu cấu hình: {ex.Message}");
                }

                await Task.Delay(ImageHeartbeatInterval, stoppingToken);
            }
        }
        public async Task ShutdownImageServiceCluster()
        {
            Console.WriteLine("🔒 [Shutdown-Job] Node đang dừng. Bảo lưu nguyên vẹn toàn bộ dữ liệu cấu hình Image Services trên hệ thống Redis tập trung.");
            await Task.CompletedTask;
        }
    }
}
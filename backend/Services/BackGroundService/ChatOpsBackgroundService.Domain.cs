using ChatOps.Services.RedisService;

namespace ChatOps.Services.BackGroundService
{
    public partial class ChatOpsBackgroundService
    {
        private static readonly TimeSpan DomainHeartbeatInterval = TimeSpan.FromSeconds(30);

        private async Task ProcessServicesHeartbeat(CancellationToken stoppingToken)
        {
            Console.WriteLine($"🚀 [Service Monitor] Khởi động vòng lặp Quản lý & Duy trì Services (Chu kỳ: {DomainHeartbeatInterval.TotalSeconds} giây)");

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RedisDomainService.SyncServicesAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [Service Monitor Error] Lỗi trong chu kỳ quét/gia hạn dịch vụ: {ex.Message}");
                }

                await Task.Delay(DomainHeartbeatInterval, stoppingToken);
            }
        }
        public async Task ShutdownServicesCluster()
        {
            Console.WriteLine("🔒 [Shutdown-Job] Hệ thống đang tắt...");
            await RedisDomainService.ShutdownServicesClusterAsync();
            await Task.CompletedTask;
        }
    }
}
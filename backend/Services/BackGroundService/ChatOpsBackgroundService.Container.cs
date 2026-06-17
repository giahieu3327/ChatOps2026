using ChatOps.Services.RedisService;

namespace ChatOps.Services.BackGroundService
{
    public partial class ChatOpsBackgroundService
    {
        private static readonly TimeSpan ContainerHeartbeatInterval = TimeSpan.FromSeconds(30);

        private async Task ProcessContainerHeartbeat(CancellationToken stoppingToken)
        {
            Console.WriteLine($"🚀 [Container Monitor] Khởi động vòng lặp Quản lý & Duy trì Containers (Chu kỳ: {ContainerHeartbeatInterval.TotalSeconds} giây)");

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RedisContainerService.SyncContainersAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [Container Monitor Error] Lỗi xảy ra trong chu kỳ quét và đồng bộ dữ liệu Container: {ex.Message}");
                }

                await Task.Delay(ContainerHeartbeatInterval, stoppingToken);
            }
        }
   
        public async Task ShutdownContainersCluster()
        {
            Console.WriteLine("🔒 [Shutdown-Job] Ứng dụng đang dừng...");
            await RedisContainerService.ShutdownContainersClusterAsync();
            await Task.CompletedTask;
        }
    }
}
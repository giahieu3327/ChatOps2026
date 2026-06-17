using ChatOps.Services.RedisService;

namespace ChatOps.Services.BackGroundService
{
    public partial class ChatOpsBackgroundService
    {
        private static readonly TimeSpan NodeHeartbeatInterval = TimeSpan.FromSeconds(30);

        private async Task ProcessNodeHeartbeat(CancellationToken stoppingToken)
        {
            Console.WriteLine($"🚀 [Cluster Monitor] Khởi động vòng lặp quản lý Node (Chu kỳ: {NodeHeartbeatInterval.TotalSeconds} giây)");

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RedisNodeService.SyncNodeHeartbeatAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [Heartbeat Error] Lỗi xảy ra trong chu kỳ quản lý Cluster Node: {ex.Message}");
                }

                await Task.Delay(NodeHeartbeatInterval, stoppingToken);
            }
        }

        public async Task ShutdownNodeCluster()
        {
            Console.WriteLine("🔒 [Shutdown-Job] Ứng dụng đang dừng...");
            await RedisNodeService.ShutdownNodeClusterAsync();
            await Task.CompletedTask;
        }
    }
}
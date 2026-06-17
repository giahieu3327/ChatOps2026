using ChatOps.Services.RedisService;

namespace ChatOps.Services.BackGroundService
{
    public partial class ChatOpsBackgroundService
    {
        private static readonly TimeSpan InstanceHeartbeatInterval = TimeSpan.FromSeconds(30);

        private async Task ProcessInstanceHeartbeat(CancellationToken stoppingToken)
        {
            Console.WriteLine($"🚀 [Instance Monitor] Khởi động vòng lặp Quản lý & Duy trì Instances (Chu kỳ: {InstanceHeartbeatInterval.TotalSeconds} giây)");

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RedisInstanceService.SyncInstancesAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [Instance Monitor Error] Lỗi xảy ra trong chu kỳ quét và đồng bộ dữ liệu Instance: {ex.Message}");
                }

                await Task.Delay(InstanceHeartbeatInterval, stoppingToken);
            }
        }

        public async Task ShutdownInstancesCluster()
        {
            Console.WriteLine("🔒 [Shutdown-Job] Ứng dụng đang dừng...");
            await RedisInstanceService.ShutdownInstancesClusterAsync();
            await Task.CompletedTask;
        }
    }
}
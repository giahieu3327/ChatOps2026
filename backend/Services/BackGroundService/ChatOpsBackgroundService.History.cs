using ChatOps.Services.RedisService;

namespace ChatOps.Services.BackGroundService
{
    public partial class ChatOpsBackgroundService
    {
        private static readonly TimeSpan HistoryReconcileInterval = TimeSpan.FromSeconds(30);

        private async Task ProcessHistoryReconcile(CancellationToken stoppingToken)
        {
            Console.WriteLine($"🚀 [History Monitor] Khởi động quản lý vòng đời Lịch sử theo Session (Chu kỳ: {HistoryReconcileInterval.TotalSeconds} giây)");

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RedisHistoryService.ReconcileHistoryAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [History Monitor Error] Lỗi xử lý đồng bộ vòng đời History: {ex.Message}");
                }

                await Task.Delay(HistoryReconcileInterval, stoppingToken);
            }
        }
    }
}
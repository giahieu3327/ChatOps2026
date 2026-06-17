using ChatOps.Services.RedisService;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.BackGroundService
{
    public partial class ChatOpsBackgroundService
    {
        private static readonly TimeSpan SessionReconcileInterval = TimeSpan.FromSeconds(30);

        private async Task ProcessSessionReconcile(CancellationToken stoppingToken)
        {
            Console.WriteLine($"🚀 [Session Monitor] Khởi động vòng lặp đồng bộ Session (Chu kỳ: {SessionReconcileInterval.TotalSeconds} giây)");

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    bool isReconciled = await RedisUserSessionService.ReconcileSessionCountAsync();
                    
                    if (!isReconciled)
                    {
                        Console.WriteLine("❌ [Session Monitor] Rà soát và đồng bộ bộ đếm hệ thống thất bại.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [Session Monitor Error] Lỗi xảy ra trong chu kỳ rà soát Session: {ex.Message}");
                }

                await Task.Delay(SessionReconcileInterval, stoppingToken);
            }
        }
        public async Task ShutdownSessionReconcile()
        {
            string localIp = AppContext.ServerIP;
            Console.WriteLine($"🛑 [Shutdown-Job] Ứng dụng đang dừng. Tiến hành thu hồi toàn bộ Session thuộc Node: [{localIp}]");

            try
            {
                bool isReconciled = await RedisUserSessionService.ReconcileSessionCountAsync();
                    
                if (isReconciled)
                {
                    Console.WriteLine("✅ [Session Monitor] Rà soát và đồng bộ bộ đếm hệ thống thành công.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Session Monitor Error] Lỗi xảy ra trong chu kỳ rà soát Session: {ex.Message}");
            }

            Console.WriteLine($"✅ [Shutdown-Job] Hoàn tất tiến trình thu hồi toàn bộ Session của Node: [{localIp}]");
        }
    }
}
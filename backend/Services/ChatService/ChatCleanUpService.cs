using ChatOps.Models;
using ChatOps.Services.RedisService;
using ChatOps.Services.SystemService;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.ChatService
{
    public static class ChatCleanUpService
    {
        private static async Task SendLogWithDelayAsync(bool debug, string connectionId, string message)
        {
            await Task.Delay(100);
            await RedisChannelService.SendMessageToClientAsync(debug, connectionId, message);
        }
        public static async Task<string> SystemPrune(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline SystemPrune...");

            bool isAllCommand = parsed.TryGetValue("all", out var allVal) && allVal == "true";

            if (isAllCommand)
            {
                await SendLogWithDelayAsync(session.Debug, connectionId, $"⚠️ [Docker API] Đang dọn dẹp triệt để hệ thống (-a --volumes)...");
                string pruneAllResult = await SystemCommandService.RunAsync("docker system prune -a -f --volumes");

                return $"⚠️ AGGRESSIVE CLEANUP REPORT [Local Node: {AppContext.ServerID}]\n" +
                       $"--------------------------------------------------\n" +
                       $"{pruneAllResult}";
            }
            else
            {
                await SendLogWithDelayAsync(session.Debug, connectionId, $"🧹 [Docker API] Đang thực hiện dọn dẹp an toàn...");
                
                string result = $"🧹 SAFE CLEANUP REPORT [Local Node: {AppContext.ServerID}]\n" +
                                $"--------------------------------------------------\n\n";

                string containerPrune = await SystemCommandService.RunAsync("docker container prune -f");
                result += $"🗑️ Containers:\n{containerPrune}\n\n";

                string networkPrune = await SystemCommandService.RunAsync("docker network prune -f");
                result += $"🌐 Networks:\n{networkPrune}\n\n";

                string imagePrune = await SystemCommandService.RunAsync("docker image prune -f");
                result += $"📦 Images:\n{imagePrune}\n\n";

                string volumePrune = await SystemCommandService.RunAsync("docker volume prune -f");
                result += $"💾 Volumes:\n{volumePrune}\n";

                await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Docker API] Hoàn tất dọn dẹp.");

                return result;
            }
        }
    }
}
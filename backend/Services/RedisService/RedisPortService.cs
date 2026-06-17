using StackExchange.Redis;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.RedisService
{
    public static class RedisPortService
    {
        private static IDatabase Db => AppContext.RedisDB;
        private const string Prefix = "port_reservation";

        /// <summary>
        /// Đăng ký và giữ chỗ (Insert) một danh sách các cổng trên một Node cụ thể với thời gian hết hạn (mặc định 3 phút).
        /// </summary>
        public static async Task<bool> ReservePortsAsync(string nodeIdOrIp, int[] ports, string reason = "RESERVED_BY_DEPLOY", TimeSpan? expiry = null)
        {
            var ttl = expiry ?? TimeSpan.FromMinutes(3);
            var batch = Db.CreateBatch();
            
            foreach (var port in ports)
            {
                string key = $"{Prefix}:{nodeIdOrIp}:{port}";
                _ = batch.StringSetAsync(key, reason, ttl);
            }

            batch.Execute();
            return await Task.FromResult(true);
        }

        /// <summary>
        /// Kiểm tra (Get) xem một cổng cụ thể trên Node đã bị chiếm dụng trên hệ thống Cluster hay chưa.
        /// </summary>
        public static async Task<bool> IsPortReservedAsync(string nodeIdOrIp, int port)
        {
            string key = $"{Prefix}:{nodeIdOrIp}:{port}";
            return await Db.KeyExistsAsync(key);
        }

        /// <summary>
        /// Giải phóng (Delete) dải cổng của một Node khi không còn sử dụng hoặc khi gỡ cài đặt container.
        /// </summary>
        public static async Task<bool> ReleasePortsAsync(string nodeIdOrIp, int[] ports)
        {
            var batch = Db.CreateBatch();

            foreach (var port in ports)
            {
                string key = $"{Prefix}:{nodeIdOrIp}:{port}";
                _ = batch.KeyDeleteAsync(key);
            }

            batch.Execute();
            return await Task.FromResult(true);
        }

        /// <summary>
        /// Lấy thông tin trạng thái giữ chỗ hiện tại của một cổng (Dùng cho mục đích Debug/Inspect).
        /// </summary>
        public static async Task<string?> GetPortReservationDetailsAsync(string nodeIdOrIp, int port)
        {
            string key = $"{Prefix}:{nodeIdOrIp}:{port}";
            var value = await Db.StringGetAsync(key);
            return value.HasValue ? value.ToString() : null;
        }
    }
}
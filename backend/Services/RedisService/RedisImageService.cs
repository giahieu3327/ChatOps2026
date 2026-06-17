using System.Text.Json;
using StackExchange.Redis;
using ChatOps.Data;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.RedisService
{
    public static class RedisImageService
    {
        private static string GetRedisKey() => "imageservices:list";

        private static string GetRedisCountKey() => "imageservices:count";

        // ==========================================
        // 5 HÀM QUẢN LÝ BỘ ĐẾM (COUNT FUNCTIONS)
        // ==========================================

        public static async Task<(bool success, Dictionary<string, string> counts)> GetImageCountAsync()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var db = AppContext.RedisDB;
                string countKey = GetRedisCountKey();
                var value = await db.StringGetAsync(countKey);
                if (!value.HasValue) return (false, result);

                result.Add("imageservices", value.ToString());
                return (true, result);
            }
            catch { return (false, result); }
        }

        public static async Task<(bool success, string message)> InsertImageCountAsync()
        {
            try
            {
                string countKey = GetRedisCountKey();
                bool created = await AppContext.RedisDB.StringSetAsync(countKey, 0, null, When.NotExists);
                return (true, "Success");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateImageCountValueAsync(bool isIncrement = true, long? exactCount = null)
        {
            try
            {
                var db = AppContext.RedisDB;
                string countKey = GetRedisCountKey();

                if (exactCount.HasValue)
                {
                    if (exactCount.Value <= 0)
                    {
                        await DeleteImageCountAsync();
                        return (true, "0 (Key Deleted)");
                    }

                    await db.StringSetAsync(countKey, exactCount.Value);
                    return (true, exactCount.Value.ToString());
                }

                long updatedVal;
                if (isIncrement)
                {
                    updatedVal = await db.StringIncrementAsync(countKey);
                }
                else
                {
                    var tran = db.CreateTransaction();
                    tran.AddCondition(Condition.KeyExists(countKey));

                    var currentAsync = db.StringGetAsync(countKey);
                    if (await tran.ExecuteAsync())
                    {
                        var current = await currentAsync;
                        if (!current.HasValue) return (false, "⚠️ Khóa bộ đếm không tồn tại.");

                        updatedVal = (long)current - 1;
                        if (updatedVal <= 0)
                        {
                            await DeleteImageCountAsync();
                            return (true, "0 (Key Automatically Deleted)");
                        }
                        else
                        {
                            await db.StringSetAsync(countKey, updatedVal);
                        }
                    }
                    else
                    {
                        return (false, "⚡ Xung đột dữ liệu đồng thời khi giảm bộ đếm.");
                    }
                }

                return (true, updatedVal.ToString());
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateImageCountKeyAsync(string oldKeyName, string newKeyName)
        {
            try
            {
                var db = AppContext.RedisDB;
                if (await db.KeyExistsAsync(oldKeyName) && !await db.KeyExistsAsync(newKeyName))
                {
                    await db.KeyRenameAsync(oldKeyName, newKeyName);
                    return (true, "✅ Đổi định danh Key bộ đếm thành công.");
                }
                return (false, "❌ Không tìm thấy bộ đếm cũ hoặc bộ đếm mới đã tồn tại.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> DeleteImageCountAsync()
        {
            try
            {
                var db = AppContext.RedisDB;
                bool isDeleted = await db.KeyDeleteAsync(GetRedisCountKey());
                return (true, isDeleted ? "Success" : "Key not found");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // ==========================================
        // 5 HÀM QUẢN LÝ DỮ LIỆU KEY (KEY FUNCTIONS)
        // ==========================================

        public static async Task<(bool success, Dictionary<string, string> services)> GetImageAsync(string? imageName = null, bool isGetAll = false)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var db = AppContext.RedisDB;
                string hashKey = GetRedisKey();

                if (!await db.KeyExistsAsync(hashKey)) return (false, result);

                if (!string.IsNullOrWhiteSpace(imageName))
                {
                    string searchName = imageName.Trim();
                    var value = await db.HashGetAsync(hashKey, searchName);
                    if (value.HasValue)
                    {
                        result.Add(searchName, value.ToString());
                        return (true, result);
                    }
                    return (false, result);
                }
                else if (isGetAll)
                {
                    var entries = await db.HashGetAllAsync(hashKey);
                    foreach (var entry in entries)
                    {
                        result.Add(entry.Name.ToString(), entry.Value.ToString());
                    }
                    return (true, result);
                }

                return (false, result);
            }
            catch { return (false, result); }
        }

        public static async Task<(bool success, string message)> InsertImageAsync()
        {
            try
            {
                var db = AppContext.RedisDB;
                string hashKey = GetRedisKey();

                var tran = db.CreateTransaction();
                tran.AddCondition(Condition.KeyNotExists(hashKey));
                _ = tran.HashSetAsync(hashKey, "init:placeholder", "{}");

                if (await tran.ExecuteAsync())
                {
                    return (true, "✅ Khởi tạo phân vùng Hash ImageServices thành công.");
                }
                return (false, "⚠️ Cấu hình ImageServices đã tồn tại trên Redis.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateImageValueAsync(string? fieldName = null, string? jsonValue = null)
        {
            try
            {
                var db = AppContext.RedisDB;
                string hashKey = GetRedisKey();

                if (!string.IsNullOrWhiteSpace(fieldName) && !string.IsNullOrWhiteSpace(jsonValue))
                {
                    var tran = db.CreateTransaction();
                    _ = tran.HashSetAsync(hashKey, fieldName, jsonValue);
                    _ = tran.HashDeleteAsync(hashKey, "init:placeholder");

                    if (await tran.ExecuteAsync())
                    {
                        return (true, "✅ Đã ghi nhận/cập nhật thông tin cấu hình Image.");
                    }
                    return (false, "⚡ Xung đột dữ liệu khi ghi nhận thông tin Image.");
                }
                else
                {
                    if (!await db.KeyExistsAsync(hashKey)) return (false, "❌ Cấu hình không tồn tại.");
                    return (true, "⏱️ Key tồn tại (Vô hạn TTL).");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateImageKeyAsync(string oldKeyName, string newKeyName)
        {
            try
            {
                var db = AppContext.RedisDB;
                if (await db.KeyExistsAsync(oldKeyName) && !await db.KeyExistsAsync(newKeyName))
                {
                    await db.KeyRenameAsync(oldKeyName, newKeyName);
                    return (true, "✅ Thay đổi định danh Key chính thành công.");
                }
                return (false, "❌ Không tìm thấy Key cũ hoặc định danh Key mới bị trùng lặp.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> DeleteImageAsync(string? fieldName = null, bool isDeleteAll = false)
        {
            try
            {
                var db = AppContext.RedisDB;
                string hashKey = GetRedisKey();

                if (isDeleteAll)
                {
                    await db.KeyDeleteAsync(hashKey);
                    await DeleteImageCountAsync();
                    return (true, "🗑️ Đã giải phóng TOÀN BỘ cấu hình Image và bộ đếm liên quan.");
                }

                if (string.IsNullOrWhiteSpace(fieldName)) return (false, "⚠️ Dữ liệu xóa không hợp lệ.");

                var tran = db.CreateTransaction();
                _ = tran.HashDeleteAsync(hashKey, fieldName);

                if (await tran.ExecuteAsync())
                {
                    long length = await db.HashLengthAsync(hashKey);
                    if (length == 0)
                    {
                        await db.KeyDeleteAsync(hashKey);
                    }
                    return (true, "🗑️ Đã xóa cấu hình Image được chỉ định khỏi Hash.");
                }

                return (false, "⚠️ Yêu cầu xóa không thành công.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // ==========================================
        // HÀM ĐỒNG BỘ ĐỒNG THỜI (SYNC FUNCTION)
        // ==========================================

        public static async Task SyncImageServicesAsync()
        {
            try
            {
                var db = AppContext.RedisDB;
                string hashKey = GetRedisKey();

                // Khởi tạo cấu trúc nền nếu chưa tồn tại trên Redis
                if (!await db.KeyExistsAsync(hashKey))
                {
                    await InsertImageAsync();
                    await InsertImageCountAsync();
                }

                var localServices = ImageCategories.ImageServices;
                if (localServices == null) localServices = new Dictionary<string, (string Image, string Network, string InPort, string Type, string Env, string neededCount, string Path)>();

                var redisEntries = await db.HashGetAllAsync(hashKey);
                var redisMap = redisEntries
                    .ToDictionary(e => e.Name.ToString(), e => e.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                redisMap.Remove("init:placeholder");

                // ĐỒNG BỘ MỘT CHIỀU: REDIS -> LOCAL MEMORY
                foreach (var redisKey in redisMap.Keys)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(redisMap[redisKey]);
                        var root = doc.RootElement;

                        string image = root.GetProperty("Image").GetString() ?? string.Empty;
                        string network = root.GetProperty("Network").GetString() ?? string.Empty;
                        string inPort = root.GetProperty("InPort").GetString() ?? string.Empty;
                        string type = root.GetProperty("Type").GetString() ?? string.Empty;
                        string env = root.GetProperty("Env").GetString() ?? string.Empty;
                        string neededCount = root.GetProperty("neededCount").GetString() ?? string.Empty;
                        string path = root.GetProperty("Path").GetString() ?? string.Empty;

                        // Kiểm tra xem Local đã có thông tin chưa hoặc thông số có bị thay đổi không
                        bool isExistLocal = localServices.TryGetValue(redisKey, out var localData);
                        bool isChanged = !isExistLocal || 
                                        localData.Image != image || 
                                        localData.Network != network || 
                                        localData.InPort != inPort || 
                                        localData.Type != type || 
                                        localData.Env != env || 
                                        localData.neededCount != neededCount ||
                                        localData.Path != path;


                        if (isChanged)
                        {
                            Console.WriteLine($"📥 [Image Monitor] Phát hiện thay đổi cấu hình từ Redis cho '{redisKey}'. Đang nạp ngược vào Local...");
                            ImageCategories.ImageServices[redisKey] = (image, network, inPort, type, env, neededCount, path);
                        }
                    }
                    catch (Exception parseEx)
                    {
                        Console.WriteLine($"⚠️ [Image Monitor Error] Không thể parse cấu hình tùy biến '{redisKey}': {parseEx.Message}");
                    }
                }

                // Cập nhật lại số lượng đếm dựa trên số lượng hash thực tế trên Redis
                long currentRealCount = redisMap.Count;
                await UpdateImageCountValueAsync(exactCount: currentRealCount);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Sync Error] Lỗi cục bộ trong quá trình đồng bộ Image: {ex.Message}");
            }
        }
    }
}
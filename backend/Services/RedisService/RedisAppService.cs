using System.Text.Json;
using StackExchange.Redis;
using ChatOps.Data;
using AppContext = ChatOps.Data.AppContext;
using ChatOps.Services.FileService;

namespace ChatOps.Services.RedisService
{
    public static class RedisAppService
    {
        public static string GetRedisKey() => "appservices:list";

        private static string GetRedisCountKey() => "appservices:count";

        // ==========================================
        // 5 HÀM QUẢN LÝ BỘ ĐẾM (COUNT FUNCTIONS)
        // ==========================================

        public static async Task<(bool success, Dictionary<string, string> counts)> GetAppCountAsync()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var db = AppContext.RedisDB;
                string countKey = GetRedisCountKey();
                var value = await db.StringGetAsync(countKey);
                if (!value.HasValue) return (false, result);

                result.Add("appservices", value.ToString());
                return (true, result);
            }
            catch { return (false, result); }
        }

        public static async Task<(bool success, string message)> InsertAppCountAsync()
        {
            try
            {
                string countKey = GetRedisCountKey();
                bool created = await AppContext.RedisDB.StringSetAsync(countKey, 0, null, When.NotExists);
                return (true, "Success");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateAppCountValueAsync(bool isIncrement = true, long? exactCount = null)
        {
            try
            {
                var db = AppContext.RedisDB;
                string countKey = GetRedisCountKey();

                if (exactCount.HasValue)
                {
                    if (exactCount.Value <= 0)
                    {
                        await DeleteAppCountAsync();
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
                            await DeleteAppCountAsync();
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

        public static async Task<(bool success, string message)> UpdateAppCountKeyAsync(string oldKeyName, string newKeyName)
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

        public static async Task<(bool success, string message)> DeleteAppCountAsync()
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

        public static async Task<(bool success, Dictionary<string, string> services)> GetAppAsync(string? app = null, bool isGetAll = false)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var db = AppContext.RedisDB;
                string hashKey = GetRedisKey();

                if (!await db.KeyExistsAsync(hashKey)) return (false, result);

                if (!string.IsNullOrWhiteSpace(app))
                {
                    string searchapp = app.Trim();
                    var value = await db.HashGetAsync(hashKey, searchapp);
                    if (value.HasValue)
                    {
                        result.Add(searchapp, value.ToString());
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

        public static async Task<(bool success, string message)> InsertAppAsync()
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
                    return (true, "✅ Khởi tạo phân vùng Hash AppServices thành công.");
                }
                return (false, "⚠️ Cấu hình AppServices đã tồn tại trên Redis.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateAppValueAsync(string? app = null, string? url = null, string? servicetype = null, bool IsReleased = false)
        {
            try
            {
                var db = AppContext.RedisDB;
                string hashKey = GetRedisKey();

                if (!string.IsNullOrWhiteSpace(app) && !string.IsNullOrWhiteSpace(url))
                {
                    var dataObject = new { Url = url, ServiceType = servicetype ?? string.Empty, IsReleased = IsReleased };
                    string jsonValue = JsonSerializer.Serialize(dataObject);

                    var tran = db.CreateTransaction();
                    _ = tran.HashSetAsync(hashKey, app, jsonValue);
                    _ = tran.HashDeleteAsync(hashKey, "init:placeholder");

                    if (await tran.ExecuteAsync())
                    {
                        return (true, "✅ Đã ghi nhận/cập nhật thông tin cấu hình App.");
                    }
                    return (false, "⚡ Xung đột dữ liệu khi ghi nhận thông tin App.");
                }
                else
                {
                    if (!await db.KeyExistsAsync(hashKey)) return (false, "❌ Cấu hình không tồn tại.");
                    return (true, "⏱️ Key tồn tại (Vô hạn TTL).");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateAppKeyAsync(string oldKeyName, string newKeyName)
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

        public static async Task<(bool success, string message)> DeleteAppAsync(string? app = null, bool isDeleteAll = false)
        {
            try
            {
                var db = AppContext.RedisDB;
                string hashKey = GetRedisKey();

                if (isDeleteAll)
                {
                    await db.KeyDeleteAsync(hashKey);
                    await DeleteAppCountAsync();
                    return (true, "🗑️ Đã giải phóng TOÀN BỘ cấu hình App và bộ đếm liên quan.");
                }

                if (string.IsNullOrWhiteSpace(app)) return (false, "⚠️ Dữ liệu xóa không hợp lệ.");

                var tran = db.CreateTransaction();
                _ = tran.HashDeleteAsync(hashKey, app);

                if (await tran.ExecuteAsync())
                {
                    long length = await db.HashLengthAsync(hashKey);
                    if (length == 0)
                    {
                        await db.KeyDeleteAsync(hashKey);
                    }
                    return (true, "🗑️ Đã xóa cấu hình App được chỉ định khỏi Hash.");
                }

                return (false, "⚠️ Yêu cầu xóa không thành công.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // ==========================================
        // HÀM ĐỒNG BỘ ĐỒNG THỜI (SYNC FUNCTION)
        // ==========================================
        public static async Task SyncAppServicesAsync()
        {
            try
            {
                var db = AppContext.RedisDB;
                string hashKey = GetRedisKey();

                if (!await db.KeyExistsAsync(hashKey))
                {
                    await InsertAppAsync();
                    await InsertAppCountAsync();
                }

                var localServices = AppCategories.AppServices;
                var redisEntries = await db.HashGetAllAsync(hashKey);

                var redisMap = redisEntries
                    .ToDictionary(e => e.Name.ToString(), e => e.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                redisMap.Remove("init:placeholder");

                // =====================================================
                // CHIỀU ĐỒNG BỘ: REDIS -> LOCAL
                // =====================================================
                foreach (var redisKey in redisMap.Keys)
                {
                    try
                    {
                        var redisData = JsonSerializer.Deserialize<JsonElement>(redisMap[redisKey]);
                        string url = redisData.TryGetProperty("Url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                        string servicetype = redisData.TryGetProperty("ServiceType", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                        bool isreleased = redisData.TryGetProperty("IsReleased", out var r) && r.GetBoolean();

                        // Kiểm tra xem Local đã có thông tin App này chưa hoặc có sự thay đổi thông số nào không
                        bool isExistLocal = localServices.TryGetValue(redisKey, out var localData);
                        bool isChanged = !isExistLocal || localData.Url != url || localData.ServiceType != servicetype || localData.IsReleased != isreleased;

                        if (isChanged)
                        {
                            Console.WriteLine($"📥 [App Monitor] Phát hiện thay đổi cấu hình từ Redis cho '{redisKey}'. Đang nạp vào Local...");

                            // Cập nhật ngược vào vùng nhớ tĩnh Local Memory
                            AppCategories.AppServices[redisKey] = (url, servicetype, isreleased);

                            string basePath = "/home/ubuntu/ChatOps/services";
                            string trialPath = Path.Combine(basePath, "Trial", redisKey);
                            string finalPath = Path.Combine(basePath, "Final", redisKey);

                            // BƯỚC THỬ NGHIỆM (TRIAL): Luôn cần pull mã nguồn từ Git về nếu chưa tồn tại
                            if (!Directory.Exists(trialPath) && !string.IsNullOrWhiteSpace(url))
                            {
                                Console.WriteLine($"📁 [App Monitor] Đang thiết lập không gian kiểm thử (Trial) tại: {trialPath}");
                                string cloneResult = await GitService.GitService.CloneRepository(url, trialPath);
                                if (cloneResult.Contains("fatal", StringComparison.OrdinalIgnoreCase))
                                {
                                    Console.WriteLine($"❌ Clone mã nguồn cho '{redisKey}' thất bại:\n{cloneResult}");
                                    continue; // Bỏ qua bước tiếp theo nếu không clone được code
                                }
                            }

                            // BƯỚC PHÁT HÀNH HOÀN CHỈNH (FINAL)
                            if (isreleased)
                            {
                                Console.WriteLine($"🚀 [App Monitor] Ứng dụng '{redisKey}' ở trạng thái Production (IsReleased = true). Bắt đầu đồng bộ sang Final...");
                                string sourceCompose = Path.Combine(trialPath, "docker-git.yml");
                                string destRegistryCompose = Path.Combine(finalPath, "docker-registry.yml");

                                if (File.Exists(sourceCompose))
                                {
                                    Directory.CreateDirectory(finalPath);

                                    Console.WriteLine($"📁 [Transform] Tiến hành chuyển đổi cấu trúc file từ docker-git.yml sang docker-registry.yml tại Final...");
                                    string transformResult = DockerComposeFileTransformer.TransformGitToRegistry(sourceCompose, destRegistryCompose);

                                    if (transformResult == "SUCCESS")
                                    {
                                        Console.WriteLine($"✅ Đồng bộ cấu hình Production cho '{redisKey}' thành công!");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"❌ Chuyển đổi file compose thất bại cho '{redisKey}': {transformResult}");
                                    }
                                }
                                else
                                {
                                    Console.WriteLine($"❌ Không thể đồng bộ Production vì thiếu tệp gốc docker-git.yml tại: {sourceCompose}");
                                }
                            }
                            else
                            {
                                // Nếu IsReleased trên Redis chuyển từ true sang false, tiến hành dọn dẹp thư mục Production cục bộ
                                if (Directory.Exists(finalPath))
                                {
                                    try
                                    {
                                        Directory.Delete(finalPath, true);
                                        Console.WriteLine($"🧹 Thao tác hạ cấp gỡ bỏ (Unrelease): Đã xóa thư mục Final trống tại `{finalPath}`");
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"⚠️ Lỗi dọn dẹp thư mục Final khi hạ cấp: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception parseEx)
                    {
                        Console.WriteLine($"⚠️ [App Monitor Error] Không thể xử lý cấu hình '{redisKey}': {parseEx.Message}");
                        AppCategories.AppServices[redisKey] = (redisMap[redisKey], string.Empty, false);
                    }
                }

                // Đồng bộ lại số lượng đếm thực tế của phân vùng Hash sau chu kỳ quét dựa trên thông tin trên Redis
                long currentRealCount = await db.HashLengthAsync(hashKey);
                if (await db.HashExistsAsync(hashKey, "init:placeholder")) currentRealCount--;
                await UpdateAppCountValueAsync(exactCount: currentRealCount);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Sync Error] Lỗi cục bộ trong quá trình đồng bộ: {ex.Message}");
            }
        }
    }
}
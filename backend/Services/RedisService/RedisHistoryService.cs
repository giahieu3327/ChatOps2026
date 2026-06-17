using StackExchange.Redis;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.RedisService
{
    public static class RedisHistoryService
    {
        private static readonly TimeSpan HISTORY_TTL = TimeSpan.FromMinutes(10);
        private const long MAX_HISTORY_ITEMS = 50;

        private static string GetHistoryKey(string username) =>
            $"history:{username.Trim().ToLower()}";

        private static string GetHistoryCountKey(string username) =>
            $"history:count:{username.Trim().ToLower()}";

        public static async Task<(bool success, Dictionary<string, string> counts)> GetHistoryCountAsync(string? username = null, bool isGetAll = false)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var db = AppContext.RedisDB;

                if (!isGetAll)
                {
                    if (string.IsNullOrWhiteSpace(username)) return (false, result);

                    string countKey = GetHistoryCountKey(username);
                    var value = await db.StringGetAsync(countKey);
                    if (!value.HasValue) return (false, result);

                    result.Add(username.Trim(), value.ToString());
                    return (true, result);
                }
                else
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "history:count:*";
                    var allKeys = new List<RedisKey>();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            allKeys.Add(key);
                        }
                    }

                    allKeys = allKeys.Distinct().ToList();
                    if (!allKeys.Any()) return (true, result);

                    var batch = db.CreateBatch();
                    var tasks = allKeys.Select(key => new { Key = key, Task = batch.StringGetAsync(key) }).ToList();
                    batch.Execute();

                    await Task.WhenAll(tasks.Select(t => t.Task));

                    foreach (var item in tasks)
                    {
                        var value = item.Task.Result;
                        if (value.HasValue)
                        {
                            string keyStr = item.Key.ToString();
                            string extractedUser = keyStr.StartsWith("history:count:") ? keyStr.Substring(14) : keyStr;
                            result[extractedUser] = value.ToString();
                        }
                    }
                    return (true, result);
                }
            }
            catch { return (false, result); }
        }

        public static async Task<(bool success, string message)> InsertHistoryCountAsync(string username)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(username)) return (false, "⚠️ Username không hợp lệ.");

                string countKey = GetHistoryCountKey(username);
                bool created = await AppContext.RedisDB.StringSetAsync(countKey, 0, HISTORY_TTL, When.NotExists);
                return created ? (true, "Success") : (false, "⚠️ Bộ đếm đã tồn tại.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateHistoryCountValueAsync(string username, bool isIncrement = true, long? exactCount = null, bool isUpdateTTL = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(username)) return (false, "⚠️ Username không hợp lệ.");

                var db = AppContext.RedisDB;
                string countKey = GetHistoryCountKey(username);

                if (isUpdateTTL)
                {
                    bool extended = await db.KeyExpireAsync(countKey, HISTORY_TTL);
                    return extended ? (true, "⏱️ Gia hạn TTL bộ đếm thành công.") : (false, "❌ Khóa bộ đếm không tồn tại.");
                }

                if (exactCount.HasValue)
                {
                    if (exactCount.Value <= 0)
                    {
                        await db.KeyDeleteAsync(countKey);
                        return (true, "0 (Key Deleted)");
                    }

                    await db.StringSetAsync(countKey, exactCount.Value, HISTORY_TTL);
                    return (true, exactCount.Value.ToString());
                }

                if (isIncrement)
                {
                    long updatedVal = await db.StringIncrementAsync(countKey);
                    await db.KeyExpireAsync(countKey, HISTORY_TTL);
                    return (true, updatedVal.ToString());
                }
                else
                {
                    long updatedVal = await db.StringDecrementAsync(countKey);
                    if (updatedVal <= 0)
                    {
                        await db.KeyDeleteAsync(countKey);
                        return (true, "0 (Key Automatically Deleted)");
                    }
                    
                    await db.KeyExpireAsync(countKey, HISTORY_TTL);
                    return (true, updatedVal.ToString());
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateHistoryCountKeyAsync(string oldUsername, string newUsername)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(oldUsername) || string.IsNullOrWhiteSpace(newUsername))
                {
                    return (false, "⚠️ Dữ liệu đầu vào không hợp lệ.");
                }

                var db = AppContext.RedisDB;
                string oldCountKey = GetHistoryCountKey(oldUsername);
                string newCountKey = GetHistoryCountKey(newUsername);

                var currentVal = await db.StringGetAsync(oldCountKey);
                if (!currentVal.HasValue) return (false, "❌ Không tìm thấy bộ đếm cũ.");

                var tran = db.CreateTransaction();
                tran.AddCondition(Condition.KeyExists(oldCountKey));
                tran.AddCondition(Condition.KeyNotExists(newCountKey));

                _ = tran.KeyDeleteAsync(oldCountKey);
                _ = tran.StringSetAsync(newCountKey, currentVal, HISTORY_TTL);

                if (await tran.ExecuteAsync())
                {
                    return (true, "✅ Đổi định danh Key Bộ đếm thành công.");
                }

                return (false, "❌ Xung đột dữ liệu: Bộ đếm cũ không tồn tại hoặc bộ đếm mới đã được khởi tạo.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> DeleteHistoryCountAsync(string? username = null, bool isDeleteAll = false)
        {
            try
            {
                var db = AppContext.RedisDB;

                if (isDeleteAll)
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "history:count:*";
                    var allKeys = new List<RedisKey>();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            allKeys.Add(key);
                        }
                    }

                    allKeys = allKeys.Distinct().ToList();
                    if (!allKeys.Any()) return (true, "ℹ️ Không có bộ đếm lịch sử nào để xóa.");

                    var tran = db.CreateTransaction();
                    _ = tran.KeyDeleteAsync(allKeys.ToArray());
                    await tran.ExecuteAsync();
                    return (true, "🗑️ Đã xóa toàn bộ các khóa bộ đếm history:count.");
                }

                if (string.IsNullOrWhiteSpace(username)) return (false, "⚠️ Username không hợp lệ.");
                bool isDeleted = await db.KeyDeleteAsync(GetHistoryCountKey(username));
                return (true, isDeleted ? "Success" : "Key not found");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, Dictionary<string, string> nodes)> GetHistoryAsync(string? username = null, bool isGetAll = false)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var db = AppContext.RedisDB;

                if (!string.IsNullOrWhiteSpace(username))
                {
                    string historyKey = GetHistoryKey(username);
                    var listValues = await db.ListRangeAsync(historyKey, 0, -1);
                    if (listValues.Length > 0)
                    {
                        var historyString = string.Join("\n", listValues.Select(v => v.ToString()));
                        result.Add(username.Trim(), historyString);
                        return (true, result);
                    }
                    return (false, result);
                }

                if (isGetAll)
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "history:*";
                    var allKeys = new List<RedisKey>();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            string keyStr = key.ToString();
                            if (!keyStr.StartsWith("history:count:") && !keyStr.EndsWith(":init"))
                            {
                                allKeys.Add(key);
                            }
                        }
                    }

                    allKeys = allKeys.Distinct().ToList();
                    if (!allKeys.Any()) return (true, result);

                    var batch = db.CreateBatch();
                    var tasks = allKeys.Select(key => new { Key = key, Task = batch.ListRangeAsync(key, 0, -1) }).ToList();
                    batch.Execute();

                    await Task.WhenAll(tasks.Select(t => t.Task));

                    foreach (var item in tasks)
                    {
                        var listValues = item.Task.Result;
                        if (listValues.Length > 0)
                        {
                            string keyStr = item.Key.ToString();
                            string extractedUser = keyStr.StartsWith("history:") ? keyStr.Substring(8) : keyStr;
                            string historyString = string.Join("\n", listValues.Select(v => v.ToString()));
                            result[extractedUser] = historyString;
                        }
                    }
                    return (true, result);
                }

                return (false, result);
            }
            catch { return (false, result); }
        }

        public static async Task<(bool success, string message)> InsertHistoryAsync(string username)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(username)) return (false, "⚠️ Username không hợp lệ.");

                var db = AppContext.RedisDB;
                string historyKey = GetHistoryKey(username);
                string initKey = $"{historyKey}:init";
                string countKey = GetHistoryCountKey(username);

                var tran = db.CreateTransaction();
                tran.AddCondition(Condition.KeyNotExists(historyKey));
                tran.AddCondition(Condition.KeyNotExists(initKey));
                tran.AddCondition(Condition.KeyNotExists(countKey));

                _ = tran.StringSetAsync(initKey, "initialized", HISTORY_TTL);
                _ = tran.StringSetAsync(countKey, 0, HISTORY_TTL);

                if (await tran.ExecuteAsync())
                {
                    return (true, "✅ Khởi tạo phân vùng lịch sử câu lệnh thành công.");
                }

                return (false, "⚠️ Vùng lưu trữ lịch sử của người dùng này đã tồn tại.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateHistoryValueAsync(string username, string? command = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(username)) return (false, "⚠️ Username không hợp lệ.");

                var db = AppContext.RedisDB;
                string historyKey = GetHistoryKey(username);
                string initKey = $"{historyKey}:init";
                string countKey = GetHistoryCountKey(username);

                if (!string.IsNullOrWhiteSpace(command))
                {
                    string cleanCommand = command.Trim();
                    
                    var currentItems = await db.ListRangeAsync(historyKey, 0, 0);
                    if (currentItems.Length > 0 && currentItems[0].ToString().Equals(cleanCommand, StringComparison.OrdinalIgnoreCase))
                    {
                        var refreshTran = db.CreateTransaction();
                        _ = refreshTran.KeyExpireAsync(historyKey, HISTORY_TTL);
                        _ = refreshTran.KeyExpireAsync(countKey, HISTORY_TTL);
                        
                        if (await refreshTran.ExecuteAsync())
                        {
                            long len = await db.ListLengthAsync(historyKey);
                            return (true, $"ℹ️ Câu lệnh trùng lặp với lệnh gần nhất. Chỉ gia hạn TTL. Hiện có {len} lệnh.");
                        }
                        return (false, "❌ Thao tác gia hạn lệnh trùng lặp bị lỗi do thay đổi trạng thái.");
                    }

                    var tran = db.CreateTransaction();
                    _ = tran.KeyDeleteAsync(initKey);
                    _ = tran.ListLeftPushAsync(historyKey, cleanCommand);
                    _ = tran.ListTrimAsync(historyKey, 0, MAX_HISTORY_ITEMS - 1);
                    _ = tran.KeyExpireAsync(historyKey, HISTORY_TTL);
                    var lenTask = tran.ListLengthAsync(historyKey);

                    if (await tran.ExecuteAsync())
                    {
                        long currentLength = await lenTask;
                        await db.StringSetAsync(countKey, currentLength, HISTORY_TTL);
                        return (true, $"📝 Đã ghi nhận lệnh mới và gia hạn TTL. Hiện có {currentLength} lệnh.");
                    }

                    return (false, "❌ Lỗi xung đột khi cập nhật lịch sử lệnh.");
                }
                else
                {
                    var tran = db.CreateTransaction();
                    _ = tran.KeyExpireAsync(historyKey, HISTORY_TTL);
                    _ = tran.KeyExpireAsync(initKey, HISTORY_TTL);
                    _ = tran.KeyExpireAsync(countKey, HISTORY_TTL);

                    if (await tran.ExecuteAsync())
                    {
                        return (true, "⏱️ Gia hạn TTL phân vùng lịch sử và bộ đếm thành công.");
                    }

                    return (false, "❌ Phân vùng lịch sử không tồn tại để gia hạn TTL.");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateHistoryKeyAsync(string oldUsername, string newUsername)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(oldUsername) || string.IsNullOrWhiteSpace(newUsername))
                {
                    return (false, "⚠️ Dữ liệu Username đầu vào không hợp lệ.");
                }

                var db = AppContext.RedisDB;
                string oldKey = GetHistoryKey(oldUsername);
                string newKey = GetHistoryKey(newUsername);
                string oldInitKey = $"{oldKey}:init";
                string newInitKey = $"{newKey}:init";
                string oldCountKey = GetHistoryCountKey(oldUsername);
                string newCountKey = GetHistoryCountKey(newUsername);

                if (!await db.KeyExistsAsync(oldKey) && !await db.KeyExistsAsync(oldInitKey))
                {
                    return (false, "❌ Không tìm thấy dữ liệu lịch sử của tài khoản cũ.");
                }
                if (await db.KeyExistsAsync(newKey) || await db.KeyExistsAsync(newInitKey))
                {
                    return (false, "⚠️ Phân vùng dữ liệu lịch sử của tài khoản mới đã có sẵn.");
                }

                var oldCommands = await db.ListRangeAsync(oldKey, 0, -1);
                var currentCountVal = await db.StringGetAsync(oldCountKey);

                var tran = db.CreateTransaction();
                tran.AddCondition(Condition.KeyNotExists(newKey));
                tran.AddCondition(Condition.KeyNotExists(newInitKey));
                tran.AddCondition(Condition.KeyNotExists(newCountKey));

                _ = tran.KeyDeleteAsync(oldKey);
                _ = tran.KeyDeleteAsync(oldInitKey);
                _ = tran.KeyDeleteAsync(oldCountKey);

                if (oldCommands.Length > 0)
                {
                    _ = tran.ListRightPushAsync(newKey, oldCommands);
                    _ = tran.KeyExpireAsync(newKey, HISTORY_TTL);
                }
                else
                {
                    _ = tran.StringSetAsync(newInitKey, "initialized", HISTORY_TTL);
                }

                if (currentCountVal.HasValue)
                {
                    _ = tran.StringSetAsync(newCountKey, currentCountVal, HISTORY_TTL);
                }

                if (await tran.ExecuteAsync())
                {
                    return (true, "✅ Đổi định danh tài khoản quản lý lịch sử câu lệnh và bộ đếm thành công.");
                }

                return (false, "❌ Thao tác đổi định danh thất bại do xung đột trạng thái dữ liệu.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> DeleteHistoryAsync(string? username = null, bool isDeleteAll = false)
        {
            try
            {
                var db = AppContext.RedisDB;

                if (isDeleteAll)
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "history:*";
                    var allKeys = new List<RedisKey>();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            if (!key.ToString().StartsWith("history:count:"))
                            {
                                allKeys.Add(key);
                            }
                        }
                    }

                    allKeys = allKeys.Distinct().ToList();

                    if (allKeys.Any())
                    {
                        var tran = db.CreateTransaction();
                        _ = tran.KeyDeleteAsync(allKeys.ToArray());
                        await tran.ExecuteAsync();
                    }

                    await DeleteHistoryCountAsync(isDeleteAll: true);
                    return (true, "🗑️ Đã xóa sạch toàn bộ lịch sử câu lệnh, các khóa khởi tạo ẩn và bộ đếm.");
                }

                if (!string.IsNullOrWhiteSpace(username))
                {
                    string historyKey = GetHistoryKey(username);

                    var tran = db.CreateTransaction();
                    _ = tran.KeyDeleteAsync(historyKey);
                    _ = tran.KeyDeleteAsync($"{historyKey}:init");
                    _ = tran.KeyDeleteAsync(GetHistoryCountKey(username));

                    await tran.ExecuteAsync();
                    return (true, "✅ Đã xóa hoàn toàn lịch sử câu lệnh của User.");
                }

                return (false, "⚠️ Yêu cầu thao tác xóa không hợp lệ.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }
        public static async Task ReconcileHistoryAsync()
        {
            var db = AppContext.RedisDB;
            if (db == null)
            {
                Console.WriteLine("⚠️ [History Reconcile] Không tìm thấy kết nối RedisDB hợp lệ từ AppContext. Bỏ qua chu kỳ này.");
                return;
            }

            var (success, counts) = await GetHistoryCountAsync(isGetAll: true);
            
            if (success && counts != null)
            {
                foreach (var kvp in counts)
                {
                    string username = kvp.Key.Trim().ToLower();
                    string sessionKey = $"session:{username}";

                    // Kiểm tra User còn online hay không bằng cách check Key Session tồn tại
                    bool isOnline = await db.KeyExistsAsync(sessionKey);

                    if (isOnline)
                    {
                        // Nếu online thì gia hạn TTL cho Key History của User đó trên Redis
                        await UpdateHistoryValueAsync(username, command: null);
                    }
                }
            }
        }
    }
}
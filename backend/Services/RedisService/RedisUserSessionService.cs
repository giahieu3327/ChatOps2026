using StackExchange.Redis;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.RedisService
{
    public static class RedisUserSessionService
    {
        private static readonly TimeSpan SESSION_TTL = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan LOCK_TTL = TimeSpan.FromSeconds(10);

        private static string GetRedisKey(string username) =>
            $"session:{username.Trim().ToLower()}";

        private static string GetRedisCountKey(string nodeIp) =>
            $"session:count:{nodeIp.Trim().ToLower()}";

        private static string GetReconcileLockKey() =>
            $"lock:session:reconcile";

        public static async Task<(bool success, Dictionary<string, string> counts)> GetUserSessionCountAsync(string? nodeIp = null, bool isGetAll = false)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var db = AppContext.RedisDB;

                if (!isGetAll)
                {
                    if (string.IsNullOrWhiteSpace(nodeIp)) return (false, result);

                    string countKey = GetRedisCountKey(nodeIp);
                    var value = await db.StringGetAsync(countKey);
                    if (!value.HasValue) return (false, result);

                    result.Add(nodeIp.Trim(), value.ToString());
                    return (true, result);
                }
                else
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "session:count:*";
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
                            string extractedIp = keyStr.StartsWith("session:count:") ? keyStr.Substring(14) : keyStr;
                            result[extractedIp] = value.ToString();
                        }
                    }
                    return (true, result);
                }
            }
            catch { return (false, result); }
        }

        public static async Task<(bool success, string message)> InsertUserSessionCountAsync(string nodeIp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Node IP không hợp lệ.");
                
                string countKey = GetRedisCountKey(nodeIp);
                await AppContext.RedisDB.StringSetAsync(countKey, 0, SESSION_TTL, When.NotExists);
                return (true, "Success");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateUserSessionCountValueAsync(string nodeIp, bool isIncrement = true, long? exactCount = null, bool isUpdateTTL = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Node IP không hợp lệ.");

                var db = AppContext.RedisDB;
                string countKey = GetRedisCountKey(nodeIp);

                if (isUpdateTTL)
                {
                    bool isApplied = await db.KeyExpireAsync(countKey, SESSION_TTL);
                    return (isApplied, isApplied ? "⏱️ Gia hạn TTL Node Count thành công (60s)." : "⚠️ Khóa bộ đếm không tồn tại hoặc lỗi gia hạn.");
                }

                if (exactCount.HasValue)
                {
                    if (exactCount.Value <= 0)
                    {
                        await DeleteUserSessionCountAsync(nodeIp);
                        return (true, "0 (Key Deleted)");
                    }

                    await db.StringSetAsync(countKey, exactCount.Value, SESSION_TTL);
                    return (true, exactCount.Value.ToString());
                }

                if (isIncrement)
                {
                    long updatedVal = await db.StringIncrementAsync(countKey);
                    await db.KeyExpireAsync(countKey, SESSION_TTL);
                    return (true, updatedVal.ToString());
                }
                else
                {
                    var tran = db.CreateTransaction();
                    var decTask = tran.StringDecrementAsync(countKey);

                    if (await tran.ExecuteAsync())
                    {
                        long updatedVal = await decTask;
                        if (updatedVal <= 0)
                        {
                            await DeleteUserSessionCountAsync(nodeIp);
                            return (true, "0 (Key Automatically Deleted)");
                        }
                        
                        await db.KeyExpireAsync(countKey, SESSION_TTL);
                        return (true, updatedVal.ToString());
                    }
                    return (false, "❌ Xung đột dữ liệu khi cập nhật bộ đếm.");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateUserSessionCountKeyAsync(string oldNodeIp, string newNodeIp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(oldNodeIp) || string.IsNullOrWhiteSpace(newNodeIp))
                {
                    return (false, "⚠️ Dữ liệu Node IP đầu vào không hợp lệ.");
                }

                var db = AppContext.RedisDB;
                string oldKey = GetRedisCountKey(oldNodeIp);
                string newKey = GetRedisCountKey(newNodeIp);

                // Lấy giá trị bộ đếm hiện tại của Node cũ
                var currentCountVal = await db.StringGetAsync(oldKey);
                if (!currentCountVal.HasValue) return (false, "❌ Không tìm thấy bộ đếm của Node cũ.");

                var tran = db.CreateTransaction();
                // ĐIỀU KIỆN RÀNG BUỘC: Khóa cũ phải còn đó và khóa mới chưa từng tồn tại
                tran.AddCondition(Condition.KeyExists(oldKey));
                tran.AddCondition(Condition.KeyNotExists(newKey));

                // Thực hiện dịch chuyển dữ liệu sang Key mới
                _ = tran.KeyDeleteAsync(oldKey);
                _ = tran.StringSetAsync(newKey, currentCountVal, SESSION_TTL);

                bool committed = await tran.ExecuteAsync();
                if (committed)
                {
                    // Kích hoạt quét đồng bộ lại để cập nhật chính xác các session user đang trỏ về Node mới
                    _ = Task.Run(() => ReconcileSessionCountAsync());
                    return (true, $"✅ Thay đổi định danh Node Count từ [{oldNodeIp}] sang [{newNodeIp}] thành công.");
                }

                return (false, "❌ Đổi định danh Node thất bại: Node cũ đã bị xóa hoặc Node mới đã được khởi tạo song song.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }
        
        public static async Task<(bool success, string message)> DeleteUserSessionCountAsync(string? nodeIp = null, bool isDeleteAll = false)
        {
            try
            {
                var db = AppContext.RedisDB;

                if (isDeleteAll)
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "session:count:*";
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
                    if (!allKeys.Any()) return (true, "ℹ️ Không có bộ đếm nào để xóa.");

                    await db.KeyDeleteAsync(allKeys.ToArray());
                    return (true, "🗑️ Đã xóa toàn bộ các khóa bộ đếm session:count.");
                }

                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Node IP không hợp lệ.");
                bool isDeleted = await db.KeyDeleteAsync(GetRedisCountKey(nodeIp));
                return (true, isDeleted ? "Success" : "Key not found");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, Dictionary<string, string> nodes)> GetUserSessionAsync(string? username = null, string? nodeIp = null, bool isGetAll = false)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var db = AppContext.RedisDB;

                if (!string.IsNullOrWhiteSpace(username))
                {
                    var value = await db.StringGetAsync(GetRedisKey(username));
                    if (value.HasValue)
                    {
                        result.Add(username.Trim(), value.ToString());
                        return (true, result);
                    }
                    return (false, result);
                }

                var endpoints = db.Multiplexer.GetEndPoints();
                string pattern = "session:*";
                var allKeys = new List<RedisKey>();

                foreach (var endpoint in endpoints)
                {
                    var server = db.Multiplexer.GetServer(endpoint);
                    await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                    {
                        string keyStr = key.ToString();
                        if (!keyStr.StartsWith("session:count:"))
                        {
                            allKeys.Add(key);
                        }
                    }
                }

                allKeys = allKeys.Distinct().ToList();
                if (!allKeys.Any()) return (true, result);

                var batch = db.CreateBatch();
                var keyTaskPairs = allKeys.Select(key => new { Key = key, Task = batch.StringGetAsync(key) }).ToList();
                batch.Execute();

                await Task.WhenAll(keyTaskPairs.Select(p => p.Task));

                foreach (var pair in keyTaskPairs)
                {
                    var value = pair.Task.Result;
                    if (value.HasValue)
                    {
                        string targetNodeIp = value.ToString();
                        string keyStr = pair.Key.ToString();
                        string extractedUser = keyStr.StartsWith("session:") ? keyStr.Substring(8) : keyStr;

                        if (!string.IsNullOrWhiteSpace(nodeIp))
                        {
                            if (targetNodeIp.Equals(nodeIp.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                result[extractedUser] = targetNodeIp;
                            }
                        }
                        else if (isGetAll)
                        {
                            result[extractedUser] = targetNodeIp;
                        }
                    }
                }
                return (true, result);
            }
            catch { return (false, result); }
        }

        public static async Task<(bool success, string message)> InsertUserSessionAsync(string username, string nodeIp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(nodeIp))
                {
                    return (false, "⚠️ Dữ liệu Username hoặc Node IP không hợp lệ.");
                }

                var db = AppContext.RedisDB;
                string sessionKey = GetRedisKey(username);
                string countKey = GetRedisCountKey(nodeIp);

                var currentVal = await db.StringGetAsync(sessionKey);
                if (currentVal.HasValue)
                {
                    return (false, $"⚠️ Phiên làm việc [{username}] đã tồn tại từ trước và trỏ tới Node [{currentVal}].");
                }

                await InsertUserSessionCountAsync(nodeIp);

                while (true)
                {
                    var tran = db.CreateTransaction();
                    tran.AddCondition(Condition.KeyNotExists(sessionKey));

                    _ = tran.StringSetAsync(sessionKey, nodeIp.Trim(), SESSION_TTL);
                    _ = tran.StringIncrementAsync(countKey);
                    _ = tran.KeyExpireAsync(countKey, SESSION_TTL);

                    bool committed = await tran.ExecuteAsync();

                    if (committed)
                    {
                        _ = Task.Run(() => ReconcileSessionCountAsync());
                        return (true, $"✅ Khởi tạo phiên làm việc cho User [{username}] trên Node [{nodeIp}] thành công.");
                    }

                    currentVal = await db.StringGetAsync(sessionKey);
                    if (currentVal.HasValue)
                    {
                        return (false, $"⚠️ Đăng ký song song thất bại. Phiên [{username}] đã được sở hữu bởi Node [{currentVal}].");
                    }
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateUserSessionValueAsync(string username, string? newNodeIp = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(username)) return (false, "⚠️ Tên người dùng không hợp lệ.");

                var db = AppContext.RedisDB;
                string sessionKey = GetRedisKey(username);

                var oldNodeIpValue = await db.StringGetAsync(sessionKey);
                if (!oldNodeIpValue.HasValue) return (false, "❌ Phiên làm việc không tồn tại.");

                string oldNodeIp = oldNodeIpValue.ToString();

                if (newNodeIp != null)
                {
                    string cleanedNewNodeIp = newNodeIp.Trim();
                    if (oldNodeIp.Equals(cleanedNewNodeIp, StringComparison.OrdinalIgnoreCase))
                    {
                        return await UpdateUserSessionValueAsync(username, null);
                    }

                    await InsertUserSessionCountAsync(cleanedNewNodeIp);

                    var tran = db.CreateTransaction();
                    tran.AddCondition(Condition.StringEqual(sessionKey, oldNodeIp));

                    _ = tran.StringSetAsync(sessionKey, cleanedNewNodeIp, SESSION_TTL);
                    _ = tran.StringDecrementAsync(GetRedisCountKey(oldNodeIp));
                    _ = tran.StringIncrementAsync(GetRedisCountKey(cleanedNewNodeIp));
                    _ = tran.KeyExpireAsync(GetRedisCountKey(cleanedNewNodeIp), SESSION_TTL);

                    bool committed = await tran.ExecuteAsync();
                    if (committed)
                    {
                        _ = Task.Run(() => ReconcileSessionCountAsync());
                        return (true, $"🔄 Đã cập nhật định tuyến Session sang Node [{cleanedNewNodeIp}] và dịch chuyển bộ đếm thành công.");
                    }
                    return (false, "❌ Cập nhật định tuyến thất bại do dữ liệu phiên đã bị thay đổi đồng thời.");
                }
                else
                {
                    var tran = db.CreateTransaction();
                    tran.AddCondition(Condition.KeyExists(sessionKey));

                    _ = tran.KeyExpireAsync(sessionKey, SESSION_TTL);
                    _ = tran.KeyExpireAsync(GetRedisCountKey(oldNodeIp), SESSION_TTL);

                    bool committed = await tran.ExecuteAsync();
                    if (committed)
                    {
                        _ = Task.Run(() => ReconcileSessionCountAsync());
                    }
                    return (committed, committed ? "⏱️ Gia hạn TTL User Session và bộ đếm thành công (60s)." : "❌ Gia hạn TTL thất bại do phiên làm việc đã bị xóa trước đó.");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateUserSessionKeyAsync(string oldUsername, string newUsername)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(oldUsername) || string.IsNullOrWhiteSpace(newUsername))
                {
                    return (false, "⚠️ Dữ liệu Username đầu vào không hợp lệ.");
                }

                var db = AppContext.RedisDB;
                string oldKey = GetRedisKey(oldUsername);
                string newKey = GetRedisKey(newUsername);

                var oldIpValue = await db.StringGetAsync(oldKey);
                if (!oldIpValue.HasValue) return (false, "❌ Không tìm thấy Session của tài khoản cũ.");

                var tran = db.CreateTransaction();
                tran.AddCondition(Condition.KeyExists(oldKey));
                tran.AddCondition(Condition.KeyNotExists(newKey));

                _ = tran.KeyDeleteAsync(oldKey);
                _ = tran.StringSetAsync(newKey, oldIpValue, SESSION_TTL);
                _ = tran.KeyExpireAsync(GetRedisCountKey(oldIpValue.ToString()), SESSION_TTL);

                bool committed = await tran.ExecuteAsync();
                if (committed)
                {
                    _ = Task.Run(() => ReconcileSessionCountAsync());
                    return (true, "✅ Thay đổi định danh Key Username thành công.");
                }

                return (false, "❌ Đổi định danh thất bại: Tài khoản cũ đã đăng xuất hoặc tài khoản mới đã được đăng ký song song.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> DeleteUserSessionAsync(string? username = null, string? nodeIp = null, bool isDeleteAll = false)
        {
            try
            {
                var db = AppContext.RedisDB;

                if (isDeleteAll)
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "session:*";
                    var allKeys = new List<RedisKey>();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            if (!key.ToString().StartsWith("session:count:"))
                            {
                                allKeys.Add(key);
                            }
                        }
                    }

                    allKeys = allKeys.Distinct().ToList();
                    if (allKeys.Any()) await db.KeyDeleteAsync(allKeys.ToArray());
                    
                    await DeleteUserSessionCountAsync(isDeleteAll: true);
                    return (true, "🗑️ Đã giải phóng TOÀN BỘ User Sessions và dọn sạch các bộ đếm liên quan.");
                }

                if (!string.IsNullOrWhiteSpace(username))
                {
                    string sessionKey = GetRedisKey(username);
                    
                    while (true)
                    {
                        var nodeIpValue = await db.StringGetAsync(sessionKey);
                        if (!nodeIpValue.HasValue) return (true, "ℹ️ Không tìm thấy phiên làm việc để xóa.");
                        
                        string targetNodeIp = nodeIpValue.ToString();

                        var tran = db.CreateTransaction();
                        tran.AddCondition(Condition.StringEqual(sessionKey, targetNodeIp));

                        _ = tran.KeyDeleteAsync(sessionKey);
                        _ = tran.StringDecrementAsync(GetRedisCountKey(targetNodeIp));

                        bool committed = await tran.ExecuteAsync();
                        if (committed)
                        {
                            _ = Task.Run(() => ReconcileSessionCountAsync());
                            return (true, $"✅ Đã xóa Session của User [{username}] và cập nhật giảm bộ đếm Node [{targetNodeIp}].");
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(nodeIp))
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "session:*";
                    var nodeUserKeys = new List<RedisKey>();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            if (!key.ToString().StartsWith("session:count:"))
                            {
                                var val = await db.StringGetAsync(key);
                                if (val.HasValue && val.ToString().Equals(nodeIp.Trim(), StringComparison.OrdinalIgnoreCase))
                                {
                                    nodeUserKeys.Add(key);
                                }
                            }
                        }
                    }

                    nodeUserKeys = nodeUserKeys.Distinct().ToList();
                    if (nodeUserKeys.Any()) await db.KeyDeleteAsync(nodeUserKeys.ToArray());

                    await DeleteUserSessionCountAsync(nodeIp: nodeIp);
                    _ = Task.Run(() => ReconcileSessionCountAsync());
                    return (true, $"🗑️ Đã xóa sạch toàn bộ User Session của riêng Node [{nodeIp}].");
                }

                return (false, "⚠️ Yêu cầu xóa không hợp lệ.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<bool> ReconcileSessionCountAsync()
        {
            var db = AppContext.RedisDB;
            string lockKey = GetReconcileLockKey();
            string lockValue = Guid.NewGuid().ToString();

            try
            {
                bool hasLock = await db.StringSetAsync(lockKey, lockValue, LOCK_TTL, When.NotExists);
                if (!hasLock) return false;

                var endpoints = db.Multiplexer.GetEndPoints();
                var livingNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string nodePattern = "node:*";

                string nodeCountKey = "node:count";
                string nodeGeneratorKey = "node:id:generator";
                string nodeRecycledKey = "node:id:recycled";

                foreach (var endpoint in endpoints)
                {
                    var server = db.Multiplexer.GetServer(endpoint);
                    await foreach (var key in server.KeysAsync(database: db.Database, pattern: nodePattern))
                    {
                        string keyStr = key.ToString();
                        if (keyStr != nodeCountKey && keyStr != nodeGeneratorKey && keyStr != nodeRecycledKey)
                        {
                            string ipFromKey = keyStr.Substring(5);
                            livingNodes.Add(ipFromKey);
                        }
                    }
                }

                string sessionPattern = "session:*";
                var sessionKeys = new List<RedisKey>();
                var countKeysToCleanup = new List<RedisKey>();

                foreach (var endpoint in endpoints)
                {
                    var server = db.Multiplexer.GetServer(endpoint);
                    await foreach (var key in server.KeysAsync(database: db.Database, pattern: sessionPattern))
                    {
                        string keyStr = key.ToString();
                        if (!keyStr.StartsWith("session:count:"))
                        {
                            sessionKeys.Add(key);
                        }
                        else
                        {
                            countKeysToCleanup.Add(key);
                        }
                    }
                }

                sessionKeys = sessionKeys.Distinct().ToList();
                countKeysToCleanup = countKeysToCleanup.Distinct().ToList();

                var actualCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var keysToDelete = new List<RedisKey>();

                if (sessionKeys.Any())
                {
                    var readBatch = db.CreateBatch();
                    var tasks = sessionKeys.Select(k => new { Key = k, Task = readBatch.StringGetAsync(k) }).ToList();
                    readBatch.Execute();

                    await Task.WhenAll(tasks.Select(t => t.Task));

                    foreach (var item in tasks)
                    {
                        var val = item.Task.Result;
                        if (val.HasValue)
                        {
                            string targetNodeIp = val.ToString().Trim().ToLower();

                            if (!livingNodes.Contains(targetNodeIp))
                            {
                                keysToDelete.Add(item.Key);
                            }
                            else
                            {
                                if (actualCounts.ContainsKey(targetNodeIp))
                                    actualCounts[targetNodeIp]++;
                                else
                                    actualCounts[targetNodeIp] = 1;
                            }
                        }
                    }
                }

                if (!livingNodes.Any())
                {
                    if (countKeysToCleanup.Any())
                    {
                        keysToDelete.AddRange(countKeysToCleanup);
                    }
                }

                if (keysToDelete.Any())
                {
                    var deleteBatch = db.CreateBatch();
                    var deleteTasks = keysToDelete.Select(k => deleteBatch.KeyDeleteAsync(k)).ToList();
                    deleteBatch.Execute();
                    await Task.WhenAll(deleteTasks);
                }

                if (livingNodes.Any())
                {
                    foreach (var nodeIp in livingNodes)
                    {
                        string countKey = $"session:count:{nodeIp.ToLower()}";
                        if (actualCounts.TryGetValue(nodeIp, out long actualCount))
                        {
                            await db.StringSetAsync(countKey, actualCount, SESSION_TTL);
                        }
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                var currentLock = await db.StringGetAsync(lockKey);
                if (currentLock == lockValue)
                {
                    await db.KeyDeleteAsync(lockKey);
                }
            }
        }    
    }
}
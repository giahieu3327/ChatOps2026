using StackExchange.Redis;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.RedisService
{
    public static class RedisNodeService
    {
        private static bool _isNodeRegistered = false;
        private static string _lastRegisteredIp = string.Empty;
        private static readonly TimeSpan NODE_TTL = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan LOCK_TTL = TimeSpan.FromSeconds(10);

        private static string GetNodeKey(string nodeIp) =>
            $"node:{nodeIp.Trim().ToLower()}";

        private static string GetNodeCountKey() =>
            $"node:count";

        private static string GetNodeIdGeneratorKey() =>
            $"node:id:generator";

        private static string GetNodeIdRecycledKey() =>
            $"node:id:recycled";

        private static string GetReconcileLockKey() =>
            $"lock:node:reconcile";

        public static async Task<long> GetNodeCountAsync()
        {
            try
            {
                var db = AppContext.RedisDB;
                var value = await db.StringGetAsync(GetNodeCountKey());
                return value.HasValue ? (long)value : -1;
            }
            catch { return -1; }
        }

        public static async Task<(bool success, string message)> InsertNodeCountAsync()
        {
            try
            {
                await AppContext.RedisDB.StringSetAsync(GetNodeCountKey(), 0, NODE_TTL, When.NotExists);
                return (true, "Success");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateNodeCountValueAsync(bool isIncrement = true, long? exactCount = null, bool isUpdateTTL = false)
        {
            try
            {
                var db = AppContext.RedisDB;
                string countKey = GetNodeCountKey();

                if (isUpdateTTL)
                {
                    bool isApplied = await db.KeyExpireAsync(countKey, NODE_TTL);
                    return (isApplied, isApplied ? "⏱️ Gia hạn TTL Node Count thành công (60s)." : "⚠️ Khóa bộ đếm không tồn tại hoặc lỗi gia hạn.");
                }

                if (exactCount.HasValue)
                {
                    if (exactCount.Value <= 0)
                    {
                        await DeleteNodeCountAsync();
                        return (true, "0 (Key Deleted)");
                    }

                    await db.StringSetAsync(countKey, exactCount.Value, NODE_TTL);
                    return (true, exactCount.Value.ToString());
                }

                if (isIncrement)
                {
                    long updatedVal = await db.StringIncrementAsync(countKey);
                    await db.KeyExpireAsync(countKey, NODE_TTL);
                    return (true, updatedVal.ToString());
                }
                else
                {
                    // Tối ưu hóa an toàn đồng thời: Sử dụng Transaction bọc quá trình giảm để tránh đọc giá trị cũ sai lệch
                    var tran = db.CreateTransaction();
                    var decTask = tran.StringDecrementAsync(countKey);
                    
                    if (await tran.ExecuteAsync())
                    {
                        long updatedVal = await decTask;
                        if (updatedVal <= 0)
                        {
                            await DeleteNodeCountAsync();
                            return (true, "0 (Key Automatically Deleted)");
                        }
                        
                        await db.KeyExpireAsync(countKey, NODE_TTL);
                        return (true, updatedVal.ToString());
                    }
                    return (false, "❌ Xung đột khi cập nhật bộ đếm Node.");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateNodeCountKeyAsync()
        {
            try
            {
                var db = AppContext.RedisDB;
                bool isApplied = await db.KeyExpireAsync(GetNodeCountKey(), NODE_TTL);
                return (isApplied, isApplied ? "⏱️ Gia hạn TTL Node Count thành công (60s)." : "⚠️ Khóa bộ đếm Node không tồn tại để làm mới TTL.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> DeleteNodeCountAsync()
        {
            try
            {
                bool isDeleted = await AppContext.RedisDB.KeyDeleteAsync(GetNodeCountKey());
                return (true, isDeleted ? "Success" : "Key not found");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, Dictionary<string, string> nodes)> GetNodeAsync(string? nodeIp = null, bool isGetAll = false)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var db = AppContext.RedisDB;

                if (!isGetAll)
                {
                    if (string.IsNullOrWhiteSpace(nodeIp)) return (false, result);

                    var value = await db.StringGetAsync(GetNodeKey(nodeIp));
                    if (value.HasValue)
                    {
                        result.Add(nodeIp.Trim(), value.ToString());
                        return (true, result);
                    }
                    return (false, result);
                }
                else
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "node:*";
                    var allKeys = new List<RedisKey>();

                    string countKey = GetNodeCountKey();
                    string generatorKey = GetNodeIdGeneratorKey();
                    string recycledKey = GetNodeIdRecycledKey();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            string keyStr = key.ToString();
                            if (keyStr != countKey && keyStr != generatorKey && keyStr != recycledKey)
                            {
                                allKeys.Add(key);
                            }
                        }
                    }

                    allKeys = allKeys.Distinct().ToList();
                    if (!allKeys.Any()) return (true, result);

                    // Thay giải pháp Task.WhenAll(Task.Run) tốn thread-pool bằng Batch tối ưu hóa pipeline của StackExchange.Redis
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
                            string extractedIp = keyStr.StartsWith("node:") ? keyStr.Substring(5) : keyStr;
                            result[extractedIp] = value.ToString();
                        }
                    }
                    return (true, result);
                }
            }
            catch { return (false, result); }
        }

        public static async Task<(bool success, string message)> InsertNodeAsync(string nodeIp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Dữ liệu đầu vào không hợp lệ.");

                var db = AppContext.RedisDB;
                string nodeKey = GetNodeKey(nodeIp);
                string countKey = GetNodeCountKey();
                string generatorKey = GetNodeIdGeneratorKey();
                string recycledKey = GetNodeIdRecycledKey();

                // Kiểm tra sơ bộ sự tồn tại trước khi khởi tạo Transaction
                var currentVal = await db.StringGetAsync(nodeKey);
                if (currentVal.HasValue)
                {
                    await db.KeyExpireAsync(nodeKey, NODE_TTL);
                    _ = Task.Run(() => ReconcileNodeCountAsync());
                    AppContext.ServerID = currentVal.ToString();
                    return (true, $"🔄 Node [{nodeIp}] đã tồn tại với NodeId [{currentVal}]. Đã làm mới trạng thái gia hạn TTL.");
                }

                while (true)
                {
                    long uniqueNodeId;
                    bool isFromRecycled = false;
                    await ReconcileNodeCountAsync();

                    // Thử rút ID tái sử dụng ra trước
                    var recycledId = await db.SetPopAsync(recycledKey);

                    if (recycledId.HasValue)
                    {
                        uniqueNodeId = (long)recycledId;
                        isFromRecycled = true;
                    }
                    else
                    {
                        uniqueNodeId = await db.StringIncrementAsync(generatorKey);
                    }

                    var tran = db.CreateTransaction();
                    tran.AddCondition(Condition.KeyNotExists(nodeKey));

                    _ = tran.StringSetAsync(nodeKey, uniqueNodeId, NODE_TTL);
                    _ = tran.StringIncrementAsync(countKey);
                    _ = tran.KeyExpireAsync(countKey, NODE_TTL);

                    bool committed = await tran.ExecuteAsync();

                    if (committed)
                    {
                        _ = Task.Run(() => ReconcileNodeCountAsync());
                        AppContext.ServerID = uniqueNodeId.ToString();
                        return (true, $"✅ Đăng ký Node mới thành công. NodeIP: {nodeIp} -> NodeId cấp phát: {uniqueNodeId}");
                    }

                    // Nếu Transaction thất bại (do node khác giành mất IP này trước), trả ID về Set ngay lập tức
                    if (isFromRecycled)
                    {
                        await db.SetAddAsync(recycledKey, uniqueNodeId);
                    }

                    currentVal = await db.StringGetAsync(nodeKey);
                    if (currentVal.HasValue)
                    {
                        _ = Task.Run(() => ReconcileNodeCountAsync());
                        AppContext.ServerID = currentVal.ToString();
                        return (true, $"🔄 Cấu hình song song hoàn tất. Node [{nodeIp}] đã sở hữu NodeId [{currentVal}].");
                    }
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateNodeValueAsync(string nodeIp, string? newValue = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Địa chỉ Node IP không hợp lệ.");

                var db = AppContext.RedisDB;
                string redisKey = GetNodeKey(nodeIp);

                if (newValue != null)
                {
                    await db.StringSetAsync(redisKey, newValue.Trim(), NODE_TTL);
                    _ = Task.Run(() => ReconcileNodeCountAsync());
                    return (true, $"🔄 Đã cập nhật lại thông tin NodeId mới cho Node [{nodeIp}].");
                }
                else
                {
                    bool isApplied = await db.KeyExpireAsync(redisKey, NODE_TTL);
                    if (isApplied)
                    {
                        _ = Task.Run(() => ReconcileNodeCountAsync());
                        return (true, "⏱️ Gia hạn TTL Node Heartbeat thành công (60s).");
                    }
                    return (false, "⚠️ Khóa dữ liệu Node không tồn tại để làm mới TTL.");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateNodeKeyAsync(string oldNodeIp, string newNodeIp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(oldNodeIp) || string.IsNullOrWhiteSpace(newNodeIp))
                {
                    return (false, "⚠️ Dữ liệu IP đầu vào không hợp lệ.");
                }

                var db = AppContext.RedisDB;
                string oldKey = GetNodeKey(oldNodeIp);
                string newKey = GetNodeKey(newNodeIp);

                // Đọc giá trị CŨ trước khi tạo phiên giao dịch nhằm đảm bảo tính nguyên tử hóa khi dịch chuyển
                var currentValue = await db.StringGetAsync(oldKey);
                if (!currentValue.HasValue)
                {
                    return (false, "❌ Thao tác thất bại: Node cũ không tồn tại.");
                }

                var tran = db.CreateTransaction();
                tran.AddCondition(Condition.KeyExists(oldKey));
                tran.AddCondition(Condition.KeyNotExists(newKey));

                // Thực hiện cả XÓA key cũ và TẠO key mới nguyên tử bên trong một Transaction duy nhất
                _ = tran.KeyDeleteAsync(oldKey);
                _ = tran.StringSetAsync(newKey, currentValue, NODE_TTL);

                bool committed = await tran.ExecuteAsync();

                if (!committed)
                {
                    return (false, "❌ Thao tác thất bại: Dữ liệu thay đổi đồng thời hoặc Node mới đã bị trùng lặp.");
                }

                _ = Task.Run(() => ReconcileNodeCountAsync());
                return (true, "✅ Đổi định danh Key Node IP thành công và giữ nguyên định danh NodeId ban đầu.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> DeleteNodeAsync(string nodeIp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Node IP không hợp lệ.");

                var db = AppContext.RedisDB;
                string nodeKey = GetNodeKey(nodeIp);
                string countKey = GetNodeCountKey();
                string recycledKey = GetNodeIdRecycledKey();

                var nodeIdValue = await db.StringGetAsync(nodeKey);
                if (!nodeIdValue.HasValue)
                {
                    _ = Task.Run(() => ReconcileNodeCountAsync());
                    return (true, "ℹ️ Không tìm thấy khóa của Node cần xóa hoặc Node đã bị xóa trước đó.");
                }

                var tran = db.CreateTransaction();
                tran.AddCondition(Condition.KeyExists(nodeKey));

                _ = tran.KeyDeleteAsync(nodeKey);
                _ = tran.StringDecrementAsync(countKey);
                _ = tran.SetAddAsync(recycledKey, nodeIdValue); // Đẩy ngược ID vào bộ thu hồi nguyên tử ngay trong transaction

                bool committed = await tran.ExecuteAsync();

                if (committed)
                {
                    _ = Task.Run(() => ReconcileNodeCountAsync());
                    return (true, $"✅ Đã xóa hoàn toàn trạng thái Node [{nodeIp}], đưa NodeId [{nodeIdValue}] vào kho tái sử dụng.");
                }

                return (false, "⚠️ Thao tác xóa thất bại do xung đột dữ liệu đồng thời.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<bool> ReconcileNodeCountAsync()
        {
            var db = AppContext.RedisDB;
            string lockKey = GetReconcileLockKey();
            string lockValue = Guid.NewGuid().ToString();

            try
            {
                bool hasLock = await db.StringSetAsync(lockKey, lockValue, LOCK_TTL, When.NotExists);
                if (!hasLock) return false;

                var endpoints = db.Multiplexer.GetEndPoints();
                string pattern = "node:*";
                
                var activeNodeIds = new HashSet<long>();
                long totalActiveNodes = 0;

                string countKey = GetNodeCountKey();
                string generatorKey = GetNodeIdGeneratorKey();
                string recycledKey = GetNodeIdRecycledKey();

                foreach (var endpoint in endpoints)
                {
                    var server = db.Multiplexer.GetServer(endpoint);
                    await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                    {
                        string keyStr = key.ToString();
                        if (keyStr != countKey && keyStr != generatorKey && keyStr != recycledKey)
                        {
                            totalActiveNodes++;
                            var val = await db.StringGetAsync(key);
                            if (val.HasValue && long.TryParse(val, out long id))
                            {
                                activeNodeIds.Add(id);
                            }
                        }
                    }
                }

                if (totalActiveNodes <= 0)
                {
                    await DeleteNodeCountAsync();
                }
                else
                {
                    await db.StringSetAsync(countKey, totalActiveNodes, NODE_TTL);
                }

                var maxIdVal = await db.StringGetAsync(generatorKey);
                if (!maxIdVal.HasValue) return true;
                
                long maxId = (long)maxIdVal;
                if (maxId <= 0) return true;

                var recycledMembers = await db.SetMembersAsync(recycledKey);
                var recycledIds = recycledMembers
                    .Where(m => m.HasValue)
                    .Select(m => (long)m)
                    .ToHashSet();

                var missingIds = new List<RedisValue>();
                for (long i = 1; i <= maxId; i++)
                {
                    if (!activeNodeIds.Contains(i) && !recycledIds.Contains(i))
                    {
                        missingIds.Add(i);
                    }
                }

                if (missingIds.Any())
                {
                    await db.SetAddAsync(recycledKey, missingIds.ToArray());
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
        public static async Task SyncNodeHeartbeatAsync()
        {
            string localIp = AppContext.ServerIP;

            if (string.IsNullOrEmpty(localIp))
            {
                Console.WriteLine("⚠️ [Cluster Sync] Không gán được địa chỉ IPv4 hợp lệ của Node. Bỏ qua chu kỳ.");
                return;
            }

            // TÌNH HUỐNG 1: Đã đăng ký nhưng IP máy chủ bị đổi đột ngột giữa chừng
            if (_isNodeRegistered && !string.IsNullOrEmpty(_lastRegisteredIp) && _lastRegisteredIp != localIp)
            {
                Console.WriteLine($"⚠️ [Cluster Sync] Phát hiện đổi IP từ {_lastRegisteredIp} sang {localIp}. Tiến hành dịch chuyển Key định danh...");
                
                await UpdateNodeKeyAsync(_lastRegisteredIp, localIp);
                await UpdateNodeValueAsync(localIp);

                AppContext.ServerIP = localIp;
                _lastRegisteredIp = localIp;
                
                Console.WriteLine($"✅ [Cluster Sync] Cập nhật IP định danh Node thành công sang: {localIp}");
            }
            // TÌNH HUỐNG 2: Ứng dụng mới bật, thực hiện đăng ký phát đầu tiên
            else if (!_isNodeRegistered)
            {
                Console.WriteLine($"🌐 [Cluster Sync] Thực hiện cấu hình ban đầu trên Redis cho Node: {localIp}");
                
                (bool success, string result) = await InsertNodeAsync(localIp);
                Console.WriteLine(result);

                AppContext.ServerIP = localIp;
                _lastRegisteredIp = localIp;
                _isNodeRegistered = true;
            }
            // TÌNH HUỐNG 3: Chu kỳ Heartbeat định kỳ (Gửi mỗi 30 giây để duy trì TTL 60 giây trên Redis)
            else
            {
                await UpdateNodeValueAsync(localIp);
            }
        }


        public static async Task ShutdownNodeClusterAsync()
        {
            string localIp = AppContext.ServerIP;
            if (string.IsNullOrEmpty(localIp)) localIp = _lastRegisteredIp;

            Console.WriteLine($"🛑 [Shutdown-Job] Tiến hành thu hồi đăng ký định danh của Node: {localIp}");

            try
            {
                (bool success, string result) = await DeleteNodeAsync(localIp);
                Console.WriteLine(result);

                _isNodeRegistered = false;
                _lastRegisteredIp = string.Empty;
                Console.WriteLine("✅ [Shutdown-Job] Đã gỡ bỏ định danh Node thành công khỏi cụm Cluster.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Shutdown-Job Error] Lỗi phát sinh khi thu hồi dữ liệu đăng ký Node: {ex.Message}");
            }
        }
    }
}
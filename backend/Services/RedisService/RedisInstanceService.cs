using ChatOps.Services.DockerService.Read;
using StackExchange.Redis;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.RedisService
{
    public static class RedisInstanceService
    {
        private static readonly TimeSpan INSTANCE_TTL = TimeSpan.FromMinutes(1);

        private static string GetRedisKey(string nodeIp) =>
            $"instance:{nodeIp.Trim().ToLower()}";

        private static string GetRedisCountKey(string nodeIp) =>
            $"instance:count:{nodeIp.Trim().ToLower()}";

        public static async Task<(bool success, Dictionary<string, string> counts)> GetInstanceCountAsync(string? nodeIp = null, bool isGetAll = false)
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
                    string pattern = "instance:count:*";
                    var allKeys = new HashSet<RedisKey>();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            allKeys.Add(key);
                        }
                    }

                    if (!allKeys.Any()) return (true, result);

                    foreach (var key in allKeys)
                    {
                        var value = await db.StringGetAsync(key);
                        if (value.HasValue)
                        {
                            string keyStr = key.ToString();
                            string extractedIp = keyStr.StartsWith("instance:count:") ? keyStr.Substring(15) : keyStr;
                            result[extractedIp] = value.ToString();
                        }
                    }
                    return (true, result);
                }
            }
            catch { return (false, result); }
        }

        public static async Task<(bool success, string message)> InsertInstanceCountAsync(string nodeIp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Node IP không hợp lệ.");

                string countKey = GetRedisCountKey(nodeIp);
                bool created = await AppContext.RedisDB.StringSetAsync(countKey, 0, INSTANCE_TTL, When.NotExists);
                if (!created)
                {
                    await AppContext.RedisDB.KeyExpireAsync(countKey, INSTANCE_TTL);
                }
                return (true, "Success");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateInstanceCountValueAsync(string nodeIp, bool isIncrement = true, long? exactCount = null, bool isUpdateTtlOnly = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Node IP không hợp lệ.");

                var db = AppContext.RedisDB;
                string countKey = GetRedisCountKey(nodeIp);

                if (isUpdateTtlOnly)
                {
                    if (!await db.KeyExistsAsync(countKey)) return (false, "❌ Khóa bộ đếm không tồn tại để gia hạn TTL.");
                    await db.KeyExpireAsync(countKey, INSTANCE_TTL);
                    return (true, "⏱️ Đã gia hạn TTL cho bộ đếm (60s).");
                }

                if (exactCount.HasValue)
                {
                    if (exactCount.Value <= 0)
                    {
                        await DeleteInstanceCountAsync(nodeIp);
                        return (true, "0 (Key Deleted)");
                    }

                    await db.StringSetAsync(countKey, exactCount.Value, INSTANCE_TTL);
                    return (true, exactCount.Value.ToString());
                }

                long updatedVal;
                if (isIncrement)
                {
                    updatedVal = await db.StringIncrementAsync(countKey);
                    await db.KeyExpireAsync(countKey, INSTANCE_TTL);
                }
                else
                {
                    var tran = db.CreateTransaction();
                    tran.AddCondition(Condition.KeyExists(countKey));

                    var currentAsync = db.StringGetAsync(countKey);
                    if (await tran.ExecuteAsync())
                    {
                        var current = await currentAsync;
                        if (!current.HasValue) return (false, "⚠️ Khóa bộ đếm instance không tồn tại.");

                        updatedVal = (long)current - 1;
                        if (updatedVal <= 0)
                        {
                            await DeleteInstanceCountAsync(nodeIp);
                            return (true, "0 (Key Automatically Deleted)");
                        }
                        else
                        {
                            await db.StringSetAsync(countKey, updatedVal, INSTANCE_TTL);
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

        public static async Task<(bool success, string message)> UpdateInstanceCountKeyAsync(string oldNodeIp, string newNodeIp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(oldNodeIp) || string.IsNullOrWhiteSpace(newNodeIp))
                {
                    return (false, "⚠️ Dữ liệu IP đầu vào không hợp lệ.");
                }

                var db = AppContext.RedisDB;
                string oldCountKey = GetRedisCountKey(oldNodeIp);
                string newCountKey = GetRedisCountKey(newNodeIp);

                var tran = db.CreateTransaction();
                tran.AddCondition(Condition.KeyExists(oldCountKey));
                tran.AddCondition(Condition.KeyNotExists(newCountKey));

                var currentValAsync = db.StringGetAsync(oldCountKey);

                if (await tran.ExecuteAsync())
                {
                    var currentVal = await currentValAsync;
                    var batch = db.CreateBatch();
                    _ = batch.KeyDeleteAsync(oldCountKey);
                    _ = batch.StringSetAsync(newCountKey, currentVal, INSTANCE_TTL);
                    batch.Execute();

                    return (true, "✅ Đổi định danh Key bộ đếm Instance thành công.");
                }
                else
                {
                    return (false, "❌ Không tìm thấy bộ đếm cũ hoặc bộ đếm mới đã tồn tại (Xung đột định danh).");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> DeleteInstanceCountAsync(string? nodeIp = null, bool isDeleteAll = false)
        {
            try
            {
                var db = AppContext.RedisDB;

                if (isDeleteAll)
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "instance:count:*";
                    var allKeys = new HashSet<RedisKey>();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            allKeys.Add(key);
                        }
                    }

                    if (!allKeys.Any()) return (true, "ℹ️ Không có bộ đếm nào để xóa.");

                    await db.KeyDeleteAsync(allKeys.ToArray());
                    return (true, "🗑️ Đã xóa toàn bộ các khóa bộ đếm instance:count.");
                }

                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Node IP không hợp lệ.");
                bool isDeleted = await db.KeyDeleteAsync(GetRedisCountKey(nodeIp));
                return (true, isDeleted ? "Success" : "Key not found");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, Dictionary<string, string> nodes)> GetInstanceAsync(string? instanceName = null, string? nodeIp = null, bool isGetAll = false)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var db = AppContext.RedisDB;

                if (!string.IsNullOrWhiteSpace(nodeIp) && string.IsNullOrWhiteSpace(instanceName))
                {
                    string instanceKey = GetRedisKey(nodeIp);
                    if (await db.KeyExistsAsync(instanceKey))
                    {
                        var setMembers = await db.SetMembersAsync(instanceKey);
                        var validInstances = setMembers
                            .Select(m => m.ToString())
                            .Where(m => !string.Equals(m, "init:placeholder", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        if (validInstances.Any())
                        {
                            result.Add(nodeIp.Trim(), string.Join("\n", validInstances));
                            return (true, result);
                        }
                    }
                    return (false, result);
                }

                var endpoints = db.Multiplexer.GetEndPoints();
                string pattern = "instance:*";
                var allKeys = new HashSet<RedisKey>();

                foreach (var endpoint in endpoints)
                {
                    var server = db.Multiplexer.GetServer(endpoint);
                    await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                    {
                        if (!key.ToString().StartsWith("instance:count:", StringComparison.OrdinalIgnoreCase))
                        {
                            allKeys.Add(key);
                        }
                    }
                }

                if (!allKeys.Any()) return (true, result);

                foreach (var key in allKeys)
                {
                    var setMembers = await db.SetMembersAsync(key);
                    if (setMembers.Length == 0) continue;

                    string keyStr = key.ToString();
                    string extractedNodeIp = keyStr.StartsWith("instance:", StringComparison.OrdinalIgnoreCase) ? keyStr.Substring(9) : keyStr;

                    var validMembers = setMembers
                        .Select(m => m.ToString())
                        .Where(m => !string.Equals(m, "init:placeholder", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (!validMembers.Any()) continue;

                    if (!string.IsNullOrWhiteSpace(instanceName))
                    {
                        string searchName = instanceName.Trim();

                        var matchedMember = validMembers.FirstOrDefault(m =>
                            m.Equals(searchName, StringComparison.OrdinalIgnoreCase));

                        if (matchedMember != null)
                        {
                            if (result.ContainsKey(extractedNodeIp))
                            {
                                var existing = result[extractedNodeIp].Split('\n').ToList();
                                if (!existing.Contains(matchedMember, StringComparer.OrdinalIgnoreCase))
                                {
                                    result[extractedNodeIp] = result[extractedNodeIp] + "\n" + matchedMember;
                                }
                            }
                            else
                            {
                                result[extractedNodeIp] = matchedMember;
                            }
                        }
                    }
                    else if (isGetAll)
                    {
                        result[extractedNodeIp] = string.Join("\n", validMembers);
                    }
                }

                return (true, result);
            }
            catch { return (false, result); }
        }

        public static async Task<(bool success, string message)> InsertInstanceAsync(string nodeIp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Dữ liệu Node IP không hợp lệ.");

                var db = AppContext.RedisDB;
                string instanceKey = GetRedisKey(nodeIp);

                var tran = db.CreateTransaction();
                tran.AddCondition(Condition.KeyNotExists(instanceKey));
                _ = tran.SetAddAsync(instanceKey, "init:placeholder");
                _ = tran.KeyExpireAsync(instanceKey, INSTANCE_TTL);

                if (await tran.ExecuteAsync())
                {
                    return (true, "✅ Khởi tạo phân vùng Instance thành công.");
                }
                return (false, "⚠️ Cấu hình Node IP đã tồn tại trên Redis.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateInstanceValueAsync(string nodeIp, string? instanceName = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Dữ liệu Node IP không hợp lệ.");

                var db = AppContext.RedisDB;
                string instanceKey = GetRedisKey(nodeIp);

                if (!string.IsNullOrWhiteSpace(instanceName))
                {
                    string formattedInstance = instanceName.Trim();

                    string countKey = GetRedisCountKey(nodeIp);
                    if (!await db.KeyExistsAsync(countKey))
                    {
                        await InsertInstanceCountAsync(nodeIp);
                    }

                    var existingMembers = await db.SetMembersAsync(instanceKey);
                    var oldMember = existingMembers
                        .Select(m => m.ToString())
                        .FirstOrDefault(m => m.Equals(formattedInstance, StringComparison.OrdinalIgnoreCase));

                    var tran = db.CreateTransaction();

                    _ = tran.SetAddAsync(instanceKey, formattedInstance);
                    _ = tran.SetRemoveAsync(instanceKey, "init:placeholder");
                    _ = tran.KeyExpireAsync(instanceKey, INSTANCE_TTL);

                    if (await tran.ExecuteAsync())
                    {
                        await UpdateInstanceCountValueAsync(nodeIp, isUpdateTtlOnly: true);

                        if (oldMember == null)
                        {
                            await UpdateInstanceCountValueAsync(nodeIp, isIncrement: true);
                        }

                        return (true, $"✅ Đã ghi nhận Instance [{formattedInstance}] tại Node [{nodeIp}] và gia hạn TTL (60s).");
                    }
                    else
                    {
                        return (false, "⚡ Xung đột dữ liệu khi ghi nhận thông tin Instance.");
                    }
                }
                else
                {
                    if (!await db.KeyExistsAsync(instanceKey))
                    {
                        return (false, "❌ Cấu hình Node không tồn tại để gia hạn TTL.");
                    }

                    var batch = db.CreateBatch();
                    _ = batch.KeyExpireAsync(instanceKey, INSTANCE_TTL);
                    batch.Execute();

                    await UpdateInstanceCountValueAsync(nodeIp, isUpdateTtlOnly: true);
                    return (true, "⏱️ Gia hạn TTL cho Node và bộ đếm Instance liên quan thành công (60s).");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateInstanceKeyAsync(string oldNodeIp, string newNodeIp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(oldNodeIp) || string.IsNullOrWhiteSpace(newNodeIp))
                {
                    return (false, "⚠️ Node IP đầu vào không hợp lệ.");
                }

                var db = AppContext.RedisDB;
                string oldKey = GetRedisKey(oldNodeIp);
                string newKey = GetRedisKey(newNodeIp);

                var tran = db.CreateTransaction();
                tran.AddCondition(Condition.KeyExists(oldKey));
                tran.AddCondition(Condition.KeyNotExists(newKey));

                var membersAsync = db.SetMembersAsync(oldKey);

                if (await tran.ExecuteAsync())
                {
                    var members = await membersAsync;
                    await UpdateInstanceCountKeyAsync(oldNodeIp, newNodeIp);

                    var batch = db.CreateBatch();
                    _ = batch.KeyDeleteAsync(oldKey);

                    if (members.Length > 0)
                    {
                        _ = batch.SetAddAsync(newKey, members);
                    }
                    else
                    {
                        _ = batch.SetAddAsync(newKey, "init:placeholder");
                    }

                    _ = batch.KeyExpireAsync(newKey, INSTANCE_TTL);
                    batch.Execute();

                    await UpdateInstanceCountValueAsync(newNodeIp, isUpdateTtlOnly: true);
                    return (true, "✅ Thay đổi định danh định tuyến Node IP thành công.");
                }
                else
                {
                    return (false, "❌ Không tìm thấy Node cũ hoặc định danh Node mới bị trùng lặp.");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> DeleteInstanceAsync(string? instanceName = null, string? nodeIp = null, bool isDeleteAll = false)
        {
            try
            {
                var db = AppContext.RedisDB;

                if (isDeleteAll)
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "instance:*";
                    var allKeys = new HashSet<RedisKey>();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            if (!key.ToString().StartsWith("instance:count:", StringComparison.OrdinalIgnoreCase))
                            {
                                allKeys.Add(key);
                            }
                        }
                    }

                    if (allKeys.Any()) await db.KeyDeleteAsync(allKeys.ToArray());
                    await DeleteInstanceCountAsync(isDeleteAll: true);
                    return (true, "🗑️ Đã giải phóng TOÀN BỘ cấu hình Instance và dọn sạch các bộ đếm liên quan.");
                }

                if (!string.IsNullOrWhiteSpace(nodeIp) && string.IsNullOrWhiteSpace(instanceName))
                {
                    string instanceKey = GetRedisKey(nodeIp);
                    var members = await db.SetMembersAsync(instanceKey);

                    var validMembers = members
                        .Select(m => m.ToString())
                        .Where(m => !string.Equals(m, "init:placeholder", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (!validMembers.Any())
                    {
                        await db.KeyDeleteAsync(instanceKey);
                        await DeleteInstanceCountAsync(nodeIp: nodeIp);
                        return (true, "✅ Đã giải phóng phân vùng Node trống.");
                    }

                    await db.KeyDeleteAsync(instanceKey);
                    await DeleteInstanceCountAsync(nodeIp: nodeIp);
                    return (true, $"✅ Đã xóa hoàn toàn toàn bộ danh sách instance của Node [{nodeIp}] và cập nhật lại bộ đếm.");
                }

                if (!string.IsNullOrWhiteSpace(instanceName))
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "instance:*";
                    string searchName = instanceName.Trim();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            if (!key.ToString().StartsWith("instance:count:", StringComparison.OrdinalIgnoreCase))
                            {
                                string keyStr = key.ToString();
                                string extractedNodeIp = keyStr.StartsWith("instance:", StringComparison.OrdinalIgnoreCase) ? keyStr.Substring(9) : keyStr;

                                if (!string.IsNullOrWhiteSpace(nodeIp) && !extractedNodeIp.Equals(nodeIp.Trim(), StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                var currentMembers = await db.SetMembersAsync(key);
                                var targetFullString = currentMembers
                                    .Select(m => m.ToString())
                                    .FirstOrDefault(m => m.Equals(searchName, StringComparison.OrdinalIgnoreCase));

                                if (targetFullString != null)
                                {
                                    var tran = db.CreateTransaction();
                                    _ = tran.SetRemoveAsync(key, targetFullString);

                                    if (await tran.ExecuteAsync())
                                    {
                                        await UpdateInstanceCountValueAsync(extractedNodeIp, isIncrement: false);

                                        var remainingMembers = await db.SetMembersAsync(key);
                                        var validRemaining = remainingMembers
                                            .Select(m => m.ToString())
                                            .Where(m => !string.Equals(m, "init:placeholder", StringComparison.OrdinalIgnoreCase))
                                            .ToList();

                                        if (!validRemaining.Any())
                                        {
                                            await db.KeyDeleteAsync(key);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    return (true, $"🗑️ Đã dọn sạch Instance [{searchName}] ra khỏi hệ thống.");
                }

                return (false, "⚠️ Yêu cầu xóa không hợp lệ.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }
        public static async Task SyncInstancesAsync()
        {
            string localIp = AppContext.ServerIP;
            if (string.IsNullOrEmpty(localIp))
            {
                Console.WriteLine("⚠️ [Instance Sync] Không tìm thấy IP hợp lệ từ AppContext. Bỏ qua chu kỳ quét này.");
                return;
            }

            // =====================================================
            // BƯỚC 1: TRÍCH XUẤT DANH SÁCH INSTANCE TỪ METADATA DOCKER SERVICES
            // =====================================================
            Dictionary<string, ServiceConfig> freshContainers = await DockerReadMetadata.GetLatestDockerServicesAsync();
            if (freshContainers == null) freshContainers = new Dictionary<string, ServiceConfig>();

            List<string?> freshInstances = freshContainers
                .Select(kvp => kvp.Value?.AppService?.Trim())
                .Where(appService => !string.IsNullOrWhiteSpace(appService))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // =====================================================
            // BƯỚC 2: ĐỐI CHIẾU VỚI REDIS ĐỂ ĐỒNG BỘ DANH SÁCH INSTANCE
            // =====================================================
            (bool getSuccess, Dictionary<string, string> redisData) = await GetInstanceAsync(instanceName: null, nodeIp: localIp, isGetAll: false);

            List<string> redisInstances = new List<string>();
            if (getSuccess && redisData != null && redisData.TryGetValue(localIp.Trim(), out string? instancesStr) && !string.IsNullOrEmpty(instancesStr))
            {
                redisInstances = instancesStr.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(member => member.Trim())
                    .ToList();
            }

            // Trường hợp Redis trống hoặc rớt kết nối nhưng thực tế đang chạy Instance -> Tái nạp lại toàn bộ
            if (redisInstances.Count == 0 && freshInstances.Count > 0)
            {
                Console.WriteLine($"🆕 [Instance Sync] Khởi tạo / Tái đồng bộ toàn bộ danh sách {freshInstances.Count} nhóm Instance lên Redis...");

                foreach (var instanceName in freshInstances)
                {
                    await UpdateInstanceValueAsync(localIp, instanceName);
                }

                (bool countSuccess, string countMsg) = await UpdateInstanceCountValueAsync(localIp, isIncrement: false, exactCount: freshInstances.Count);
                Console.WriteLine($"   ➔ Thiết lập bộ đếm số lượng Instance thực tế: {countMsg}");
            }
            else
            {
                // Kiểm tra và bổ sung các Instance mới vừa xuất hiện từ Metadata
                foreach (var instanceName in freshInstances)
                {
                    bool isExisted = redisInstances.Contains(instanceName, StringComparer.OrdinalIgnoreCase);

                    if (!isExisted)
                    {
                        Console.WriteLine($"➕ [Instance Sync] Phát hiện nhóm Instance mới '{instanceName}'. Tiến hành đăng ký vào phân vùng Redis...");
                        (bool insertSuccess, string msg) = await UpdateInstanceValueAsync(localIp, instanceName);
                        Console.WriteLine($"   ➔ {msg}");
                    }
                }

                // Gia hạn thời gian duy trì định kỳ cho phân vùng Node và bộ đếm tương ứng
                await UpdateInstanceValueAsync(localIp, instanceName: null);
            }

            // =====================================================
            // BƯỚC 3: DỌN DẸP KHỎI REDIS NẾU INSTANCE KHÔNG CÒN HIỆN DIỆN TRONG METADATA
            // =====================================================
            (_, redisData) = await GetInstanceAsync(instanceName: null, nodeIp: localIp, isGetAll: false);

            if (redisData != null && redisData.TryGetValue(localIp.Trim(), out string? updatedInstancesStr) && !string.IsNullOrEmpty(updatedInstancesStr))
            {
                var currentRedisInstances = updatedInstancesStr.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(member => member.Trim())
                    .ToList();

                foreach (var redisInstanceName in currentRedisInstances)
                {
                    if (!freshInstances.Contains(redisInstanceName, StringComparer.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"🗑️ [Instance Sync] Nhóm Instance '{redisInstanceName}' không còn tồn tại trong cấu hình Metadata. Thu hồi khỏi Redis...");
                        (bool deleteSuccess, string msg) = await DeleteInstanceAsync(instanceName: redisInstanceName, nodeIp: localIp, isDeleteAll: false);
                        Console.WriteLine($"   ➔ {msg}");
                    }
                }
            }
        }

        public static async Task ShutdownInstancesClusterAsync()
        {
            string localIp = AppContext.ServerIP;
            if (string.IsNullOrEmpty(localIp))
            {
                Console.WriteLine("⚠️ [Shutdown-Job] Không tìm thấy IP hợp lệ từ AppContext để dọn dẹp Instance.");
                return;
            }

            Console.WriteLine($"🛑 [Shutdown-Job] Tiến hành gỡ bỏ hoàn toàn danh sách Instance & Bộ đếm thuộc Node: [{localIp}]");

            try
            {
                (bool success, string result) = await DeleteInstanceAsync(instanceName: null, nodeIp: localIp, isDeleteAll: false);
                Console.WriteLine($"✅ [Shutdown-Job] Kết quả giải phóng hạ tầng Instance: {result}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Shutdown-Job Error] Gặp lỗi khi dọn dẹp dữ liệu Instance: {ex.Message}");
            }

            Console.WriteLine("✅ [Shutdown-Job] Hoàn tất tiến trình giải phóng danh sách Instance của Node.");
        }
    }
}
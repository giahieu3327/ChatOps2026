using ChatOps.Models;
using ChatOps.Services.DockerService.Read;
using StackExchange.Redis;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.RedisService
{
    public static class RedisContainerService
    {
        private static readonly TimeSpan CONTAINER_TTL = TimeSpan.FromMinutes(1);

        private static string GetRedisKey(string nodeIp) =>
            $"container:{nodeIp.Trim().ToLower()}";

        private static string GetRedisCountKey(string nodeIp) =>
            $"container:count:{nodeIp.Trim().ToLower()}";

        public static async Task<(bool success, Dictionary<string, string> counts)> GetContainerCountAsync(string? nodeIp = null, bool isGetAll = false)
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
                    string pattern = "container:count:*";
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
                            string extractedIp = keyStr.StartsWith("container:count:") ? keyStr.Substring(16) : keyStr;
                            result[extractedIp] = value.ToString();
                        }
                    }
                    return (true, result);
                }
            }
            catch { return (false, result); }
        }

        public static async Task<(bool success, string message)> InsertContainerCountAsync(string nodeIp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Node IP không hợp lệ.");

                string countKey = GetRedisCountKey(nodeIp);
                bool created = await AppContext.RedisDB.StringSetAsync(countKey, 0, CONTAINER_TTL, When.NotExists);
                if (!created)
                {
                    await AppContext.RedisDB.KeyExpireAsync(countKey, CONTAINER_TTL);
                }
                return (true, "Success");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateContainerCountValueAsync(string nodeIp, bool isIncrement = true, long? exactCount = null, bool isUpdateTtlOnly = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Node IP không hợp lệ.");

                var db = AppContext.RedisDB;
                string countKey = GetRedisCountKey(nodeIp);

                if (isUpdateTtlOnly)
                {
                    if (!await db.KeyExistsAsync(countKey)) return (false, "❌ Khóa bộ đếm không tồn tại để gia hạn TTL.");
                    await db.KeyExpireAsync(countKey, CONTAINER_TTL);
                    return (true, "⏱️ Đã gia hạn TTL cho bộ đếm (60s).");
                }

                if (exactCount.HasValue)
                {
                    if (exactCount.Value <= 0)
                    {
                        await DeleteContainerCountAsync(nodeIp);
                        return (true, "0 (Key Deleted)");
                    }

                    await db.StringSetAsync(countKey, exactCount.Value, CONTAINER_TTL);
                    return (true, exactCount.Value.ToString());
                }

                long updatedVal;
                if (isIncrement)
                {
                    updatedVal = await db.StringIncrementAsync(countKey);
                    await db.KeyExpireAsync(countKey, CONTAINER_TTL);
                }
                else
                {
                    var tran = db.CreateTransaction();
                    tran.AddCondition(Condition.KeyExists(countKey));

                    var currentAsync = db.StringGetAsync(countKey);
                    if (await tran.ExecuteAsync())
                    {
                        var current = await currentAsync;
                        if (!current.HasValue) return (false, "⚠️ Khóa bộ đếm container không tồn tại.");

                        updatedVal = (long)current - 1;
                        if (updatedVal <= 0)
                        {
                            await DeleteContainerCountAsync(nodeIp);
                            return (true, "0 (Key Automatically Deleted)");
                        }
                        else
                        {
                            await db.StringSetAsync(countKey, updatedVal, CONTAINER_TTL);
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

        public static async Task<(bool success, string message)> UpdateContainerCountKeyAsync(string oldNodeIp, string newNodeIp)
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
                    _ = batch.StringSetAsync(newCountKey, currentVal, CONTAINER_TTL);
                    batch.Execute();

                    return (true, "✅ Đổi định danh Key bộ đếm Container thành công.");
                }
                else
                {
                    return (false, "❌ Không tìm thấy bộ đếm cũ hoặc bộ đếm mới đã tồn tại (Xung đột định danh).");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> DeleteContainerCountAsync(string? nodeIp = null, bool isDeleteAll = false)
        {
            try
            {
                var db = AppContext.RedisDB;

                if (isDeleteAll)
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "container:count:*";
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
                    return (true, "🗑️ Đã xóa toàn bộ các khóa bộ đếm container:count.");
                }

                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Node IP không hợp lệ.");
                bool isDeleted = await db.KeyDeleteAsync(GetRedisCountKey(nodeIp));
                return (true, isDeleted ? "Success" : "Key not found");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, Dictionary<string, string> nodes)> GetContainerAsync(string? containerName = null, string? nodeIp = null, bool isGetAll = false)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var db = AppContext.RedisDB;

                if (!string.IsNullOrWhiteSpace(nodeIp) && string.IsNullOrWhiteSpace(containerName))
                {
                    string containerKey = GetRedisKey(nodeIp);
                    if (await db.KeyExistsAsync(containerKey))
                    {
                        var setMembers = await db.SetMembersAsync(containerKey);
                        var validContainers = setMembers
                            .Select(m => m.ToString())
                            .Where(m => !string.Equals(m, "init:placeholder", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        if (validContainers.Any())
                        {
                            result.Add(nodeIp.Trim(), string.Join("\n", validContainers));
                            return (true, result);
                        }
                    }
                    return (false, result);
                }

                var endpoints = db.Multiplexer.GetEndPoints();
                string pattern = "container:*";
                var allKeys = new HashSet<RedisKey>();

                foreach (var endpoint in endpoints)
                {
                    var server = db.Multiplexer.GetServer(endpoint);
                    await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                    {
                        if (!key.ToString().StartsWith("container:count:", StringComparison.OrdinalIgnoreCase))
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
                    string extractedNodeIp = keyStr.StartsWith("container:", StringComparison.OrdinalIgnoreCase) ? keyStr.Substring(10) : keyStr;

                    var validMembers = setMembers
                        .Select(m => m.ToString())
                        .Where(m => !string.Equals(m, "init:placeholder", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (!validMembers.Any()) continue;

                    if (!string.IsNullOrWhiteSpace(containerName))
                    {
                        string searchName = containerName.Trim();

                        var matchedMember = validMembers.FirstOrDefault(m =>
                            m.Split('|')[0].Equals(searchName, StringComparison.OrdinalIgnoreCase));

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

        public static async Task<(bool success, string message)> InsertContainerAsync(string nodeIp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Dữ liệu Node IP không hợp lệ.");

                var db = AppContext.RedisDB;
                string containerKey = GetRedisKey(nodeIp);

                var tran = db.CreateTransaction();
                tran.AddCondition(Condition.KeyNotExists(containerKey));
                _ = tran.SetAddAsync(containerKey, "init:placeholder");
                _ = tran.KeyExpireAsync(containerKey, CONTAINER_TTL);

                if (await tran.ExecuteAsync())
                {
                    return (true, "✅ Khởi tạo phân vùng Container thành công.");
                }
                return (false, "⚠️ Cấu hình Node IP đã tồn tại trên Redis.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateContainerValueAsync(string nodeIp, string? containerName = null, string? containerexport = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Dữ liệu Node IP không hợp lệ.");

                var db = AppContext.RedisDB;
                string containerKey = GetRedisKey(nodeIp);

                if (!string.IsNullOrWhiteSpace(containerName))
                {
                    string targetName = containerName.Trim();
                    string formattedContainer = targetName;

                    if (!string.IsNullOrWhiteSpace(containerexport))
                    {
                        formattedContainer += $"|{containerexport.Trim()}";
                    }

                    string countKey = GetRedisCountKey(nodeIp);
                    if (!await db.KeyExistsAsync(countKey))
                    {
                        await InsertContainerCountAsync(nodeIp);
                    }

                    var existingMembers = await db.SetMembersAsync(containerKey);
                    var oldMember = existingMembers
                        .Select(m => m.ToString())
                        .FirstOrDefault(m => m.Split('|')[0].Equals(targetName, StringComparison.OrdinalIgnoreCase));

                    var tran = db.CreateTransaction();

                    if (oldMember != null && !string.Equals(oldMember, formattedContainer, StringComparison.OrdinalIgnoreCase))
                    {
                        _ = tran.SetRemoveAsync(containerKey, oldMember);
                    }

                    _ = tran.SetAddAsync(containerKey, formattedContainer);
                    _ = tran.SetRemoveAsync(containerKey, "init:placeholder");
                    _ = tran.KeyExpireAsync(containerKey, CONTAINER_TTL);

                    if (await tran.ExecuteAsync())
                    {
                        await UpdateContainerCountValueAsync(nodeIp, isUpdateTtlOnly: true);

                        if (oldMember == null)
                        {
                            await UpdateContainerCountValueAsync(nodeIp, isIncrement: true);
                        }

                        return (true, $"✅ Đã ghi nhận Container [{formattedContainer}] tại Node [{nodeIp}] và gia hạn TTL (60s).");
                    }
                    else
                    {
                        return (false, "⚡ Xung đột dữ liệu khi ghi nhận thông tin Container.");
                    }
                }
                else
                {
                    if (!await db.KeyExistsAsync(containerKey))
                    {
                        return (false, "❌ Cấu hình Node không tồn tại để gia hạn TTL.");
                    }

                    var batch = db.CreateBatch();
                    _ = batch.KeyExpireAsync(containerKey, CONTAINER_TTL);
                    batch.Execute();

                    await UpdateContainerCountValueAsync(nodeIp, isUpdateTtlOnly: true);
                    return (true, "⏱️ Gia hạn TTL cho Node và bộ đếm Container liên quan thành công (60s).");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateContainerKeyAsync(string oldNodeIp, string newNodeIp)
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
                    await UpdateContainerCountKeyAsync(oldNodeIp, newNodeIp);

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

                    _ = batch.KeyExpireAsync(newKey, CONTAINER_TTL);
                    batch.Execute();

                    await UpdateContainerCountValueAsync(newNodeIp, isUpdateTtlOnly: true);
                    return (true, "✅ Thay đổi định danh định tuyến Node IP thành công.");
                }
                else
                {
                    return (false, "❌ Không tìm thấy Node cũ hoặc định danh Node mới bị trùng lặp.");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> DeleteContainerAsync(string? containerName = null, string? nodeIp = null, bool isDeleteAll = false)
        {
            try
            {
                var db = AppContext.RedisDB;

                if (isDeleteAll)
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "container:*";
                    var allKeys = new HashSet<RedisKey>();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            if (!key.ToString().StartsWith("container:count:", StringComparison.OrdinalIgnoreCase))
                            {
                                allKeys.Add(key);
                            }
                        }
                    }

                    if (allKeys.Any()) await db.KeyDeleteAsync(allKeys.ToArray());
                    await DeleteContainerCountAsync(isDeleteAll: true);
                    return (true, "🗑️ Đã giải phóng TOÀN BỘ cấu hình Container và dọn sạch các bộ đếm liên quan.");
                }

                if (!string.IsNullOrWhiteSpace(nodeIp) && string.IsNullOrWhiteSpace(containerName))
                {
                    string containerKey = GetRedisKey(nodeIp);
                    var members = await db.SetMembersAsync(containerKey);

                    var validMembers = members
                        .Select(m => m.ToString())
                        .Where(m => !string.Equals(m, "init:placeholder", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (!validMembers.Any())
                    {
                        await db.KeyDeleteAsync(containerKey);
                        await DeleteContainerCountAsync(nodeIp: nodeIp);
                        return (true, "✅ Đã giải phóng phân vùng Node trống.");
                    }

                    await db.KeyDeleteAsync(containerKey);
                    await DeleteContainerCountAsync(nodeIp: nodeIp);
                    return (true, $"✅ Đã xóa hoàn toàn toàn bộ danh sách container của Node [{nodeIp}] và cập nhật lại bộ đếm.");
                }

                if (!string.IsNullOrWhiteSpace(containerName))
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "container:*";
                    string searchName = containerName.Trim();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            if (!key.ToString().StartsWith("container:count:", StringComparison.OrdinalIgnoreCase))
                            {
                                string keyStr = key.ToString();
                                string extractedNodeIp = keyStr.StartsWith("container:", StringComparison.OrdinalIgnoreCase) ? keyStr.Substring(10) : keyStr;

                                if (!string.IsNullOrWhiteSpace(nodeIp) && !extractedNodeIp.Equals(nodeIp.Trim(), StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                var currentMembers = await db.SetMembersAsync(key);
                                var targetFullString = currentMembers
                                    .Select(m => m.ToString())
                                    .FirstOrDefault(m => m.Split('|')[0].Equals(searchName, StringComparison.OrdinalIgnoreCase));

                                if (targetFullString != null)
                                {
                                    var tran = db.CreateTransaction();
                                    _ = tran.SetRemoveAsync(key, targetFullString);

                                    if (await tran.ExecuteAsync())
                                    {
                                        await UpdateContainerCountValueAsync(extractedNodeIp, isIncrement: false);

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

                    return (true, $"🗑️ Đã dọn sạch Container [{searchName}] ra khỏi hệ thống.");
                }

                return (false, "⚠️ Yêu cầu xóa không hợp lệ.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task SyncContainersAsync()
        {
            string localIp = AppContext.ServerIP;
            if (string.IsNullOrEmpty(localIp))
            {
                Console.WriteLine("⚠️ [Container Sync] Không tìm thấy IP hợp lệ từ AppContext. Bỏ qua lượt đồng bộ này.");
                return;
            }

            // =====================================================
            // BƯỚC 1: QUÉT VÀ LẤY DANH SÁCH CONTAINERS THỰC TẾ TỪ DOCKER ENGINE
            // =====================================================
            List<DockerContainer> freshContainers = await DockerReadContainer.GetContainersAsync(showAll: true);
            if (freshContainers == null) freshContainers = new List<DockerContainer>();

            var freshContainerDict = freshContainers
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .ToDictionary(c => c.Name.Trim(), c => c, StringComparer.OrdinalIgnoreCase);

            List<string> freshContainerNames = freshContainerDict.Keys.ToList();

            // =====================================================
            // BƯỚC 2: ĐỐI CHIẾU VỚI REDIS ĐỂ ĐỒNG BỘ DANH SÁCH (HỖ TRỢ CONTAINER MỚI & UPDATE)
            // =====================================================
            (bool getSuccess, Dictionary<string, string> redisData) = await GetContainerAsync(containerName: null, nodeIp: localIp, isGetAll: false);

            List<string> redisRawMembers = new List<string>();
            if (getSuccess && redisData != null && redisData.TryGetValue(localIp.Trim(), out string? containersStr) && !string.IsNullOrEmpty(containersStr))
            {
                redisRawMembers = containersStr.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(member => member.Trim())
                    .ToList();
            }

            if (redisRawMembers.Count == 0 && freshContainerNames.Count > 0)
            {
                Console.WriteLine($"🆕 [Container Sync] Khởi tạo / Tái đồng bộ toàn bộ danh sách {freshContainerNames.Count} container kèm cổng lên Redis...");

                foreach (var containerName in freshContainerNames)
                {
                    var containerObj = freshContainerDict[containerName];
                    await UpdateContainerValueAsync(localIp, containerName, containerexport: containerObj.OutPorts);
                }

                (bool countSuccess, string countMsg) = await UpdateContainerCountValueAsync(localIp, isIncrement: false, exactCount: freshContainerNames.Count);
                Console.WriteLine($"   ➔ Thiết lập bộ đếm số lượng thực tế: {countMsg}");
            }
            else
            {
                foreach (var containerName in freshContainerNames)
                {
                    var containerObj = freshContainerDict[containerName];
                    string currentOutPort = containerObj.OutPorts ?? string.Empty;

                    string? existingMember = redisRawMembers.FirstOrDefault(m => m.Split('|')[0].Equals(containerName, StringComparison.OrdinalIgnoreCase));

                    if (existingMember == null)
                    {
                        Console.WriteLine($"➕ [Container Sync] Phát hiện container mới '{containerName}' (Port: {currentOutPort}). Tiến hành đăng ký vào Redis...");
                        (bool insertSuccess, string msg) = await UpdateContainerValueAsync(localIp, containerName, containerexport: currentOutPort);
                        Console.WriteLine($"   ➔ {msg}");
                    }
                    else
                    {
                        string[] parts = existingMember.Split('|');
                        string redisPort = parts.Length > 1 ? parts[1] : string.Empty;

                        if (redisPort != currentOutPort)
                        {
                            Console.WriteLine($"🔄 [Container Sync] Phát hiện thay đổi cấu hình cổng tại '{containerName}' (Cũ: '{redisPort}' -> Mới: '{currentOutPort}'). Đang cập nhật lại Redis...");
                            await UpdateContainerValueAsync(localIp, containerName, containerexport: currentOutPort);
                        }
                    }
                }

                await UpdateContainerValueAsync(localIp, containerName: null);
            }

            // =====================================================
            // BƯỚC 3: DỌN DẸP CÁC CONTAINER NẾU KHÔNG CÒN TỒN TẠI TRÊN DOCKER ENGINE
            // =====================================================
            (_, redisData) = await GetContainerAsync(containerName: null, nodeIp: localIp, isGetAll: false);

            if (redisData != null && redisData.TryGetValue(localIp.Trim(), out string? updatedContainersStr) && !string.IsNullOrEmpty(updatedContainersStr))
            {
                var currentRedisMembers = updatedContainersStr.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(member => member.Trim())
                    .ToList();

                foreach (var redisMember in currentRedisMembers)
                {
                    string redisContainerName = redisMember.Split('|')[0];

                    if (!freshContainerNames.Contains(redisContainerName, StringComparer.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"🗑️ [Container Sync] Container '{redisContainerName}' không còn tồn tại dưới Docker Engine. Tiến hành thu hồi khỏi Redis...");
                        (bool deleteSuccess, string msg) = await DeleteContainerAsync(containerName: redisContainerName, nodeIp: localIp, isDeleteAll: false);
                        Console.WriteLine($"   ➔ {msg}");
                    }
                }
            }
        }
        public static async Task ShutdownContainersClusterAsync()
        {
            string localIp = AppContext.ServerIP;
            if (string.IsNullOrEmpty(localIp))
            {
                Console.WriteLine("⚠️ [Shutdown-Job] Không tìm thấy IP hợp lệ từ AppContext để dọn dẹp.");
                return;
            }

            Console.WriteLine($"🛑 [Shutdown-Job] Tiến hành gỡ bỏ hoàn toàn danh sách Container & Bộ đếm thuộc Node: [{localIp}]");

            try
            {
                (bool success, string result) = await DeleteContainerAsync(containerName: null, nodeIp: localIp, isDeleteAll: false);
                Console.WriteLine($"✅ [Shutdown-Job] Kết quả giải phóng hạ tầng Container: {result}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Shutdown-Job Error] Gặp lỗi khi dọn dẹp dữ liệu Container: {ex.Message}");
            }

            Console.WriteLine("✅ [Shutdown-Job] Hoàn tất tiến trình giải phóng danh sách Container của Node.");
        }
    }
}
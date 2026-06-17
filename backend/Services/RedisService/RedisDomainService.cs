using ChatOps.Services.DockerService.Read;
using StackExchange.Redis;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.RedisService
{
    public static class RedisDomainService
    {
        private static readonly TimeSpan DOMAIN_TTL = TimeSpan.FromMinutes(1);

        private static (string type, string cleanDomain) ParseInputDomain(string inputDomain)
        {
            inputDomain = inputDomain.Trim().ToLower();

            if (inputDomain.Contains(':'))
            {
                string[] parts = inputDomain.Split(':', 2);
                string type = parts[0];
                string cleanDomain = parts[1];

                if (type == "frontend" || type == "backend")
                {
                    return (type, cleanDomain);
                }
            }

            return ("service", inputDomain);
        }

        private static string GetDomainKey(string inputDomain, string nodeIp)
        {
            var (type, cleanDomain) = ParseInputDomain(inputDomain);
            nodeIp = nodeIp.Trim().ToLower();

            return $"domain:{type}:{cleanDomain}:{nodeIp}";
        }

        private static string GetDomainCountKey(string nodeIp) =>
            $"domain:count:{nodeIp.Trim().ToLower()}";

        public static async Task<(bool success, Dictionary<string, string> counts)> GetDomainCountAsync(string? nodeIp = null, bool isGetAll = false)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var db = AppContext.RedisDB;

                if (!isGetAll)
                {
                    if (string.IsNullOrWhiteSpace(nodeIp)) return (false, result);

                    string countKey = GetDomainCountKey(nodeIp);
                    var value = await db.StringGetAsync(countKey);
                    if (!value.HasValue) return (false, result);

                    result.Add(nodeIp.Trim(), value.ToString());
                    return (true, result);
                }
                else
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "domain:count:*";
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

                    foreach (var key in allKeys)
                    {
                        var value = await db.StringGetAsync(key);
                        if (value.HasValue)
                        {
                            string keyStr = key.ToString();
                            string extractedIp = keyStr.StartsWith("domain:count:") ? keyStr.Substring(13) : keyStr;
                            result[extractedIp] = value.ToString();
                        }
                    }
                    return (true, result);
                }
            }
            catch { return (false, result); }
        }

        public static async Task<(bool success, string message)> InsertDomainCountAsync(string nodeIp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Node IP không hợp lệ.");

                string countKey = GetDomainCountKey(nodeIp);
                var db = AppContext.RedisDB;

                var tran = db.CreateTransaction();
                tran.AddCondition(Condition.KeyNotExists(countKey));
                
                _ = tran.StringSetAsync(countKey, 0, DOMAIN_TTL);
                bool committed = await tran.ExecuteAsync();

                if (!committed) return (false, "⚠️ Bộ đếm Node đã tồn tại.");
                return (true, "Success");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateDomainCountValueAsync(string nodeIp, bool isIncrement = true, long? exactCount = null, bool isUpdateTTL = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nodeIp)) return (false, "⚠️ Node IP không hợp lệ.");

                var db = AppContext.RedisDB;
                string countKey = GetDomainCountKey(nodeIp);

                if (isUpdateTTL)
                {
                    bool exists = await db.KeyExpireAsync(countKey, DOMAIN_TTL);
                    if (!exists)
                    {
                        await db.StringSetAsync(countKey, 0, DOMAIN_TTL);
                    }
                    return (true, "⏱️ Đã cập nhật TTL (60s) cho bộ đếm Node.");
                }

                if (exactCount.HasValue)
                {
                    if (exactCount.Value <= 0)
                    {
                        await db.KeyDeleteAsync(countKey);
                        return (true, "0 (Key Deleted)");
                    }

                    await db.StringSetAsync(countKey, exactCount.Value, DOMAIN_TTL);
                    return (true, exactCount.Value.ToString());
                }

                if (isIncrement)
                {
                    long updatedVal = await db.StringIncrementAsync(countKey);
                    await db.KeyExpireAsync(countKey, DOMAIN_TTL);
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
                            await db.KeyDeleteAsync(countKey);
                            return (true, "0 (Key Automatically Deleted)");
                        }
                        await db.KeyExpireAsync(countKey, DOMAIN_TTL);
                        return (true, updatedVal.ToString());
                    }
                    return (false, "❌ Thao tác giảm bộ đếm bị xung đột.");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateDomainCountKeyAsync(string oldNodeIp, string newNodeIp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(oldNodeIp) || string.IsNullOrWhiteSpace(newNodeIp))
                {
                    return (false, "⚠️ Dữ liệu IP đầu vào không hợp lệ.");
                }

                var db = AppContext.RedisDB;
                string oldCountKey = GetDomainCountKey(oldNodeIp);
                string newCountKey = GetDomainCountKey(newNodeIp);

                var currentVal = await db.StringGetAsync(oldCountKey);
                if (!currentVal.HasValue) return (false, "❌ Không tìm thấy bộ đếm cũ.");

                var tran = db.CreateTransaction();
                tran.AddCondition(Condition.KeyExists(oldCountKey));
                tran.AddCondition(Condition.KeyNotExists(newCountKey));

                _ = tran.KeyDeleteAsync(oldCountKey);
                _ = tran.StringSetAsync(newCountKey, currentVal, DOMAIN_TTL);

                if (await tran.ExecuteAsync())
                {
                    return (true, "✅ Đổi định danh Key bộ đếm Domain thành công.");
                }
                return (false, "❌ Thay đổi định danh thất bại do xung đột trạng thái.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> DeleteDomainCountAsync(string? nodeIp = null, bool isDeleteAll = false)
        {
            try
            {
                var db = AppContext.RedisDB;
                var endpoints = db.Multiplexer.GetEndPoints();

                if (isDeleteAll)
                {
                    string pattern = "domain:count:*";
                    var allCountKeys = new List<RedisKey>();
                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            allCountKeys.Add(key);
                        }
                    }
                    if (allCountKeys.Any()) await db.KeyDeleteAsync(allCountKeys.ToArray());
                    return (true, "🗑️ Đã xóa toàn bộ khóa bộ đếm domain:count.");
                }

                if (!string.IsNullOrWhiteSpace(nodeIp))
                {
                    bool isDeleted = await db.KeyDeleteAsync(GetDomainCountKey(nodeIp));
                    return (true, isDeleted ? "Success" : "Key not found");
                }

                return (false, "⚠️ Node IP không hợp lệ.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, Dictionary<string, string> nodes)> GetDomainAsync(string? domain = null, string? nodeIp = null, bool isGetAll = false)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var db = AppContext.RedisDB;
                var endpoints = db.Multiplexer.GetEndPoints();
                var allKeys = new List<RedisKey>();

                string pattern = "domain:*:*:*";
                
                if (!string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(nodeIp))
                {
                    pattern = GetDomainKey(domain, nodeIp);
                }
                else if (!string.IsNullOrWhiteSpace(domain))
                {
                    var (type, cleanDomain) = ParseInputDomain(domain);
                    pattern = $"domain:{type}:{cleanDomain}:*";
                }
                else if (!string.IsNullOrWhiteSpace(nodeIp))
                {
                    pattern = $"domain:*:*:{nodeIp.Trim().ToLower()}";
                }

                foreach (var endpoint in endpoints)
                {
                    var server = db.Multiplexer.GetServer(endpoint);
                    await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                    {
                        string keyStr = key.ToString();
                        if (!keyStr.StartsWith("domain:count:"))
                        {
                            allKeys.Add(key);
                        }
                    }
                }

                allKeys = allKeys.Distinct().ToList();
                if (!allKeys.Any()) return (true, result);

                foreach (var key in allKeys)
                {
                    // SỬA ĐỔI: Đọc dữ liệu từ Set bằng SetMembersAsync thay vì StringGetAsync
                    var members = await db.SetMembersAsync(key);
                    if (members.Length == 0) continue;

                    string keyStr = key.ToString();
                    string[] parts = keyStr.Split(':');
                    
                    if (parts.Length == 4)
                    {
                        string type = parts[1];
                        string cleanDomain = parts[2];
                        string extractedIp = parts[3];

                        string originalFormatDomain = (type == "frontend" || type == "backend") 
                            ? $"{type}:{cleanDomain}" 
                            : cleanDomain;

                        // Gom toàn bộ Port trong Set để tạo chuỗi kết quả mong muốn
                        foreach (var member in members)
                        {
                            string port = member.ToString();
                            string endpointInfo = $"{extractedIp}:{port}";

                            if (result.ContainsKey(originalFormatDomain))
                            {
                                // Tránh append lặp nếu chạy trong Cluster quét trùng key
                                if (!result[originalFormatDomain].Split('\n').Contains(endpointInfo))
                                {
                                    result[originalFormatDomain] += "\n" + endpointInfo;
                                }
                            }
                            else
                            {
                                result[originalFormatDomain] = endpointInfo;
                            }
                        }
                    }
                }

                return (true, result);
            }
            catch { return (false, result); }
        }

        public static async Task<(bool success, string message)> InsertDomainAsync(string domain, string endpointIpAndPort)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(domain)) return (false, "⚠️ Tên Domain không hợp lệ.");
                if (string.IsNullOrWhiteSpace(endpointIpAndPort)) return (false, "⚠️ Endpoint (IP:Port) không được để trống.");

                var db = AppContext.RedisDB;
                string formattedEndpoint = endpointIpAndPort.Trim();
                
                string nodeIp = formattedEndpoint;
                string port = "80";

                if (formattedEndpoint.Contains(':'))
                {
                    string[] tokens = formattedEndpoint.Split(':');
                    nodeIp = tokens[0];
                    port = tokens[1];
                }

                string domainKey = GetDomainKey(domain, nodeIp);
                string countKey = GetDomainCountKey(nodeIp);

                // SỬA ĐỔI: Kiểm tra xem Port này đã tồn tại trong Set của Domain chưa
                bool isPortExists = await db.SetContainsAsync(domainKey, port);
                if (isPortExists)
                {
                    return (false, $"⚠️ Cấu hình Port [{port}] cho Domain [{domain}] trên Node [{nodeIp}] đã tồn tại.");
                }

                var tran = db.CreateTransaction();
                // Sử dụng SetAddAsync để đẩy Port vào Set thay vì cấu trúc String cũ
                _ = tran.SetAddAsync(domainKey, port);
                _ = tran.KeyExpireAsync(domainKey, DOMAIN_TTL);
                
                var incrTask = tran.StringIncrementAsync(countKey);
                _ = tran.KeyExpireAsync(countKey, DOMAIN_TTL);

                if (await tran.ExecuteAsync())
                {
                    _ = await incrTask;
                    return (true, $"✅ Thêm mới Port [{port}] cho Domain [{domain}] -> Node [{nodeIp}] thành công.");
                }

                return (false, $"❌ Không thể thêm cấu hình do xung đột thao tác trên Redis.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateDomainValueAsync(string domain, string? endpointIpAndPort = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(domain)) return (false, "⚠️ Tên Domain không hợp lệ.");

                var db = AppContext.RedisDB;

                if (!string.IsNullOrWhiteSpace(endpointIpAndPort))
                {
                    string formattedEndpoint = endpointIpAndPort.Trim();
                    string nodeIp = formattedEndpoint;
                    string port = "80";

                    if (formattedEndpoint.Contains(':'))
                    {
                        string[] tokens = formattedEndpoint.Split(':');
                        nodeIp = tokens[0];
                        port = tokens[1];
                    }

                    string domainKey = GetDomainKey(domain, nodeIp);

                    var tran = db.CreateTransaction();
                    tran.AddCondition(Condition.KeyExists(domainKey));
                    _ = tran.KeyExpireAsync(domainKey, DOMAIN_TTL);

                    if (await tran.ExecuteAsync())
                    {
                        await UpdateDomainCountValueAsync(nodeIp, isUpdateTTL: true);
                        return (true, $"✅ Gia hạn thành công TTL (60s) cho Domain [{domain}] trên Node [{nodeIp}].");
                    }
                    return (false, $"❌ Cấu hình Domain [{domain}] trên Node [{nodeIp}] không tồn tại.");
                }
                else
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    var (type, cleanDomain) = ParseInputDomain(domain);
                    string pattern = $"domain:{type}:{cleanDomain}:*";

                    var targetKeys = new List<RedisKey>();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            targetKeys.Add(key);
                        }
                    }

                    targetKeys = targetKeys.Distinct().ToList();
                    if (!targetKeys.Any()) return (false, "❌ Cấu hình Domain không tồn tại để gia hạn TTL.");

                    foreach (var key in targetKeys)
                    {
                        var tran = db.CreateTransaction();
                        tran.AddCondition(Condition.KeyExists(key));
                        _ = tran.KeyExpireAsync(key, DOMAIN_TTL);

                        if (await tran.ExecuteAsync())
                        {
                            string keyStr = key.ToString();
                            string[] parts = keyStr.Split(':');
                            
                            if (parts.Length == 4)
                            {
                                string associatedNodeIp = parts[3];
                                await UpdateDomainCountValueAsync(associatedNodeIp, isUpdateTTL: true);
                            }
                        }
                    }

                    return (true, $"⏱️ Gia hạn thành công TTL (60s) cho toàn bộ Node đang xử lý Domain [{domain}].");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> UpdateDomainKeyAsync(string oldDomain, string newDomain)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(oldDomain) || string.IsNullOrWhiteSpace(newDomain))
                {
                    return (false, "⚠️ Tên Domain đầu vào không hợp lệ.");
                }

                var db = AppContext.RedisDB;
                var endpoints = db.Multiplexer.GetEndPoints();
                
                var (oldType, oldCleanDomain) = ParseInputDomain(oldDomain);
                string pattern = $"domain:{oldType}:{oldCleanDomain}:*";

                var oldKeys = new List<RedisKey>();

                foreach (var endpoint in endpoints)
                {
                    var server = db.Multiplexer.GetServer(endpoint);
                    await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                    {
                        oldKeys.Add(key);
                    }
                }

                oldKeys = oldKeys.Distinct().ToList();
                if (!oldKeys.Any()) return (false, "❌ Không tìm thấy thông tin định tuyến cũ nào của Domain.");

                int migratedCount = 0;
                foreach (var oldKey in oldKeys)
                {
                    // SỬA ĐỔI: Lấy tất cả Port ra khỏi Set cũ để dời sang Set mới
                    var ports = await db.SetMembersAsync(oldKey);
                    if (ports.Length == 0) continue;

                    string oldKeyStr = oldKey.ToString();
                    string[] parts = oldKeyStr.Split(':');
                    if (parts.Length != 4) continue;

                    string nodeIp = parts[3];
                    string newKey = GetDomainKey(newDomain, nodeIp);

                    var tran = db.CreateTransaction();
                    tran.AddCondition(Condition.KeyExists(oldKey));
                    tran.AddCondition(Condition.KeyNotExists(newKey));

                    _ = tran.KeyDeleteAsync(oldKey);
                    foreach (var port in ports)
                    {
                        _ = tran.SetAddAsync(newKey, port);
                    }
                    _ = tran.KeyExpireAsync(newKey, DOMAIN_TTL);

                    if (await tran.ExecuteAsync())
                    {
                        await UpdateDomainCountValueAsync(nodeIp, isUpdateTTL: true);
                        migratedCount++;
                    }
                }

                return (true, $"✅ Thay đổi định danh hệ thống cho ({migratedCount}) bản ghi định tuyến Domain thành công.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool success, string message)> DeleteDomainAsync(string? domain = null, string? nodeIp = null, bool isDeleteAll = false)
        {
            try
            {
                var db = AppContext.RedisDB;

                if (isDeleteAll)
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = "domain:*:*:*";
                    var allKeys = new List<RedisKey>();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            if (!key.ToString().StartsWith("domain:count:"))
                            {
                                allKeys.Add(key);
                            }
                        }
                    }

                    allKeys = allKeys.Distinct().ToList();
                    if (allKeys.Any()) await db.KeyDeleteAsync(allKeys.ToArray());

                    await DeleteDomainCountAsync(isDeleteAll: true);
                    return (true, "🗑️ Đã giải phóng TOÀN BỘ cấu hình Domain và dọn sạch các bộ đếm liên quan.");
                }

                // SỬA ĐỔI: Xử lý xóa Port cụ thể, hoặc toàn bộ cấu hình Set của cặp Domain & NodeIp
                if (!string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(nodeIp))
                {
                    // Nếu chuỗi nodeIp được truyền vào có chứa dấu ':' (Ví dụ "192.168.1.8:7000") thì xóa port cụ thể đó
                    string targetIp = nodeIp;
                    string? targetPort = null;
                    if (nodeIp.Contains(':'))
                    {
                        string[] tokens = nodeIp.Split(':');
                        targetIp = tokens[0];
                        targetPort = tokens[1];
                    }

                    string domainKey = GetDomainKey(domain, targetIp);
                    string countKey = GetDomainCountKey(targetIp);
                    
                    if (!await db.KeyExistsAsync(domainKey)) return (false, "❌ Bản ghi định tuyến không tồn tại.");

                    if (!string.IsNullOrWhiteSpace(targetPort))
                    {
                        // Xóa 1 Port cụ thể ra khỏi Set
                        var tran = db.CreateTransaction();
                        _ = tran.SetRemoveAsync(domainKey, targetPort);
                        var decTask = tran.StringDecrementAsync(countKey);

                        if (await tran.ExecuteAsync())
                        {
                            // Kiểm tra nếu Set rỗng hoàn toàn thì dọn luôn Key
                            long remSize = await db.SetLengthAsync(domainKey);
                            if (remSize == 0) await db.KeyDeleteAsync(domainKey);
                            
                            long updatedVal = await decTask;
                            if (updatedVal <= 0) await db.KeyDeleteAsync(countKey);
                            else await db.KeyExpireAsync(countKey, DOMAIN_TTL);

                            return (true, $"✅ Đã xóa Port [{targetPort}] của Domain [{domain}] trên Node [{targetIp}].");
                        }
                    }
                    else
                    {
                        // Xóa TOÀN BỘ Port thuộc Key này
                        long currentPortsCount = await db.SetLengthAsync(domainKey);
                        var tran = db.CreateTransaction();
                        _ = tran.KeyDeleteAsync(domainKey);
                        var decTask = tran.StringDecrementAsync(countKey, currentPortsCount);

                        if (await tran.ExecuteAsync())
                        {
                            long updatedVal = await decTask;
                            if (updatedVal <= 0) await db.KeyDeleteAsync(countKey);
                            else await db.KeyExpireAsync(countKey, DOMAIN_TTL);

                            return (true, $"✅ Đã xóa toàn bộ cấu hình của Domain [{domain}] trên Node [{targetIp}].");
                        }
                    }
                    return (false, "❌ Thao tác xóa bị xung đột.");
                }

                if (!string.IsNullOrWhiteSpace(domain))
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    var (type, cleanDomain) = ParseInputDomain(domain);
                    string pattern = $"domain:{type}:{cleanDomain}:*";
                        
                    var targetKeys = new List<RedisKey>();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            targetKeys.Add(key);
                        }
                    }

                    targetKeys = targetKeys.Distinct().ToList();
                    if (!targetKeys.Any()) return (false, "❌ Cấu hình Domain không tồn tại.");

                    foreach (var key in targetKeys)
                    {
                        string keyStr = key.ToString();
                        string[] parts = keyStr.Split(':');

                        if (parts.Length == 4)
                        {
                            string associatedNodeIp = parts[3];
                            string countKey = GetDomainCountKey(associatedNodeIp);
                            long currentPortsCount = await db.SetLengthAsync(key);

                            var tran = db.CreateTransaction();
                            _ = tran.KeyDeleteAsync(key);
                            var decTask = tran.StringDecrementAsync(countKey, currentPortsCount);

                            if (await tran.ExecuteAsync())
                            {
                                long updatedVal = await decTask;
                                if (updatedVal <= 0) await db.KeyDeleteAsync(countKey);
                                else await db.KeyExpireAsync(countKey, DOMAIN_TTL);
                            }
                        }
                    }

                    return (true, $"✅ Đã xóa hoàn toàn cấu hình Domain [{domain}] trên tất cả các Node.");
                }

                if (!string.IsNullOrWhiteSpace(nodeIp))
                {
                    var endpoints = db.Multiplexer.GetEndPoints();
                    string pattern = $"domain:*:*:{nodeIp.Trim().ToLower()}";
                    var targetKeys = new List<RedisKey>();

                    foreach (var endpoint in endpoints)
                    {
                        var server = db.Multiplexer.GetServer(endpoint);
                        await foreach (var key in server.KeysAsync(database: db.Database, pattern: pattern))
                        {
                            targetKeys.Add(key);
                        }
                    }

                    targetKeys = targetKeys.Distinct().ToList();
                    long affectedDomainsCount = targetKeys.Count;

                    if (targetKeys.Any())
                    {
                        await db.KeyDeleteAsync(targetKeys.ToArray());
                    }

                    await DeleteDomainCountAsync(nodeIp: nodeIp);
                    return (true, $"🗑️ Đã dọn sạch các Endpoint thuộc Node [{nodeIp}] ra khỏi ({affectedDomainsCount}) Domain.");
                }

                return (false, "⚠️ Yêu cầu xóa không hợp lệ.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task SyncServicesAsync()
        {
            string localIp = AppContext.ServerIP;
            if (string.IsNullOrEmpty(localIp))
            {
                Console.WriteLine("⚠️ [Service Sync] Không tìm thấy IP hợp lệ từ AppContext. Bỏ qua chu kỳ quét này.");
                return;
            }

            // Cấu hình định dạng domain đầu vào mới theo đúng thiết kế hệ thống
            string frontendDomain = $"frontend:{AppContext.ServerDomain}".Trim().ToLower();
            string backendDomain = $"backend:{AppContext.ServerDomain}".Trim().ToLower();

            string frontendValue = $"{localIp}:80";
            string backendValue = $"{localIp}:5000";

            // =====================================================
            // 1. DUY TRÌ VÀ KIỂM TRA CORE FRONTEND CLUSTER
            // =====================================================
            var redisFrontend = await GetDomainAsync(domain: frontendDomain, nodeIp: localIp);
            bool isFrontendNodeExist = false;

            if (redisFrontend.success && redisFrontend.nodes != null && redisFrontend.nodes.TryGetValue(frontendDomain, out string? frontendEndpointsStr))
            {
                if (!string.IsNullOrEmpty(frontendEndpointsStr) && frontendEndpointsStr.Contains(frontendValue))
                {
                    isFrontendNodeExist = true;
                }
            }

            if (isFrontendNodeExist)
            {
                await UpdateDomainValueAsync(frontendDomain);
            }
            else
            {
                await InsertDomainAsync(frontendDomain, frontendValue);
                Console.WriteLine($"➕ [Service Sync] Thêm mới/Bổ sung Node {frontendValue} vào {frontendDomain}");
            }

            // =====================================================
            // 2. DUY TRÌ VÀ KIỂM TRA CORE BACKEND CLUSTER
            // =====================================================
            var redisBackend = await GetDomainAsync(domain: backendDomain, nodeIp: localIp);
            bool isBackendNodeExist = false;

            if (redisBackend.success && redisBackend.nodes != null && redisBackend.nodes.TryGetValue(backendDomain, out string? backendEndpointsStr))
            {
                if (!string.IsNullOrEmpty(backendEndpointsStr) && backendEndpointsStr.Contains(backendValue))
                {
                    isBackendNodeExist = true;
                }
            }

            if (isBackendNodeExist)
            {
                await UpdateDomainValueAsync(backendDomain);
            }
            else
            {
                await InsertDomainAsync(backendDomain, backendValue);
                Console.WriteLine($"➕ [Service Sync] Thêm mới/Bổ sung Node {backendValue} vào {backendDomain}");
            }

            // =====================================================
            // BƯỚC 1: QUÉT VÀ LẤY DANH SÁCH DỊCH VỤ THỰC TẾ TỪ DOCKER
            // =====================================================
            Dictionary<string, ServiceConfig> freshDockerServicesRaw = await DockerReadMetadata.GetLatestDockerServicesAsync();
            if (freshDockerServicesRaw == null) freshDockerServicesRaw = new Dictionary<string, ServiceConfig>();

            Dictionary<string, ServiceConfig> freshDockerServices = freshDockerServicesRaw
                .ToDictionary(k => k.Key.Trim().ToLower(), v => v.Value);

            // =====================================================
            // BƯỚC 2: ĐỐI CHIẾU VỚI REDIS ĐỂ CẬP NHẬT HOẶC THÊM MỚI
            // =====================================================
            foreach (var dockerServ in freshDockerServices)
            {
                string serviceDomain = dockerServ.Key; 
                ServiceConfig currentDockerConfig = dockerServ.Value;
                
                var currentConnections = currentDockerConfig.Ports
                    .OrderBy(p => p)
                    .Select(port => $"{localIp}:{port}")
                    .ToList();

                var redisState = await GetDomainAsync(domain: serviceDomain, nodeIp: localIp);

                if (!redisState.success || redisState.nodes == null || !redisState.nodes.ContainsKey(serviceDomain))
                {
                    Console.WriteLine($"🆕 [Service Sync] Phát hiện dịch vụ mới '{serviceDomain}'. Tiến hành đăng ký lên Redis... ");
                    foreach (var endpoint in currentConnections)
                    {
                        await InsertDomainAsync(serviceDomain, endpoint);
                    }
                }
                else
                {
                    string rawRedisEndpoints = redisState.nodes.TryGetValue(serviceDomain, out var eps) ? eps : string.Empty;
                    var redisEndpointsSorted = rawRedisEndpoints
                        .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(c => c.Trim().ToLower())
                        .OrderBy(c => c)
                        .ToList();

                    var currentNodesSorted = currentConnections
                        .Select(c => c.Trim().ToLower())
                        .OrderBy(c => c)
                        .ToList();

                    if (!currentNodesSorted.SequenceEqual(redisEndpointsSorted))
                    {
                        Console.WriteLine($"🔄 [Service Sync] Phát hiện biến động cấu hình Port của '{serviceDomain}'. Tiến hành tái cấu trúc định tuyến...");
                        await DeleteDomainAsync(domain: serviceDomain, nodeIp: localIp);
                        foreach (var endpoint in currentConnections)
                        {
                            await InsertDomainAsync(serviceDomain, endpoint);
                        }
                    }
                    else
                    {
                        await UpdateDomainValueAsync(serviceDomain);
                    }
                }
            }

            // =====================================================
            // BƯỚC 3: QUÉT REDIS ĐỂ DỌN DẸP DỊCH VỤ ĐÃ BỊ XÓA KHỎI DOCKER
            // =====================================================
            var scanResult = await GetDomainAsync(nodeIp: localIp);
            
            if (scanResult.success && scanResult.nodes != null)
            {
                foreach (var item in scanResult.nodes)
                {
                    string redisDomain = item.Key.Trim().ToLower();

                    if (string.Equals(redisDomain, frontendDomain, StringComparison.OrdinalIgnoreCase) || 
                        string.Equals(redisDomain, backendDomain, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!freshDockerServices.ContainsKey(redisDomain))
                    {
                        Console.WriteLine($"🗑️ [Service Sync] Dịch vụ '{redisDomain}' không còn tồn tại dưới Docker. Tiến hành thu hồi định tuyến...");
                        (bool deleteSuccess, string result) = await DeleteDomainAsync(domain: redisDomain, nodeIp: localIp);
                        Console.WriteLine($"   ➔ {result}");
                    }
                }
            }
        }
        public static async Task ShutdownServicesClusterAsync()
        {
            string localIp = AppContext.ServerIP;
            if (string.IsNullOrEmpty(localIp))
            {
                Console.WriteLine("⚠️ [Shutdown-Job] Không tìm thấy IP hợp lệ từ AppContext để thu hồi định tuyến.");
                return;
            }

            Console.WriteLine($"🛑 [Shutdown-Job] Tiến hành thu hồi toàn bộ định tuyến Domain thuộc Node: [{localIp}]");

            try
            {
                (bool success, string result) = await DeleteDomainAsync(nodeIp: localIp);
                Console.WriteLine($"✅ [Shutdown-Job] Kết quả giải phóng Domain: {result}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Shutdown-Job Error] Gặp lỗi khi dọn dẹp hạ tầng Domain: {ex.Message}");
            }

            Console.WriteLine("✅ [Shutdown-Job] Hoàn tất tiến trình giải phóng Domain của Node.");
        }
    }
}
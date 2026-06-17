using ChatOps.Models;
using ChatOps.Services.FileService;
using ChatOps.Services.RedisService;
using ChatOps.Services.SystemService;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services
{
    public static class ClusterRoutingService
    {
        public static async Task<RoutingResult> HandleRoutingAsync(string baseCmd, Dictionary<string, string> payload, string connectionId, int neededCount, UserSession session)
        {
            baseCmd = baseCmd.ToLower().Trim();

            // =========================================================================
            // NHÓM 2: DEPLOY -> Chuyển tiếp cho bộ lọc phân phối tải (Xử lý sau)
            // =========================================================================
            var deployCommands = new[] { "deploy", "attach-db", "detach-db", "deploy-git", "deploy-compose" };
            if (deployCommands.Contains(baseCmd))
            {
                return await HandlePortRoutingAsync(baseCmd, payload, connectionId, neededCount, session);
            }
            // =========================================================================
            // NHÓM 3 ĐẾN 10: ĐỊNH TUYẾN ĐỘNG THEO THAM SỐ (NODE / INSTANCE / CONTAINER)
            // =========================================================================
            await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang phân tích ngữ cảnh cụm cho lệnh '{baseCmd}'...");

            // Trích xuất và chuẩn hóa dữ liệu đầu vào từ payload
            payload.TryGetValue("node", out string? rawNode);
            payload.TryGetValue("instance", out string? rawInstance);
            payload.TryGetValue("container", out string? rawContainer);

            string nodeParam = rawNode?.Trim().ToLower() ?? "";
            string instanceParam = rawInstance?.Trim() ?? "";
            string containerParam = rawContainer?.Trim() ?? "";

            // -------------------------------------------------------------------------
            // QUY TẮC 1: ƯU TIÊN TUYỆT ĐỐI THEO THAM SỐ 'node'
            // -------------------------------------------------------------------------
            if (!string.IsNullOrEmpty(nodeParam))
            {
                // Trường hợp ép định tuyến xuống TOÀN BỘ các Node thuộc hệ thống Cluster
                if (nodeParam == "allnode")
                {
                    await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🌐 [Node {AppContext.ServerID}] Phát hiện cấu hình 'node [allnode]'. Kích hoạt phát tán lệnh '{baseCmd}' đến TOÀN BỘ các Node trong cụm.");
                    return new RoutingResult { IsError = false, IsForwarding = true, TargetNodeIp = "ALL_NODES" };
                }

                // Trường hợp chỉ định ID hoặc IP đích danh của 1 máy chủ cụ thể
                string? mappedIp = await ResolveNodeIpAsync(nodeParam);
                if (!string.IsNullOrEmpty(mappedIp))
                {
                    bool isForwardingNeeded = !mappedIp.Equals(AppContext.ServerIP, StringComparison.OrdinalIgnoreCase);
                    await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🎯 [Node {AppContext.ServerID}] Định vị máy chủ mục tiêu thành công: Node [{nodeParam}] ({mappedIp}).");
                    return new RoutingResult { IsError = false, IsForwarding = isForwardingNeeded, TargetNodeIp = mappedIp };
                }

                // Nhập sai định danh NodeId / Node không trực tuyến
                return new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = $"Máy chủ chỉ định '{nodeParam}' không tồn tại hoặc đã mất kết nối khỏi mạng lưới." };
            }

            // -------------------------------------------------------------------------
            // QUY TẮC 2: TRA CỨU NODE THEO THỰC THỂ 'instance'
            // -------------------------------------------------------------------------
            if (!string.IsNullOrEmpty(instanceParam))
            {
                var (success, nodes) = await RedisInstanceService.GetInstanceAsync(instanceParam);
                if (success && nodes != null && nodes.Count > 0)
                {
                    string targetNodeIp = nodes.Keys.FirstOrDefault() ?? AppContext.ServerIP;
                    bool isForwardingNeeded = !targetNodeIp.Equals(AppContext.ServerIP, StringComparison.OrdinalIgnoreCase);

                    var noderesult = await RedisNodeService.GetNodeAsync(targetNodeIp);
                    string nodeid = noderesult.nodes[$"{targetNodeIp}"];

                    await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"📦 [Node {AppContext.ServerID}] Định vị Instance [{instanceParam}] chạy trên máy chủ: {nodeid}.");
                    return new RoutingResult { IsError = false, IsForwarding = isForwardingNeeded, TargetNodeIp = targetNodeIp };
                }
            }

            // -------------------------------------------------------------------------
            // QUY TẮC 3: TRA CỨU NODE THEO THỰC THỂ 'container'
            // -------------------------------------------------------------------------
            if (!string.IsNullOrEmpty(containerParam))
            {
                var (success, nodes) = await RedisContainerService.GetContainerAsync(containerName: containerParam);
                if (success && nodes != null && nodes.Count > 0)
                {
                    string targetNodeIp = nodes.Keys.FirstOrDefault() ?? AppContext.ServerIP;
                    bool isForwardingNeeded = !targetNodeIp.Equals(AppContext.ServerIP, StringComparison.OrdinalIgnoreCase);

                    var noderesult = await RedisNodeService.GetNodeAsync(targetNodeIp);
                    string nodeid = noderesult.nodes[$"{targetNodeIp}"];

                    await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] Định vị Container [{containerParam}] nằm trên máy chủ: {nodeid}.");
                    return new RoutingResult { IsError = false, IsForwarding = isForwardingNeeded, TargetNodeIp = targetNodeIp };
                }
            }

            // -------------------------------------------------------------------------
            // QUY TẮC 4: MẶC ĐỊNH CHẠY LOCAL TẠI NODE TIẾP NHẬN
            // -------------------------------------------------------------------------
            await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🏠 [Node {AppContext.ServerID}] Không tìm thấy tham số điều hướng hoặc thực thể ràng buộc. Thực thi cục bộ.");
            return new RoutingResult { IsError = false, IsForwarding = false, TargetNodeIp = AppContext.ServerIP };
        }

        private static async Task<string?> ResolveNodeIpAsync(string nodeIdOrIp)
        {
            if (string.IsNullOrWhiteSpace(nodeIdOrIp)) return null;

            var clusterInfo = await RedisNodeService.GetNodeAsync(isGetAll: true);
            if (clusterInfo.nodes != null)
            {
                var matchedNode = clusterInfo.nodes.FirstOrDefault(n => 
                    n.Key.Equals(nodeIdOrIp, StringComparison.OrdinalIgnoreCase) || 
                    n.Value.Equals(nodeIdOrIp, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(matchedNode.Key))
                {
                    return matchedNode.Key; 
                }
            }
            return null;
        }

        public static async Task<RoutingResult> HandlePortRoutingAsync(string baseCmd, Dictionary<string, string> payload, string connectionId, int neededCount, UserSession session)
        {
            baseCmd = baseCmd.ToLower().Trim();

            await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🔄 [Node {AppContext.ServerID}] Bắt đầu xử lý lệnh port: {baseCmd}");
            
            // BƯỚC 1: Kiểm tra điều kiện cơ bản ban đầu và Xác định Node đích ép buộc (Forced Node IP)
            var (baseErrorResult, forcedNodeIp) = await CheckRoutingContextAndForcedNodeAsync(payload, connectionId, session);
            if (baseErrorResult != null)
            {
                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"❌ [Node {AppContext.ServerID}] Lỗi tiền kiểm tra: {baseErrorResult.ErrorMessage}");
                return baseErrorResult;
            }

            // BƯỚC 2: Xử lý kịch bản đặc thù của nhóm lệnh Attach/Detach DB
            if (baseCmd == "attach-db")
            {
                var attachResult = await HandleAttachDbRoutingInternalAsync(payload, forcedNodeIp, AppContext.ServerIP, connectionId, session);

                // Nếu attachResult != null nghĩa là Tool dùng port tĩnh cũ không bị trùng -> Hoàn tất định tuyến sớm
                if (attachResult != null) return attachResult;

                // Nếu trùng port cũ và bị xóa "port" -> Ép neededCount = 1 để tí nữa xuống Bước 5 dò port động 7xxx cho Tool
                string neededCountStr = payload.GetValueOrDefault("neededCount", "1").Trim().ToLower();  
                neededCount = int.Parse(neededCountStr);

                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"⚡ [Node {AppContext.ServerID}] Chuyển tiếp luồng dò tìm cổng tự động...");
            }
            else if (baseCmd == "detach-db")
            {
                var attachResult = await HandleDetachDbRoutingInternalAsync(payload, forcedNodeIp, AppContext.ServerIP, connectionId, session);

                // Nếu attachResult != null nghĩa là Tool dùng port tĩnh cũ không bị trùng -> Hoàn tất định tuyến sớm
                if (attachResult != null) return attachResult;

                // Nếu trùng port cũ và bị xóa "port" -> Ép neededCount = 1 để tí nữa xuống Bước 5 dò port động 7xxx cho Tool
                string neededCountStr = payload.GetValueOrDefault("neededCount", "1").Trim().ToLower();  
                neededCount = int.Parse(neededCountStr);

                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"⚡ [Node {AppContext.ServerID}] Chuyển tiếp luồng dò tìm cổng tự động...");
            }

            // BƯỚC 3: Kiểm tra tính hợp lệ của số lượng cổng yêu cầu ban đầu
            if (neededCount < 0)
            {
                return new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = "Số lượng cổng cần cấp phát (neededCount) không được nhỏ hơn 0." };
            }

            // BƯỚC 4: Thu thập danh sách ứng viên xử lý (Load Balancing hoặc Ép Node cố định)
            var candidateNodes = new List<(string NodeIp, int ContainerCount)>();
            if (!string.IsNullOrEmpty(forcedNodeIp))
            {
                var noderesult = await RedisNodeService.GetNodeAsync(forcedNodeIp);
                string nodeid = noderesult.nodes[$"{forcedNodeIp}"];

                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"📍 [Node {AppContext.ServerID}] Chế độ ép định tuyến hoạt động. Chỉ kiểm tra tài nguyên trên Node chỉ định: {nodeid}");
                candidateNodes.Add((forcedNodeIp, 0));
            }
            else
            {
                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"📊 [Node {AppContext.ServerID}] Tiến hành phân tích tải trọng toàn bộ Cluster Nodes...");
                var nodeQuery = await RedisNodeService.GetNodeAsync(isGetAll: true);
                if (!nodeQuery.success || nodeQuery.nodes == null)
                {
                    return new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = "Không thể truy vấn danh sách Cluster Node từ Redis Registry." };
                }

                foreach (var node in nodeQuery.nodes)
                {
                    int count = 0;
                    var countQuery = await RedisContainerService.GetContainerCountAsync(node.Key);
                    if (countQuery.success && countQuery.counts != null && countQuery.counts.TryGetValue(node.Key, out string? rawCount))
                    {
                        int.TryParse(rawCount, out count);
                    }
                    candidateNodes.Add((node.Key, count));
                }
                candidateNodes = candidateNodes.OrderBy(n => n.ContainerCount).ToList();
            }

            // KIỂM TRA TRẠNG THÁI CÓ NHẬP PORT TĨNH HAY KHÔNG (Dành cho cả DB có nhập port bừa, Web, Tool)
            bool isStaticMode = payload.TryGetValue("port", out string? rawPortValue) && !string.IsNullOrWhiteSpace(rawPortValue);

            // BƯỚC 5: Trường hợp đặc biệt - ĐÍNH CHÍNH LOGIC CHO DB (neededCount == 0 và KHÔNG nhập port tĩnh)
            if (neededCount == 0 && !isStaticMode)
            {
                string targetNode = candidateNodes.First().NodeIp;
                var noderesult = await RedisNodeService.GetNodeAsync(targetNode);
                string nodeid = noderesult.nodes[$"{targetNode}"];
                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"📦 [Node {AppContext.ServerID}] DB không yêu cầu mở cổng Host công khai (neededCount = 0). Định tuyến container về Node tối ưu: {nodeid}");

                bool isForwardingNeeded = !targetNode.Equals(AppContext.ServerIP, StringComparison.OrdinalIgnoreCase);
                return new RoutingResult { IsError = false, IsForwarding = isForwardingNeeded, TargetNodeIp = targetNode };
            }

            // BƯỚC 6: Tiến hành dò tìm hoặc Thẩm định dải cổng tĩnh (Nếu có static port hoặc neededCount > 0)
            return await AllocateClusterPortsAsync(payload, candidateNodes, neededCount, forcedNodeIp, AppContext.ServerIP, connectionId, isStaticMode, session);
        }

        private static async Task<RoutingResult> AllocateClusterPortsAsync(Dictionary<string, string> payload, List<(string NodeIp, int ContainerCount)> candidateNodes, int neededCount, string? forcedNodeIp, string serverIp, string connectionId, bool isStaticMode, UserSession session)
        {
            var redisDb = ChatOps.Data.AppContext.RedisDB;
            var userPorts = new List<int>();
            string validatedPortsStr = "";

            // --- KHÂU KIỂM TRA PORT TĨNH (STATIC PORTS) ---
            if (isStaticMode)
            {
                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🛡️ [Node {AppContext.ServerID}] Đang thẩm định dải cổng tĩnh: {payload["port"]}");
                string rawPortValue = payload["port"];
                var portParts = rawPortValue.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in portParts)
                {
                    if (!int.TryParse(part.Trim(), out int parsedPort))
                        return new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = $"Giá trị cổng '{part}' phải là số nguyên." };
                    userPorts.Add(parsedPort);
                }

                string neededCountStr = payload.GetValueOrDefault("neededCount", "0").Trim().ToLower();  
                int expectedCount = int.Parse(neededCountStr);
                if (userPorts.Count != expectedCount)
                    return new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = $"Số lượng cổng nhập vào không khớp với yêu cầu hệ thống ({expectedCount} cổng)." };

                userPorts = userPorts.OrderBy(p => p).ToList();
                for (int i = 1; i < userPorts.Count; i++)
                {
                    if (userPorts[i] != userPorts[i - 1] + 1)
                        return new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = "Dải cổng chỉ định không liên tục (Ví dụ hợp lệ: 8001,8002)." };
                }

                if (userPorts.First() < 8000 || userPorts.Last() > 8999)
                    return new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = "Cổng tĩnh chỉ định bắt buộc phải nằm trong dải [8000 - 8999]." };

                validatedPortsStr = string.Join(",", userPorts);
            }

            // --- TIẾN HÀNH CHIẾM DỤNG TÀI NGUYÊN TRÊN CÁC NODE ---
            foreach (var node in candidateNodes)
            {
                var noderesult = await RedisNodeService.GetNodeAsync(node.NodeIp);
                string nodeid = noderesult.nodes[$"{node.NodeIp}"];
                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Thử nghiệm cấp phát trên Node: {nodeid}");
                string lockKey = $"lock:port_allocation:{node.NodeIp}";
                string lockValue = Guid.NewGuid().ToString();

                bool isLocked = await redisDb.LockTakeAsync(lockKey, lockValue, TimeSpan.FromSeconds(15));
                if (!isLocked) continue;

                try
                {
                    bool allPortsAvailable = false;
                    var finalPortsList = new List<int>();

                    if (isStaticMode)
                    {
                        bool checkStatic = true;
                        foreach (var port in userPorts)
                        {
                            if (await RedisPortService.IsPortReservedAsync(node.NodeIp, port)) { checkStatic = false; break; }
                        }

                        if (checkStatic)
                        {
                            if (node.NodeIp == serverIp)
                            {
                                foreach (var port in userPorts) if (!await SystemCommandService.IsHostPortAvailableAsync(port)) { checkStatic = false; break; }
                            }
                            else
                            {
                                string rpcResult = await RedisService.RedisService.SendPortCheckRequestAsync(node.NodeIp, validatedPortsStr);
                                if (rpcResult.StartsWith("TIMEOUT") || rpcResult.StartsWith("ERROR") || !userPorts.All(p => rpcResult.Contains($"{p}:FREE"))) checkStatic = false;
                            }
                        }

                        if (checkStatic)
                        {
                            allPortsAvailable = true;
                            finalPortsList = userPorts;
                        }
                    }
                    else
                    {
                        // --- KỊCH BẢN TỰ ĐỘNG DÒ TÌM DẢI CỔNG ĐỘNG ---
                        int currentStartPort = 7000;
                        int chunkSize = 60;

                        while (currentStartPort <= 7999)
                        {
                            int currentEndPort = Math.Min(currentStartPort + chunkSize - 1, 7999);
                            if (currentEndPort - currentStartPort + 1 < neededCount) break;

                            var candidatePorts = Enumerable.Range(currentStartPort, currentEndPort - currentStartPort + 1).ToList();
                            int consecutiveCount = 0;
                            var tempRange = new List<int>();

                            string rpcResult = node.NodeIp == serverIp ? "" : await RedisService.RedisService.SendPortCheckRequestAsync(node.NodeIp, string.Join(",", candidatePorts));
                            if (rpcResult.StartsWith("TIMEOUT") || rpcResult.StartsWith("ERROR")) { currentStartPort += (chunkSize - neededCount + 1); continue; }

                            foreach (var portOpt in candidatePorts)
                            {
                                bool isReserved = await RedisPortService.IsPortReservedAsync(node.NodeIp, portOpt);
                                bool isFreePhysically = node.NodeIp == serverIp ? await SystemCommandService.IsHostPortAvailableAsync(portOpt) : rpcResult.Contains($"{portOpt}:FREE");

                                if (!isReserved && isFreePhysically)
                                {
                                    consecutiveCount++;
                                    tempRange.Add(portOpt);
                                    if (consecutiveCount == neededCount)
                                    {
                                        allPortsAvailable = true;
                                        finalPortsList = tempRange;
                                        validatedPortsStr = string.Join(",", tempRange);
                                        break;
                                    }
                                }
                                else
                                {
                                    consecutiveCount = 0;
                                    tempRange.Clear();
                                }
                            }

                            if (allPortsAvailable) break;
                            currentStartPort += (chunkSize - neededCount + 1);
                        }
                    }

                    // Đăng ký giữ chỗ port thành công
                    if (allPortsAvailable && finalPortsList.Any())
                    {
                        await RedisPortService.ReservePortsAsync(node.NodeIp, finalPortsList.ToArray());

                        payload["port"] = validatedPortsStr;

                        await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"⚙️ [Node {AppContext.ServerID}] Xác thực dải cổng [{validatedPortsStr}] thành công trên Node: {nodeid}");

                        bool isForwardingNeeded = !node.NodeIp.Equals(serverIp, StringComparison.OrdinalIgnoreCase);
                        return new RoutingResult { IsError = false, IsForwarding = isForwardingNeeded, TargetNodeIp = node.NodeIp };
                    }
                    else
                    {
                        await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"⚠️ [Node {AppContext.ServerID}] Node {nodeid} không đủ tài nguyên hoặc trùng port.");
                    }
                }
                finally
                {
                    await redisDb.LockReleaseAsync(lockKey, lockValue);
                }
            }

            return new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = "Không tìm thấy cổng khả dụng." };
        }

        private static async Task<RoutingResult?> HandleAttachDbRoutingInternalAsync(Dictionary<string, string> payload, string? forcedNodeIp, string serverIp, string connectionId, UserSession session)
        {
            // Node mặc định ban đầu dựa trên tham số truyền vào
            string targetNode = !string.IsNullOrEmpty(forcedNodeIp) ? forcedNodeIp : serverIp;

            if (payload.TryGetValue("tool", out string? toolContainerName) && !string.IsNullOrWhiteSpace(toolContainerName))
            {
                string cleanToolName = toolContainerName.Trim();
                var (toolSuccess, toolNodes) = await RedisContainerService.GetContainerAsync(containerName: cleanToolName);

                if (toolSuccess && toolNodes != null && toolNodes.Count > 0)
                {
                    string currentToolNodeIp = toolNodes.Keys.FirstOrDefault() ?? serverIp;
                    string rawToolInfo = toolNodes.Values.FirstOrDefault() ?? string.Empty;
                    string currentToolPortsStr = rawToolInfo.Split('|').Length > 1 ? rawToolInfo.Split('|')[1].Trim() : string.Empty;

                    DockerContainer? toolContainer = null;
                    ContainerMetadata? toolMetadata = null;
                    ContainerNetworkDetails? toolNetworkDetails = null;
                    if(currentToolNodeIp==AppContext.ServerIP)
                    {
                        var tool = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(toolContainerName);
                        var metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(toolContainerName);
                        var networks = await DockerService.Read.DockerReadNetwork.GetContainerNetworkDetailsAsync(toolContainerName);
                        toolContainer = tool;
                        toolMetadata = metadata;
                        toolNetworkDetails = networks;
                    }
                    else
                    {
                        var (tool, metadata, networks) = await RedisService.RedisService.SendGetDetailContainerRequestAsync(currentToolNodeIp, cleanToolName);
                        toolContainer = tool;
                        toolMetadata = metadata;
                        toolNetworkDetails = networks;
                    }

                    if (toolContainer != null && !string.IsNullOrWhiteSpace(toolContainer.Image))
                    {
                        string toolImage = toolContainer.Image.ToLower();

                        // NHÁNH XỬ LÝ PHPMYADMIN
                        if (toolImage.Contains("phpmyadmin"))
                        {
                            var (phpMyAdminRouting, calculatedNodeIp) = await ProcessPhpMyAdminAttachedDbsAsync(payload, cleanToolName, toolMetadata, connectionId, session);
                            if (phpMyAdminRouting != null && phpMyAdminRouting.IsError) return phpMyAdminRouting;

                            // CẬP NHẬT LẠI TARGET NODE NẾU HỆ THỐNG ĐỊNH VỊ ĐƯỢC NODE MỚI TỐI ƯU HƠN
                            if (!string.IsNullOrEmpty(calculatedNodeIp) && calculatedNodeIp != targetNode)
                            {
                                var noderesult1 = await RedisNodeService.GetNodeAsync(targetNode);
                                string nodeid1 = noderesult1.nodes[$"{targetNode}"];
                                var noderesult2 = await RedisNodeService.GetNodeAsync(calculatedNodeIp);
                                string nodeid2 = noderesult2.nodes[$"{calculatedNodeIp}"];

                                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId,
                                    $"🔄 [Node {AppContext.ServerID}] Phát hiện sự dịch chuyển kiến trúc hạ tầng! Điều hướng thực thi Tool '{cleanToolName}' từ Node cũ [{nodeid1}] sang Node mới [{nodeid2}].");
                                targetNode = calculatedNodeIp;
                            }
                        }
                        // NHÁNH XỬ LÝ PGADMIN4
                        else if (toolImage.Contains("pgadmin"))
                        {
                            var (pgAdminRouting, calculatedNodeIp) = await ProcessPgAdminAttachedDbsAsync(payload, cleanToolName, currentToolNodeIp, connectionId, session);
                            if (pgAdminRouting != null && pgAdminRouting.IsError) return pgAdminRouting;

                            // CẬP NHẬT LẠI TARGET NODE NẾU HỆ THỐNG ĐỊNH VỊ ĐƯỢC NODE MỚI TỐI ƯU HƠN
                            if (!string.IsNullOrEmpty(calculatedNodeIp) && calculatedNodeIp != targetNode)
                            {
                                var noderesult1 = await RedisNodeService.GetNodeAsync(targetNode);
                                string nodeid1 = noderesult1.nodes[$"{targetNode}"];
                                var noderesult2 = await RedisNodeService.GetNodeAsync(calculatedNodeIp);
                                string nodeid2 = noderesult2.nodes[$"{calculatedNodeIp}"];

                                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId,
                                    $"🔄 [Node {AppContext.ServerID}] Phát hiện sự dịch chuyển kiến trúc hạ tầng! Điều hướng thực thi Tool '{cleanToolName}' từ Node cũ [{nodeid1}] sang Node mới [{nodeid2}].");
                                targetNode = calculatedNodeIp;
                            }
                        }
                    }

                    // 3. Logic kiểm tra trùng dải cổng cũ của Tool tại Node ĐÍCH MỚI (targetNode đã được cập nhật ở trên)
                    if (!string.IsNullOrEmpty(currentToolPortsStr))
                    {
                        payload["port"] = currentToolPortsStr;
                        bool isPortConflictAtTarget = false;
                        var toolPortsList = currentToolPortsStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                            .Select(p => int.TryParse(p.Trim(), out int portVal) ? portVal : 0)
                                                            .Where(p => p > 0).ToList();

                        if (toolPortsList.Any())
                        {
                            if (targetNode == serverIp)
                            {
                                foreach (var p in toolPortsList) if (!await SystemCommandService.IsHostPortAvailableAsync(p)) { isPortConflictAtTarget = true; break; }
                            }
                            else
                            {
                                string rpcResult = await RedisService.RedisService.SendPortCheckRequestAsync(targetNode, currentToolPortsStr);
                                if (rpcResult.StartsWith("TIMEOUT") || rpcResult.StartsWith("ERROR") || !toolPortsList.All(p => rpcResult.Contains($"{p}:FREE"))) isPortConflictAtTarget = true;
                            }
                        }

                        if (isPortConflictAtTarget)
                        {
                            await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"⚠️ [Node {AppContext.ServerID}] Tool bị trùng dải cổng cũ [{currentToolPortsStr}] tại Node đích mới. Hủy cấu hình dải cũ để chuyển sang tự động cấp phát.");
                            payload.Remove("port");
                            return null;
                        }
                    }
                }
            }

            // Trả về kết quả định tuyến chính xác đến Node vật lý cuối cùng
            bool isForwardingNeeded = !targetNode.Equals(serverIp, StringComparison.OrdinalIgnoreCase);
            return new RoutingResult { IsError = false, IsForwarding = isForwardingNeeded, TargetNodeIp = targetNode };
        }

        private static async Task<RoutingResult?> HandleDetachDbRoutingInternalAsync(Dictionary<string, string> payload, string? forcedNodeIp, string serverIp, string connectionId, UserSession session)
        {
            // 1. Xác định Node đích mặc định (Ưu tiên forcedNodeIp nếu có ép từ tham số)
            string targetNode = !string.IsNullOrEmpty(forcedNodeIp) ? forcedNodeIp : AppContext.ServerIP;

            // 2. Truy vết vị trí thực tế của Tool để điều phối chính xác về Node chứa Tool
            if (payload.TryGetValue("tool", out string? toolContainerName) && !string.IsNullOrWhiteSpace(toolContainerName))
            {
                string cleanToolName = toolContainerName.Trim();
                var (toolSuccess, toolNodes) = await RedisContainerService.GetContainerAsync(containerName: cleanToolName);

                if (toolSuccess && toolNodes != null && toolNodes.Count > 0)
                {
                    // Tìm thấy vị trí của Tool -> Cập nhật targetNode về đúng Node đang chạy Tool đó
                    string currentToolNodeIp = toolNodes.Keys.FirstOrDefault() ?? serverIp;
                    string rawToolInfo = toolNodes.Values.FirstOrDefault() ?? string.Empty;
                    string currentToolPortsStr = rawToolInfo.Split('|').Length > 1 ? rawToolInfo.Split('|')[1].Trim() : string.Empty;

                    if (!string.IsNullOrEmpty(currentToolPortsStr))
                    {
                        payload["port"] = currentToolPortsStr;
                        bool isPortConflictAtTarget = false;
                        var toolPortsList = currentToolPortsStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                            .Select(p => int.TryParse(p.Trim(), out int portVal) ? portVal : 0)
                                                            .Where(p => p > 0).ToList();

                        if (toolPortsList.Any())
                        {
                            if (targetNode == serverIp)
                            {
                                foreach (var p in toolPortsList) if (!await SystemCommandService.IsHostPortAvailableAsync(p)) { isPortConflictAtTarget = true; break; }
                            }
                            else
                            {
                                string rpcResult = await RedisService.RedisService.SendPortCheckRequestAsync(targetNode, currentToolPortsStr);
                                if (rpcResult.StartsWith("TIMEOUT") || rpcResult.StartsWith("ERROR") || !toolPortsList.All(p => rpcResult.Contains($"{p}:FREE"))) isPortConflictAtTarget = true;
                            }
                        }

                        if (isPortConflictAtTarget)
                        {
                            await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"⚠️ [Node {AppContext.ServerID}] Tool bị trùng dải cổng cũ [{currentToolPortsStr}] tại Node đích mới. Hủy cấu hình dải cũ để chuyển sang tự động cấp phát.");
                            payload.Remove("port");
                            return null;
                        }
                    }
                }
                else
                {
                    // Không tìm thấy Tool trong cụm Cluster -> Trả lỗi sớm để chặn luồng thực thi bậy
                    return new RoutingResult
                    {
                        IsError = true,
                        IsForwarding = false,
                        ErrorMessage = $"❌ Lỗi định tuyến: Không tìm thấy Tool '{cleanToolName}' đang vận hành trong hệ thống Cluster để thực hiện detach."
                    };
                }

            }
            bool isForwardingNeeded = !targetNode.Equals(serverIp, StringComparison.OrdinalIgnoreCase);
            return new RoutingResult { IsError = false, IsForwarding = isForwardingNeeded, TargetNodeIp = targetNode };
        }

        private static async Task<(RoutingResult? Routing, string? TargetNodeIp)> ProcessPhpMyAdminAttachedDbsAsync(Dictionary<string, string> payload, string toolName, dynamic? toolMetadata, string connectionId, UserSession session)
        {
            var rawInputHosts = new List<string>();

            // 1. Thu thập danh sách DB cũ từ biến môi trường PMA_HOSTS (Có thể chứa container name hoặc ip:port)
            string? toolpmaHosts = null;

            if (toolMetadata != null &&
                toolMetadata?.Environments != null && 
                toolMetadata?.Environments.TryGetValue("PMA_HOSTS", out toolpmaHosts) && 
                !string.IsNullOrEmpty(toolpmaHosts))
            {
                var oldDbs = toolpmaHosts?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim());
                if(oldDbs!=null)
                    rawInputHosts.AddRange(oldDbs);
            }

            // 2. Thu thập danh sách DB mới từ payload người dùng nhập vào lệnh
            if (payload.TryGetValue("db", out string? newDbContainer) && !string.IsNullOrWhiteSpace(newDbContainer))
            {
                var newDbs = newDbContainer.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim());
                rawInputHosts.AddRange(newDbs);
            }

            // Loại bỏ trùng lặp thô để tối ưu hiệu năng gọi sang Redis
            rawInputHosts = rawInputHosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (!rawInputHosts.Any()) return (null, null);

            await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId,
                $"🔍 [Node {AppContext.ServerID}] Đang chuẩn hóa danh sách kết nối (Container Name / IP:Port) qua hệ thống Redis Cluster...");

            // 3. CHUẨN HÓA ĐỒNG LOẠT: Dịch ngược toàn bộ chuỗi thô về Container Name chuẩn bằng cách so khớp Port
            var resolveTasks = rawInputHosts.Select(host => ResolveContainerNameFromHostAsync(host));
            string?[] resolvedResults = await Task.WhenAll(resolveTasks);

            // Kiểm tra xem có bất kỳ host nào thất bại trong quá trình phân giải không
            var failedResolution = resolvedResults.FirstOrDefault(r => string.IsNullOrEmpty(r) || r.StartsWith("Không phân giải được", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(failedResolution))
            {
                return (new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = $"❌ Lỗi định tuyến: {failedResolution}" }, null);
            }

            // Lọc sạch lại danh sách Container Name hợp lệ cuối cùng sau khi bóc tách
            var allDbContainers = resolvedResults
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!allDbContainers.Any()) return (null, null);

            await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId,
                $"🔍 [Node {AppContext.ServerID}] phpMyAdmin '{toolName}' đang tính toán lại hạ tầng gộp cho tổng số {allDbContainers.Count} DB...");

            // 4. Đánh giá vị trí tối ưu dựa trên danh sách Container Name đã chuẩn hóa sạch
            var (routingResult, calculatedNodeIp) = await EvaluateDatabasePlacementAsync(allDbContainers, connectionId, session);

            if (routingResult != null && routingResult.IsError)
            {
                return (routingResult, null);
            }

            // 5. Đồng bộ chuỗi gộp hoàn chỉnh (đã chuẩn hóa thành dạng Container Name sạch) vào payload
            payload["db"] = string.Join(",", allDbContainers);

            // Trả về IP Node tối ưu vừa tính toán được để hàm cha cập nhật targetNode
            return (null, calculatedNodeIp);
        }
        /// <summary>
        /// HƯỚNG XỬ LÝ 2: pgAdmin4 - Sẽ xử lý đọc file servers.json sau
        /// </summary>
        private static async Task<(RoutingResult? Routing, string? TargetNodeIp)> ProcessPgAdminAttachedDbsAsync(Dictionary<string, string> payload, string toolName, string nodeip, string connectionId, UserSession session)
        {
            var rawInputHosts = new List<string>();
            string oldDbsStr = "";

            // 1. Thu thập danh sách DB cũ từ biến môi trường PMA_HOSTS (Có thể chứa container name hoặc ip:port)
            if(nodeip == AppContext.ServerIP)
            {
                List<string> resultList = await PgAdminFileConfigurator.GetAttachedHosts(toolName);
                if(resultList.Count != 0)
                oldDbsStr = string.Join("\n", resultList);
            }
            else
            {
                oldDbsStr = await RedisService.RedisService.SendGetFileServersRequestAsync(nodeip, toolName);
            }
            if(!string.IsNullOrEmpty(oldDbsStr))
            {
                var oldDbs = oldDbsStr.Split("\n", StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim());
                rawInputHosts.AddRange(oldDbs);
            }
            
            // 2. Thu thập danh sách DB mới từ payload người dùng nhập vào lệnh
            if (payload.TryGetValue("db", out string? newDbContainer) && !string.IsNullOrWhiteSpace(newDbContainer))
            {
                var newDbs = newDbContainer.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim());
                rawInputHosts.AddRange(newDbs);
            }

            // Loại bỏ trùng lặp thô để tối ưu hiệu năng gọi sang Redis
            rawInputHosts = rawInputHosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (!rawInputHosts.Any()) return (null, null);

            await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId,
                $"🔍 [Node {AppContext.ServerID}] Đang chuẩn hóa danh sách kết nối (Container Name / IP:Port) qua hệ thống Redis Cluster...");

            // 3. CHUẨN HÓA ĐỒNG LOẠT: Dịch ngược toàn bộ chuỗi thô về Container Name chuẩn bằng cách so khớp Port
            var resolveTasks = rawInputHosts.Select(host => ResolveContainerNameFromHostAsync(host));
            string?[] resolvedResults = await Task.WhenAll(resolveTasks);

            // Kiểm tra xem có bất kỳ host nào thất bại trong quá trình phân giải không
            var failedResolution = resolvedResults.FirstOrDefault(r => string.IsNullOrEmpty(r) || r.StartsWith("Không phân giải được", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(failedResolution))
            {
                return (new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = $"❌ Lỗi định tuyến: {failedResolution}" }, null);
            }

            // Lọc sạch lại danh sách Container Name hợp lệ cuối cùng sau khi bóc tách
            var allDbContainers = resolvedResults
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!allDbContainers.Any()) return (null, null);

            await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId,
                $"🔍 [Node {AppContext.ServerID}] phpMyAdmin '{toolName}' đang tính toán lại hạ tầng gộp cho tổng số {allDbContainers.Count} DB...");

            // 4. Đánh giá vị trí tối ưu dựa trên danh sách Container Name đã chuẩn hóa sạch
            var (routingResult, calculatedNodeIp) = await EvaluateDatabasePlacementAsync(allDbContainers, connectionId, session);

            if (routingResult != null && routingResult.IsError)
            {
                return (routingResult, null);
            }

            // 5. Đồng bộ chuỗi gộp hoàn chỉnh (đã chuẩn hóa thành dạng Container Name sạch) vào payload
            payload["db"] = string.Join(",", allDbContainers);

            // Trả về IP Node tối ưu vừa tính toán được để hàm cha cập nhật targetNode
            return (null, calculatedNodeIp);
        }
        /// <summary>
        /// Chuyển đổi chính xác HostInput (Container Name hoặc IP:Port) về Container Name chuẩn từ Redis
        /// </summary>
        public static async Task<string?> ResolveContainerNameFromHostAsync(string hostInput)
        {
            string cleanInput = hostInput.Trim();
            if (string.IsNullOrEmpty(cleanInput)) return string.Empty;

            // TRƯỜNG HỢP 1: Đầu vào là Container Name thuần túy (Không chứa dấu ":")
            if (!cleanInput.Contains(":"))
            {
                return cleanInput;
            }

            // TRƯỜNG HỢP 2: Đầu vào là dạng IP:Port (Ví dụ: 192.168.1.8:8001)
            var parts = cleanInput.Split(':');
            string ipAddress = parts[0].Trim();
            string targetPort = parts.Length > 1 ? parts[1].Trim() : string.Empty;

            // Lấy toàn bộ danh sách container thuộc Node IP này từ Redis
            var (success, nodes) = await RedisContainerService.GetContainerAsync(nodeIp: ipAddress);

            if (success && nodes != null && nodes.TryGetValue(ipAddress, out string? rawContainers) && !string.IsNullOrEmpty(rawContainers))
            {
                // Các member trong cụm được hàm GetContainerAsync nối với nhau bằng '\n'
                string[] validMembers = rawContainers.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                foreach (var member in validMembers)
                {
                    // Cấu trúc member trong Redis: "container_name|port" (Ví dụ: "my-db|8001")
                    var memberParts = member.Split('|');
                    string containerName = memberParts[0].Trim();
                    string currentPort = memberParts.Length > 1 ? memberParts[1].Trim() : string.Empty;

                    // So khớp chính xác Port để bốc ra đúng Container Name
                    if (currentPort.Equals(targetPort, StringComparison.OrdinalIgnoreCase))
                    {
                        return containerName;
                    }
                }
            }

            // Nếu không quét được trong Redis Cluster, trả về IP gốc để tránh làm gãy luồng xử lý sau
            return $"Không phân giải được {hostInput}";
        }
        /// <summary>
        /// Dịch ngược từ Container Name để phân giải cấu hình kết nối tối ưu:
        /// - Nếu DB KHÔNG có outport -> Trả về chính Container Name để kết nối nội bộ Docker Network.
        /// - Nếu DB CÓ outport -> Trả về định dạng IP:Port để kết nối qua IP vật lý của Node.
        /// </summary>
        public static async Task<string> ResolveHostFromContainerNameAsync(string containerName)
        {
            string cleanInput = containerName.Trim();
            if (string.IsNullOrEmpty(cleanInput)) return string.Empty;

            // 1. Quét thông tin vị trí container trên toàn cụm Cluster từ Redis
            var (success, nodes) = await RedisContainerService.GetContainerAsync(containerName: cleanInput);

            if (success && nodes != null && nodes.Count > 0)
            {
                // Lấy Node IP chứa container này (Key của Dictionary)
                string nodeIp = nodes.Keys.FirstOrDefault() ?? string.Empty;

                // Lấy chuỗi thông tin thành viên dạng "container_name|port" (Value của Dictionary)
                string rawMemberInfo = nodes.Values.FirstOrDefault() ?? string.Empty;

                if (!string.IsNullOrEmpty(nodeIp) && !string.IsNullOrEmpty(rawMemberInfo))
                {
                    // Dự phòng trường hợp chuỗi chứa nhiều dòng member nối bằng '\n', bốc dòng đầu tiên khớp
                    string firstMember = rawMemberInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? rawMemberInfo;

                    // Tách chuỗi theo ký tự '|' để bóc tách Port
                    var memberParts = firstMember.Split('|');
                    string actualContainerName = memberParts[0].Trim();
                    string targetPort = memberParts.Length > 1 ? memberParts[1].Trim() : string.Empty;

                    // CHIA 2 TRƯỜNG HỢP DỰA VÀO OUTPORT:
                    // TH 1: Không có outport (chuỗi port rỗng, hoặc bằng "0") -> Dùng Container Name nội bộ
                    if (string.IsNullOrEmpty(targetPort) || targetPort == "0")
                    {
                        return actualContainerName; 
                    }

                    // TH 2: Có outport hợp lệ -> Gộp lại thành chuỗi IP:Port để kết nối qua cổng Public vật lý
                    return $"{nodeIp}:{targetPort}";
                }
            }

            // Nếu hoàn toàn không truy vết được container trong hệ thống Redis
            return $"Không tìm thấy thông tin hạ tầng cho container {containerName}";
        }
        
        private static async Task<(RoutingResult? ErrorResult, string? ForcedNodeIp)> CheckRoutingContextAndForcedNodeAsync(Dictionary<string, string> payload, string connectionId, UserSession session)
        {
            // Bước 1: Kiểm tra tính hợp lệ của payload
            if (!payload.ContainsKey("rawcommand"))
            {
                string errorMsg = "Không tìm thấy tham số lệnh gốc 'rawcommand' trong payload.";
                return (new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = errorMsg }, null);
            }

            // Bước 2: Kiểm tra tham số 'db' (Được đẩy lên ưu tiên cao nhất)
            if (payload.TryGetValue("db", out string? dbContainer) && !string.IsNullOrWhiteSpace(dbContainer))
            {
                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang kiểm tra vị trí và cấu hình export của các DB: '{dbContainer}'...");

                var rawDbs = dbContainer.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()).ToList();

                // 1. CHUẨN HÓA ĐỒNG LOẠT: Dịch ngược từ Container Name hoặc IP:Port về Container Name chuẩn
                var resolveTasks = rawDbs.Select(host => ResolveContainerNameFromHostAsync(host));
                string?[] resolvedResults = await Task.WhenAll(resolveTasks);

                // 2. Kiểm tra xem có host nào bị thất bại trong quá trình phân giải không
                var failedResolution = resolvedResults.FirstOrDefault(r => string.IsNullOrEmpty(r) || r.StartsWith("Không phân giải được", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(failedResolution))
                {
                    return (new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = $"❌ Lỗi định tuyến: {failedResolution}" }, null);
                }

                // 3. Lọc sạch lại danh sách Container Name sau phân giải
                var cleanDbContainers = resolvedResults
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (!cleanDbContainers.Any())
                {
                    return (new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = "❌ Không tìm thấy danh sách DB hợp lệ sau khi phân giải." }, null);
                }

                // 4. Đồng bộ lại chuỗi sạch vào payload để các tầng sau không phải xử lý lại chuỗi IP:Port nữa
                payload["db"] = string.Join(",", cleanDbContainers);

                // 5. Đánh giá vị trí hạ tầng tối ưu
                return await EvaluateDatabasePlacementAsync(cleanDbContainers, connectionId, session);
            }

            // Bước 3: Kiểm tra tham số 'node' (Chỉ chạy khi không truyền tham số 'db' hoặc lệnh không gắn DB)
            if (payload.TryGetValue("node", out string? targetNodeId) && !string.IsNullOrWhiteSpace(targetNodeId))
            {
                string cleanNodeId = targetNodeId.Trim();
                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang truy vết Node ID: '{cleanNodeId}'...");
                
                var nodeQuery = await RedisNodeService.GetNodeAsync(isGetAll: true);
                if (nodeQuery.success && nodeQuery.nodes != null)
                {
                    string? mappedIp = nodeQuery.nodes.FirstOrDefault(x => x.Value.Trim().Equals(cleanNodeId, StringComparison.OrdinalIgnoreCase)).Key;
                    if (!string.IsNullOrEmpty(mappedIp))
                    {
                        await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"✅ [Node {AppContext.ServerID}] Xác định Node đích theo chỉ định: {cleanNodeId} (IP: {mappedIp})");
                        return (null, mappedIp);
                    }
                    
                    string errorMsg = $"Node ID '{cleanNodeId}' không tồn tại trong hệ thống cluster.";
                    return (new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = errorMsg }, null);
                }
                
                return (new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = "Lỗi kết nối hệ thống Registry khi truy vết Node ID." }, null);
            }

            return (null, null);
        }
        /// <summary>
        /// Hàm phân tích vị trí vật lý và cấu hình mạng của danh sách DB nhằm xác định Node điều phối Tool hợp lệ.
        /// </summary>
        private static async Task<(RoutingResult? Routing, string? TargetNodeIp)> EvaluateDatabasePlacementAsync(
            List<string?> dbContainers,
            string connectionId, 
            UserSession session)
        {
            var analyzedNodes = new List<(string NodeIp, string ContainerName, bool IsNoExport)>();

            foreach (var dbContainerName in dbContainers)
            {
                if(dbContainerName == null)
                    continue;
                string cleanName = dbContainerName.Trim();
                var (dbSuccess, dbNodes) = await RedisContainerService.GetContainerAsync(containerName: cleanName);

                if (!dbSuccess || dbNodes == null || dbNodes.Count == 0)
                {
                    string errorMsg = $"❌ Container DB '{cleanName}' được chỉ định không tồn tại trong cụm Cluster.";
                    return (new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = errorMsg }, null);
                }

                string currentNodeIp = dbNodes.Keys.FirstOrDefault() ?? AppContext.ServerIP;
                string redisValue = dbNodes.Values.FirstOrDefault() ?? ""; // "my-pma|8088" hoặc "my-pma"

                // Kiểm tra cấu hình dựa trên 2 kiểu lưu trữ đặc thù của Redis
                bool isNoExport = !redisValue.Contains("|");

                analyzedNodes.Add((NodeIp: currentNodeIp, ContainerName: cleanName, IsNoExport: isNoExport));
            }

            // Lọc ra danh sách các container chạy ngầm (không export)
            var noExportContainers = analyzedNodes.Where(x => x.IsNoExport).ToList();

            // Nhóm các container không export theo từng Node IP để phục vụ việc trace lỗi
            var noExportGroups = noExportContainers
                .GroupBy(x => x.NodeIp)
                .ToDictionary(g => g.Key, g => g.Select(x => x.ContainerName).ToList());

            // TH 1: Có các container chạy ngầm nằm rải rác ở từ 2 Node khác nhau trở lên -> BÁO LỖI CHI TIẾT
            if (noExportGroups.Count > 1)
            {
                var errorDetails = new System.Text.StringBuilder();
                errorDetails.AppendLine("❌ Lỗi cấu hình hạ tầng bất khả thi! Phát hiện các Container chạy ngầm (Không export) nằm ở các Node khác nhau, Tool không thể kết nối nội bộ đồng thời:");

                foreach (var group in noExportGroups)
                {
                    errorDetails.AppendLine($"  📍 Node IP [{group.Key}]: chứa các DB chạy ngầm -> {string.Join(", ", group.Value)}");
                }
                errorDetails.Append("💡 Giải pháp: Vui lòng chọn các DB không export chạy chung một Node, hoặc phải cấu hình Export Port cho chúng.");

                return (new RoutingResult { IsError = true, IsForwarding = false, ErrorMessage = errorDetails.ToString() }, null);
            }

            // TH 2: Tất cả các container chạy ngầm đều tập trung tại ĐÚNG 1 Node duy nhất -> HỢP LỆ
            if (noExportGroups.Count == 1)
            {
                string targetNodeIp = noExportGroups.Keys.First();
                var currentNoExportNames = noExportGroups.Values.First();

                var noderesult = await RedisNodeService.GetNodeAsync(targetNodeIp);
                string nodeid = noderesult.nodes[$"{targetNodeIp}"];

                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId,
                    $"⚠️ [Node {AppContext.ServerID}] Phát hiện kiến trúc DB phân tán. Tuy nhiên, toàn bộ các DB chạy ngầm ({string.Join(", ", currentNoExportNames)}) đều nằm tập trung tại Node: {nodeid}.");
                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId,
                    $"🚀 [Node {AppContext.ServerID}] Điều phối Tool khởi chạy tại Node gốc: {nodeid} để kết nối nội bộ. Các DB có export ở các Node khác sẽ được kết nối từ xa qua IP:Port.");

                return (null, targetNodeIp);
            }

            // TH 3: Không có bất kỳ container chạy ngầm nào (Tất cả chỉ định đều là kiểu export "my-pma|8088")
            var distinctAllNodes = analyzedNodes.Select(x => x.NodeIp).Distinct().ToList();
            if (distinctAllNodes.Count == 1)
            {
                string targetIp = distinctAllNodes.First();

                var noderesult = await RedisNodeService.GetNodeAsync(targetIp);
                string nodeid = noderesult.nodes[$"{targetIp}"];

                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"✅ [Node {AppContext.ServerID}] Toàn bộ danh sách DB đồng nhất trên cùng một Node: {nodeid}");
                return (null, targetIp);
            }
            else
            {
                // Toàn bộ đều export và nằm khác Node -> Lấy Node của DB đầu tiên làm Node điều phối Tool
                string fallbackNodeIp = analyzedNodes.First().NodeIp;

                var noderesult = await RedisNodeService.GetNodeAsync(fallbackNodeIp);
                string nodeid = noderesult.nodes[$"{fallbackNodeIp}"];

                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId,
                    $"🌐 [Node {AppContext.ServerID}] Tất cả các DB chỉ định đều đã cấu hình Export Port công khai. Hệ thống điều phối Tool khởi chạy tại Node mặc định của DB đầu tiên: {nodeid}");
                return (null, fallbackNodeIp);
            }
        }
    }
}
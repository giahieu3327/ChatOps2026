using ChatOps.Data;
using AppContext = ChatOps.Data.AppContext;
using ChatOps.Models;
using Microsoft.EntityFrameworkCore;
using ChatOps.Services.RedisService;
using ChatOps.Services.SystemService;
using ChatOps.Services.FileService;
using System.Text.RegularExpressions;

namespace ChatOps.Services.ChatService
{
    public static class ChatDeployServiceAtDetachDB
    {
        private static async Task SendLogWithDelayAsync(bool debug, string connectionId, string message)
        {
            await Task.Delay(100);
            await RedisChannelService.SendMessageToClientAsync(debug, connectionId, message);
        }
        public static async Task<string> AttachDB(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang trích xuất và kiểm tra tham số định danh hạ tầng Cluster...");

            string dbInput = parsed.GetValueOrDefault("db", "").Trim().ToLower();
            string toolInput = parsed.GetValueOrDefault("tool", "").Trim().ToLower();

            if (string.IsNullOrWhiteSpace(dbInput))
                return "❌ Thiếu tham số bắt buộc! Vui lòng chỉ định tên danh sách DB container bằng tham số 'db'.";

            if (string.IsNullOrWhiteSpace(toolInput))
                return "❌ Thiếu tham số bắt buộc! Vui lòng chỉ định tên Tool container bằng tham số 'tool'.";

            if (!Regex.IsMatch(toolInput, "^[a-zA-Z0-9]+$"))
                return "❌ Tên Tool container không hợp lệ. Chỉ được chứa chữ hoặc số.";

            var inputDbsCheck = dbInput.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()).ToList();
            foreach (var dbName in inputDbsCheck)
            {
                if (!Regex.IsMatch(dbName, "^[a-zA-Z0-9]+$"))
                    return $"❌ Tên DB container '{dbName}' trong danh sách nhập vào không hợp lệ. Chỉ được chứa chữ hoặc số.";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Kiểm tra định dạng hợp lệ. Đang truy vấn vị trí và thông tin cấu hình Tool '{toolInput}'...");

            string currentToolNodeIp = "";
            var toolnode = await RedisContainerService.GetContainerAsync(toolInput);
            if (toolnode.success && toolnode.nodes != null && toolnode.nodes.Count > 0)
            {
                foreach (var node in toolnode.nodes)
                {
                    string[] rawvalue = node.Value.Split('|');
                    if (rawvalue[0] == toolInput)
                    {
                        currentToolNodeIp = node.Key;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(currentToolNodeIp))
                return $"❌ Không xác định được Node IP hiện tại của Tool container '{toolInput}'. Vui lòng đảm bảo container đang chạy.";

            DockerContainer? toolContainer = null;
            ContainerMetadata? toolMetadata = null;
            ContainerNetworkDetails? toolNetworkDetails = null;

            if (currentToolNodeIp == AppContext.ServerIP)
            {
                var toolTask = DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(toolInput);
                var metadataTask = DockerService.Read.DockerReadMetadata.GetMetadataAsync(toolInput);
                var networksTask = DockerService.Read.DockerReadNetwork.GetContainerNetworkDetailsAsync(toolInput);
                await Task.WhenAll(toolTask, metadataTask, networksTask);

                toolContainer = toolTask.Result;
                toolMetadata = metadataTask.Result;
                toolNetworkDetails = networksTask.Result;
            }
            else
            {
                var (tool, metadata, networks) = await RedisService.RedisService.SendGetDetailContainerRequestAsync(currentToolNodeIp, toolInput);
                toolContainer = tool;
                toolMetadata = metadata;
                toolNetworkDetails = networks;
            }

            if (toolContainer == null || toolMetadata == null || toolNetworkDetails == null)
                return $"❌ Lấy dữ liệu Container/Metadata/Network của Tool container '{toolInput}' thất bại.";

            string toolName = toolContainer.Name;
            string toolImage = toolContainer.Image;
            bool isPhpMyAdmin = toolImage.Contains("phpmyadmin");
            bool isPgAdmin = toolImage.Contains("pgadmin");

            if (!isPhpMyAdmin && !isPgAdmin)
                return "❌ Container đích không phải tool hỗ trợ kết nối quản trị.";

            var rawInputHosts = new List<string>();

            if (isPhpMyAdmin)
            {
                var existingEnv = toolMetadata.Environments;
                if (existingEnv != null && existingEnv.TryGetValue("PMA_HOSTS", out string? oldHosts) && !string.IsNullOrEmpty(oldHosts))
                {
                    var oldList = oldHosts.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(h => h.Trim());
                    rawInputHosts.AddRange(oldList);
                }
            }
            else if (isPgAdmin)
            {
                string oldDbsStr = "";
                if (currentToolNodeIp == AppContext.ServerIP)
                {
                    List<string> resultList = await PgAdminFileConfigurator.GetAttachedHosts(toolName);
                    if (resultList.Count != 0)
                        oldDbsStr = string.Join("\n", resultList);
                }
                else
                {
                    oldDbsStr = await RedisService.RedisService.SendGetFileServersRequestAsync(currentToolNodeIp, toolName);
                }

                if (!string.IsNullOrEmpty(oldDbsStr))
                {
                    var oldDbs = oldDbsStr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim());
                    rawInputHosts.AddRange(oldDbs);
                }
            }

            rawInputHosts.AddRange(inputDbsCheck);
            rawInputHosts = rawInputHosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang phân giải và chuẩn hóa danh sách liên kết DB qua Redis Cluster Routing...");

            var resolveTasks = rawInputHosts.Select(host => ClusterRoutingService.ResolveContainerNameFromHostAsync(host)).ToList();
            string?[] resolvedResults = await Task.WhenAll(resolveTasks);

            var failedResolution = resolvedResults.FirstOrDefault(r => string.IsNullOrEmpty(r) || r.StartsWith("Không phân giải được", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(failedResolution))
                return $"❌ Lỗi định tuyến Cluster khi phân giải thực thể: {failedResolution}";

            var allDbContainers = resolvedResults
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!allDbContainers.Any())
                return "❌ Danh sách cơ sở dữ liệu sau xử lý trống.";

            var finalResolvedDbHosts = new List<string>();
            var finalDbTypes = new List<string>();
            string chosenNetwork = "ChatOps-net";

            foreach (var dbContainerName in allDbContainers)
            {
                if (dbContainerName == null) continue;

                string hostResolved = await ClusterRoutingService.ResolveHostFromContainerNameAsync(dbContainerName);
                if (hostResolved.StartsWith("Không tìm thấy", StringComparison.OrdinalIgnoreCase))
                    return $"❌ Lỗi định tuyến Cluster khi phân giải ngược lại DB '{dbContainerName}': {hostResolved}";

                string currentdbNodeIp = "";
                var dbnode = await RedisContainerService.GetContainerAsync(dbContainerName);
                if (dbnode.success && dbnode.nodes != null && dbnode.nodes.Count > 0)
                {
                    foreach (var node in dbnode.nodes)
                    {
                        string[] rawvalue = node.Value.Split('|');
                        if (rawvalue[0] == dbContainerName)
                        {
                            currentdbNodeIp = node.Key;
                            break;
                        }
                    }
                }

                DockerContainer? dbContainer = null;
                ContainerMetadata? dbMetadata = null;
                ContainerNetworkDetails? dbNetworkDetails = null;

                if (currentdbNodeIp == AppContext.ServerIP)
                {
                    var dbTask = DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(dbContainerName);
                    var metaTask = DockerService.Read.DockerReadMetadata.GetMetadataAsync(dbContainerName);
                    var netTask = DockerService.Read.DockerReadNetwork.GetContainerNetworkDetailsAsync(dbContainerName);
                    await Task.WhenAll(dbTask, metaTask, netTask);

                    dbContainer = dbTask.Result;
                    dbMetadata = metaTask.Result;
                    dbNetworkDetails = netTask.Result;
                }
                else
                {
                    var (db, metadata, networks) = await RedisService.RedisService.SendGetDetailContainerRequestAsync(currentdbNodeIp, dbContainerName);
                    dbContainer = db;
                    dbMetadata = metadata;
                    dbNetworkDetails = networks;
                }

                if (dbContainer == null || dbMetadata == null || dbNetworkDetails == null)
                    return $"❌ Lấy dữ liệu Container/Metadata/Network của DB container '{dbContainerName}' thất bại.";

                string dbType = dbMetadata.DbType?.ToLower() ?? "";
                if (string.IsNullOrWhiteSpace(dbType))
                    return $"❌ DB container '{dbContainerName}' chưa có nhãn định danh dbtype.";

                finalResolvedDbHosts.Add(hostResolved);
                if (!finalDbTypes.Contains(dbType))
                    finalDbTypes.Add(dbType);

                if (chosenNetwork == "ChatOps-net" && dbNetworkDetails.Networks != null && dbNetworkDetails.Networks.Any())
                    chosenNetwork = dbNetworkDetails.Networks.FirstOrDefault() ?? "ChatOps-net";
            }

            string toolOwner = toolMetadata.Owners != null && toolMetadata.Owners.Count > 0 ? string.Join(",", toolMetadata.Owners) : "unknown";
            string toolDomain = toolMetadata.Domain;
            string serviceKey = toolMetadata.Service;
            string toolOldPort = toolContainer.OutPorts;

            string labelArgs = ChatDeployService.BuildDockerLabels(serviceKey, dockerImage: null!, serviceType: null!, toolDomain, toolOwner);

            ImageCategories.ImageServices.TryGetValue(toolImage, out var service);

            string finalAllocatedPorts = parsed.GetValueOrDefault("port", "");
            if (string.IsNullOrEmpty(finalAllocatedPorts))
                finalAllocatedPorts = toolContainer.OutPorts;

            var extPorts = finalAllocatedPorts
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var p) ? p : (int?)null)
                .Where(p => p.HasValue)
                .Select(p => p!.Value)
                .ToList();

            string rawInPorts = service.InPort ?? "";
            var inPorts = rawInPorts
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var p) ? p : (int?)null)
                .Where(p => p.HasValue)
                .Select(p => p!.Value)
                .ToList();

            string portMapping = extPorts.Any(p => p > 0)
                ? string.Join(" ", extPorts.Zip(inPorts, (ext, def) => $"-p {ext}:{def}"))
                : "";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"⚙️ [Node {AppContext.ServerID}] Đồng bộ cấu hình thành công. Khởi chạy chiến thuật sao lưu dữ liệu hạ tầng (Rollback Boundary)...");

            string backupToolName = $"{toolName}_backup";
            bool hasRemoteBackup = false;

            try
            {
                if (currentToolNodeIp.Equals(AppContext.ServerIP, StringComparison.OrdinalIgnoreCase))
                {
                    if (toolContainer.IsRunning)
                    {
                        await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] [Local Backup] Đổi tên container hiện tại thành '{backupToolName}'...");
                        string renameStatus = await DockerService.Update.DockerUpdateLifecycle.RenameContainerAsync(toolName, backupToolName);
                        if (renameStatus != "SUCCESS") throw new Exception($"Lỗi tạo bản sao tại Master: {renameStatus}");
                        hasRemoteBackup = true;

                        await RedisContainerService.DeleteContainerAsync(toolName);
                        await RedisContainerService.UpdateContainerValueAsync(AppContext.ServerIP, backupToolName, toolOldPort);
                    }
                }
                else
                {
                    var noderesult = await RedisNodeService.GetNodeAsync(currentToolNodeIp);
                    string nodeid = noderesult.nodes[$"{currentToolNodeIp}"];
                    await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] [Remote Backup] Gửi yêu cầu đổi tên tới Worker Node ({nodeid})...");
                    string remoteRenameResult = await RedisService.RedisService.SendRenameContainerRequestAsync(currentToolNodeIp, toolName, backupToolName);

                    if (remoteRenameResult.StartsWith("ERROR") || remoteRenameResult.StartsWith("TIMEOUT"))
                        throw new Exception($"Không thể tạo bản sao dự phòng tại Node từ xa. Chi tiết: {remoteRenameResult}");

                    hasRemoteBackup = true;
                    await RedisContainerService.DeleteContainerAsync(toolName);
                    await RedisContainerService.UpdateContainerValueAsync(currentToolNodeIp, backupToolName, toolOldPort);
                }

                await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] [Deploy] Đang khởi tạo Tool mới mang cấu hình liên kết gộp Cluster...");

                string result = isPhpMyAdmin
                    ? await ChatDeployService.RunPhpMyAdmin(toolImage, finalResolvedDbHosts, finalDbTypes, chosenNetwork, portMapping, toolName, labelArgs, session, connectionId)
                    : await ChatDeployService.RunPgAdmin(toolImage, finalResolvedDbHosts, finalDbTypes, chosenNetwork, portMapping, toolName, labelArgs, session, connectionId, ImageCategories.ImageServices["dpage/pgadmin4:latest"].Env);

                if (extPorts.Any(p => p > 0))
                    await RedisPortService.ReleasePortsAsync(AppContext.ServerIP, extPorts.ToArray());

                if (string.IsNullOrWhiteSpace(result) || result.Contains("error", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Docker Engine từ chối áp dụng cấu hình đính kèm mới. Chi tiết: {result}");

                if (hasRemoteBackup)
                {
                    await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] [Clean] Đính kèm thành công! Tiến hành giải phóng và xóa bản sao dự phòng...");

                    if (currentToolNodeIp.Equals(AppContext.ServerIP, StringComparison.OrdinalIgnoreCase))
                    {
                        await DockerService.Delete.DockerDeleteContainer.RemoveContainerForceAsync(backupToolName);
                        await RedisContainerService.DeleteContainerAsync(backupToolName);
                    }
                    else
                    {
                        await RedisService.RedisService.SendDeleteContainerRequestAsync(currentToolNodeIp, backupToolName);
                        await RedisContainerService.DeleteContainerAsync(backupToolName);
                    }

                    await RedisContainerService.UpdateContainerValueAsync(currentToolNodeIp, toolName, finalAllocatedPorts);
                    await RedisDomainService.DeleteDomainAsync(toolDomain);
                    foreach (var port in extPorts)
                    {
                        await RedisDomainService.InsertDomainAsync(toolDomain, currentToolNodeIp + $":{port}");
                    }
                }
            }
            catch (Exception ex)
            {
                await SendLogWithDelayAsync(session.Debug, connectionId, $"⚠️ [Node {AppContext.ServerID}] [Rollback] Phát hiện sự cố: {ex.Message}. Đang khôi phục lại trạng thái cũ...");

                bool isLocal = currentToolNodeIp.Equals(AppContext.ServerIP, StringComparison.OrdinalIgnoreCase);

                if (isLocal)
                {
                    if (await DockerService.Read.DockerReadContainer.IsContainerExistsAsync(toolName))
                    {
                        await DockerService.Delete.DockerDeleteContainer.RemoveContainerForceAsync(toolName);
                        await RedisContainerService.DeleteContainerAsync(toolName);
                    }

                    if (hasRemoteBackup)
                    {
                        await DockerService.Update.DockerUpdateLifecycle.RenameContainerAsync(backupToolName, toolName);
                        await SystemCommandService.RunAsync($"docker start {toolName}");
                        await RedisContainerService.DeleteContainerAsync(backupToolName);
                        await RedisContainerService.UpdateContainerValueAsync(AppContext.ServerIP, toolName, toolOldPort);
                    }
                }
                else
                {
                    await RedisService.RedisService.SendDeleteContainerRequestAsync(currentToolNodeIp, toolName);
                    await RedisContainerService.DeleteContainerAsync(toolName);

                    if (hasRemoteBackup)
                    {
                        await RedisService.RedisService.SendRenameContainerRequestAsync(currentToolNodeIp, backupToolName, toolName);
                        await RedisContainerService.DeleteContainerAsync(backupToolName);
                        await RedisContainerService.UpdateContainerValueAsync(currentToolNodeIp, toolName, toolOldPort);
                    }
                }

                if (hasRemoteBackup)
                    return $"❌ Tiến trình Attach DB thất bại. Hệ thống đã tự động khôi phục lại Tool về trạng thái ban đầu tại Node: {currentToolNodeIp}.\nChi tiết lỗi: {ex.Message}";

                return $"❌ Tiến trình lỗi nặng và không thể phục hồi dữ liệu từ bản backup.\nChi tiết lỗi: {ex.Message}";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Kết thúc tiến trình thành công. Đang kết xuất báo cáo...");

            var toolNetworkDetails1 = await DockerService.Read.DockerReadNetwork.GetContainerNetworkDetailsAsync(toolName);
            string finalNetworks = toolNetworkDetails1?.Networks != null && toolNetworkDetails1.Networks.Count > 0
                ? string.Join(",", toolNetworkDetails1.Networks)
                : "Không xác định";

            string dbLinkStatus = string.Join(",", finalResolvedDbHosts);
            return $"✅ Attach DB thành công\n🧠 Tool: {toolName}\n🗄 Danh sách các DB liên kết hiện tại: [{dbLinkStatus}]\n🗄 DB vừa thêm: {dbInput}\n🌐 Port Host: {portMapping}\n🌐 Toàn bộ mạng kết nối: {finalNetworks}";
        }
        public static async Task<string> DetachDB(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang trích xuất và kiểm tra tham số định danh hạ tầng Cluster...");

            string dbInput = parsed.GetValueOrDefault("db", "").Trim().ToLower();
            string toolInput = parsed.GetValueOrDefault("tool", "").Trim().ToLower();

            if (string.IsNullOrWhiteSpace(dbInput))
                return "❌ Thiếu tham số bắt buộc! Vui lòng chỉ định danh sách DB container cần gỡ bằng tham số 'db'.";

            if (string.IsNullOrWhiteSpace(toolInput))
                return "❌ Thiếu tham số bắt buộc! Vui lòng chỉ định tên Tool container bằng tham số 'tool'.";

            if (!Regex.IsMatch(toolInput, "^[a-zA-Z0-9]+$"))
                return "❌ Tên Tool container không hợp lệ. Chỉ được chứa chữ hoặc số.";

            var inputDbsCheck = dbInput.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim());
            foreach (var dbName in inputDbsCheck)
            {
                if (!Regex.IsMatch(dbName, "^[a-zA-Z0-9]+$"))
                    return $"❌ Tên DB container '{dbName}' trong danh sách yêu cầu gỡ không hợp lệ. Chỉ được chứa chữ hoặc số.";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Kiểm tra định dạng hợp lệ. Đang truy vấn vị trí và thông tin cấu hình Tool '{toolInput}'...");

            string currentToolNodeIp = "";
            var toolnode = await RedisContainerService.GetContainerAsync(toolInput);
            if (toolnode.success && toolnode.nodes != null && toolnode.nodes.Count > 0)
            {
                foreach (var node in toolnode.nodes)
                {
                    string[] rawvalue = node.Value.Split("|");
                    if (rawvalue[0] == toolInput)
                    {
                        currentToolNodeIp = node.Key;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(currentToolNodeIp))
                return $"❌ Không xác định được Node IP hiện tại của Tool container '{toolInput}'. Vui lòng đảm bảo container đang chạy.";

            DockerContainer? toolContainer = null;
            ContainerMetadata? toolMetadata = null;
            ContainerNetworkDetails? toolNetworkDetails = null;

            if (currentToolNodeIp == AppContext.ServerIP)
            {
                var toolTask = DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(toolInput);
                var metadataTask = DockerService.Read.DockerReadMetadata.GetMetadataAsync(toolInput);
                var networksTask = DockerService.Read.DockerReadNetwork.GetContainerNetworkDetailsAsync(toolInput);
                await Task.WhenAll(toolTask, metadataTask, networksTask);

                toolContainer = toolTask.Result;
                toolMetadata = metadataTask.Result;
                toolNetworkDetails = networksTask.Result;
            }
            else
            {
                var (tool, metadata, networks) = await RedisService.RedisService.SendGetDetailContainerRequestAsync(currentToolNodeIp, toolInput);
                toolContainer = tool;
                toolMetadata = metadata;
                toolNetworkDetails = networks;
            }

            if (toolContainer == null || toolMetadata == null || toolNetworkDetails == null)
                return $"❌ Lấy dữ liệu Container/Metadata/Network của Tool container '{toolInput}' thất bại.";

            string toolName = toolContainer.Name;
            string toolImage = toolContainer.Image;
            bool isPhpMyAdmin = toolImage.Contains("phpmyadmin");
            bool isPgAdmin = toolImage.Contains("pgadmin");

            if (!isPhpMyAdmin && !isPgAdmin)
                return "❌ Container đích không phải tool hỗ trợ kết nối quản trị.";

            var existingRawHosts = new List<string>();

            if (isPhpMyAdmin)
            {
                var existingEnv = toolMetadata.Environments;
                if (existingEnv != null && existingEnv.TryGetValue("PMA_HOSTS", out string? oldHosts) && !string.IsNullOrEmpty(oldHosts))
                {
                    existingRawHosts.AddRange(oldHosts.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(h => h.Trim()));
                }
            }
            else if (isPgAdmin)
            {
                string oldDbsStr = "";
                if (currentToolNodeIp == AppContext.ServerIP)
                {
                    List<string> oldDbsList = await FileService.PgAdminFileConfigurator.GetAttachedHosts(toolName);
                    if (oldDbsList != null && oldDbsList.Count > 0)
                        oldDbsStr = string.Join("\n", oldDbsList);
                }
                else
                {
                    oldDbsStr = await RedisService.RedisService.SendGetFileServersRequestAsync(currentToolNodeIp, toolName);
                }

                if (!string.IsNullOrEmpty(oldDbsStr))
                {
                    existingRawHosts.AddRange(oldDbsStr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()));
                }
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang phân giải và tách rời danh sách liên kết DB qua Redis Cluster Routing...");

            var resolveExistingTasks = existingRawHosts.Select(host => ClusterRoutingService.ResolveContainerNameFromHostAsync(host));
            string?[] resolvedExistingContainers = await Task.WhenAll(resolveExistingTasks);

            var rawDetachList = dbInput.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()).Distinct().ToList();
            var resolveDetachTasks = rawDetachList.Select(host => ClusterRoutingService.ResolveContainerNameFromHostAsync(host));
            string?[] resolvedDetachContainers = await Task.WhenAll(resolveDetachTasks);

            var currentDbContainers = resolvedExistingContainers.Where(name => !string.IsNullOrEmpty(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var detachDbContainers = resolvedDetachContainers.Where(name => !string.IsNullOrEmpty(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var remainingDbContainers = currentDbContainers.Except(detachDbContainers, StringComparer.OrdinalIgnoreCase).ToList();

            var finalResolvedDbHosts = new List<string>();
            var finalDbTypes = new List<string>();
            string chosenNetwork = "ChatOps-net";

            if (remainingDbContainers.Any())
            {
                foreach (var dbContainerName in remainingDbContainers)
                {
                    if (dbContainerName == null) continue;

                    string hostResolved = await ClusterRoutingService.ResolveHostFromContainerNameAsync(dbContainerName);
                    if (hostResolved.StartsWith("Không tìm thấy", StringComparison.OrdinalIgnoreCase))
                        return $"❌ Lỗi cấu trúc Cluster khi phân giải ngược lại DB còn giữ '{dbContainerName}': {hostResolved}";

                    string currentdbNodeIp = "";
                    var dbnode = await RedisContainerService.GetContainerAsync(dbContainerName);
                    if (dbnode.success && dbnode.nodes != null && dbnode.nodes.Count > 0)
                    {
                        foreach (var node in dbnode.nodes)
                        {
                            string[] rawvalue = node.Value.Split("|");
                            if (rawvalue[0] == dbContainerName)
                            {
                                currentdbNodeIp = node.Key;
                                break;
                            }
                        }
                    }

                    DockerContainer? dbContainer = null;
                    ContainerMetadata? dbMetadata = null;
                    ContainerNetworkDetails? dbNetworkDetails = null;

                    if (currentdbNodeIp == AppContext.ServerIP)
                    {
                        var dbTask = DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(dbContainerName);
                        var metaTask = DockerService.Read.DockerReadMetadata.GetMetadataAsync(dbContainerName);
                        var netTask = DockerService.Read.DockerReadNetwork.GetContainerNetworkDetailsAsync(dbContainerName);
                        await Task.WhenAll(dbTask, metaTask, netTask);

                        dbContainer = dbTask.Result;
                        dbMetadata = metaTask.Result;
                        dbNetworkDetails = netTask.Result;
                    }
                    else
                    {
                        var (db, metadata, networks) = await RedisService.RedisService.SendGetDetailContainerRequestAsync(currentdbNodeIp, dbContainerName);
                        dbContainer = db;
                        dbMetadata = metadata;
                        dbNetworkDetails = networks;
                    }

                    if (dbContainer == null || dbMetadata == null || dbNetworkDetails == null)
                        return $"❌ Lấy dữ liệu Container/Metadata/Network của DB container '{dbContainerName}' thất bại.";

                    string dbType = dbMetadata.DbType?.ToLower() ?? "";
                    if (string.IsNullOrWhiteSpace(dbType))
                        return $"❌ DB container '{dbContainerName}' chưa có nhãn định danh dbtype.";

                    finalResolvedDbHosts.Add(hostResolved);
                    if (!finalDbTypes.Contains(dbType))
                        finalDbTypes.Add(dbType);

                    if (chosenNetwork == "ChatOps-net" && dbNetworkDetails.Networks != null && dbNetworkDetails.Networks.Any())
                        chosenNetwork = dbNetworkDetails.Networks.FirstOrDefault() ?? "ChatOps-net";
                }
            }

            string toolOwner = toolMetadata.Owners != null && toolMetadata.Owners.Count > 0 ? string.Join(",", toolMetadata.Owners) : "unknown";
            string toolDomain = toolMetadata.Domain;
            string serviceKey = toolMetadata.Service;
            string toolOldPort = toolContainer.OutPorts;

            string labelArgs = ChatDeployService.BuildDockerLabels(serviceKey, dockerImage: null!, serviceType: null!, toolDomain, toolOwner);

            ImageCategories.ImageServices.TryGetValue(toolImage, out var service);

            string finalAllocatedPorts = parsed.GetValueOrDefault("port", "");
            if (string.IsNullOrEmpty(finalAllocatedPorts))
                finalAllocatedPorts = toolContainer.OutPorts;

            var extPorts = finalAllocatedPorts
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var p) ? p : (int?)null)
                .Where(p => p.HasValue)
                .Select(p => p!.Value)
                .ToList();

            string rawInPorts = service.InPort ?? "";
            var inPorts = rawInPorts
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var p) ? p : (int?)null)
                .Where(p => p.HasValue)
                .Select(p => p!.Value)
                .ToList();

            string portMapping = extPorts.Any(p => p > 0)
                ? string.Join(" ", extPorts.Zip(inPorts, (ext, def) => $"-p {ext}:{def}"))
                : "";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"⚙️ [Node {AppContext.ServerID}] Đồng bộ cấu hình thành công. Khởi chạy chiến thuật sao lưu dữ liệu hạ tầng (Rollback Boundary)...");

            string backupToolName = $"{toolName}_backup";
            bool hasRemoteBackup = false;

            try
            {
                if (currentToolNodeIp.Equals(AppContext.ServerIP, StringComparison.OrdinalIgnoreCase))
                {
                    if (toolContainer.IsRunning)
                    {
                        string renameStatus = await DockerService.Update.DockerUpdateLifecycle.RenameContainerAsync(toolName, backupToolName);
                        if (renameStatus != "SUCCESS") throw new Exception($"Lỗi tạo bản sao tại Master: {renameStatus}");
                        hasRemoteBackup = true;
                        await RedisContainerService.DeleteContainerAsync(toolName);
                        await RedisContainerService.UpdateContainerValueAsync(AppContext.ServerIP, backupToolName, toolOldPort);
                    }
                }
                else
                {
                    var noderesult = await RedisNodeService.GetNodeAsync(currentToolNodeIp);
                    string nodeid = noderesult.nodes[$"{currentToolNodeIp}"];
                    await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] [Remote Backup] Gửi yêu cầu đổi tên tới Worker Node ({nodeid})...");
                    string remoteRenameResult = await RedisService.RedisService.SendRenameContainerRequestAsync(currentToolNodeIp, toolName, backupToolName);

                    if (remoteRenameResult.StartsWith("ERROR") || remoteRenameResult.StartsWith("TIMEOUT"))
                        throw new Exception($"Không thể tạo bản sao dự phòng tại Node từ xa. Chi tiết: {remoteRenameResult}");

                    hasRemoteBackup = true;
                    await RedisContainerService.DeleteContainerAsync(toolName);
                    await RedisContainerService.UpdateContainerValueAsync(currentToolNodeIp, backupToolName, toolOldPort);
                }

                await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] [Deploy] Đang khởi tạo Tool mới mang cấu hình tách rời trên Cluster...");

                string result = isPhpMyAdmin
                    ? await ChatDeployService.RunPhpMyAdmin(toolImage, finalResolvedDbHosts, finalDbTypes, chosenNetwork, portMapping, toolName, labelArgs, session, connectionId)
                    : await ChatDeployService.RunPgAdmin(toolImage, finalResolvedDbHosts, finalDbTypes, chosenNetwork, portMapping, toolName, labelArgs, session, connectionId, ImageCategories.ImageServices["dpage/pgadmin4:latest"].Env);

                if (extPorts.Any(p => p > 0))
                    await RedisPortService.ReleasePortsAsync(AppContext.ServerIP, extPorts.ToArray());

                if (string.IsNullOrWhiteSpace(result) || result.Contains("error", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Docker Engine từ chối áp dụng cấu hình tách rời mới. Chi tiết: {result}");

                if (hasRemoteBackup)
                {
                    await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] [Clean] Tách rời hoàn tất! Tiến hành giải phóng và xóa bản sao dự phòng...");

                    if (currentToolNodeIp.Equals(AppContext.ServerIP, StringComparison.OrdinalIgnoreCase))
                    {
                        await DockerService.Delete.DockerDeleteContainer.RemoveContainerForceAsync(backupToolName);
                        await RedisContainerService.DeleteContainerAsync(backupToolName);
                    }
                    else
                    {
                        await RedisService.RedisService.SendDeleteContainerRequestAsync(currentToolNodeIp, backupToolName);
                        await RedisContainerService.DeleteContainerAsync(backupToolName);
                    }

                    await RedisContainerService.UpdateContainerValueAsync(AppContext.ServerIP, toolName, finalAllocatedPorts);
                    await RedisDomainService.DeleteDomainAsync(toolDomain);
                    foreach (var port in extPorts)
                    {
                        await RedisDomainService.InsertDomainAsync(toolDomain, AppContext.ServerIP + $":{port}");
                    }
                }
            }
            catch (Exception ex)
            {
                await SendLogWithDelayAsync(session.Debug, connectionId, $"⚠️ [Node {AppContext.ServerID}] [Rollback] Phát hiện sự cố: {ex.Message}. Đang khôi phục lại trạng thái cũ...");

                if (await DockerService.Read.DockerReadContainer.IsContainerExistsAsync(toolName))
                {
                    await DockerService.Delete.DockerDeleteContainer.RemoveContainerForceAsync(toolName);
                    await RedisContainerService.DeleteContainerAsync(toolName);
                }

                if (hasRemoteBackup)
                {
                    if (currentToolNodeIp.Equals(AppContext.ServerIP, StringComparison.OrdinalIgnoreCase))
                    {
                        await DockerService.Update.DockerUpdateLifecycle.RenameContainerAsync(backupToolName, toolName);
                        await SystemCommandService.RunAsync($"docker start {toolName}");
                        await RedisContainerService.DeleteContainerAsync(backupToolName);
                        await RedisContainerService.UpdateContainerValueAsync(AppContext.ServerIP, toolName, toolOldPort);
                    }
                    else
                    {
                        await RedisService.RedisService.SendRenameContainerRequestAsync(currentToolNodeIp, backupToolName, toolName);
                        await RedisContainerService.DeleteContainerAsync(backupToolName);
                        await RedisContainerService.UpdateContainerValueAsync(currentToolNodeIp, toolName, toolOldPort);
                    }
                    return $"❌ Tiến trình Detach DB thất bại. Hệ thống đã tự động khôi phục lại Tool về trạng thái liên kết ban đầu tại Node: {currentToolNodeIp}.\nChi tiết lỗi: {ex.Message}";
                }

                return $"❌ Tiến trình lỗi nặng và không thể phục hồi dữ liệu từ bản backup.\nChi tiết lỗi: {ex.Message}";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Kết thúc tiến trình thành công. Đang kết xuất báo cáo...");

            var toolNetworkDetails1 = await DockerService.Read.DockerReadNetwork.GetContainerNetworkDetailsAsync(toolName);
            string finalNetworks = toolNetworkDetails1?.Networks != null && toolNetworkDetails1.Networks.Count > 0
                ? string.Join(",", toolNetworkDetails1.Networks)
                : "Không xác định";

            string remainingDbStatus = finalResolvedDbHosts.Any() ? string.Join(",", finalResolvedDbHosts) : "Trống (Đã cô lập hoàn toàn)";

            return $"✅ Detach DB thành công\n🧠 Tool: {toolName}\n🗄 Danh sách các DB còn duy trì kết nối: [{remainingDbStatus}]\n🗄 Các DB vừa gỡ: {dbInput}\n🌐 Port Host: {portMapping}\n🌐 Toàn bộ mạng kết nối hiện tại: {finalNetworks}";
        }
    }
}
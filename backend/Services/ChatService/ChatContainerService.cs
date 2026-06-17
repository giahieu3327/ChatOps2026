using ChatOps.Data;
using ChatOps.Models;
using ChatOps.Services.FileService;
using ChatOps.Services.RedisService;
using ChatOps.Services.SystemService;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.ChatService
{
    public static class ChatContainerService
    {
        private static async Task SendLogWithDelayAsync(bool debug, string connectionId, string message)
        {
            await Task.Delay(100);
            await RedisChannelService.SendMessageToClientAsync(debug, connectionId, message);
        }

        public static async Task<string> ListContainer(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline ListContainer...");

            bool isAllCommand = parsed.TryGetValue("all", out var allVal) && allVal == "true";
            string instanceFilter = parsed.GetValueOrDefault("instance", "").Trim().ToLower();

            bool isFullAccess =
                session.Role == "admin" ||
                session.Role == "manager" ||
                session.Role == "dev" ||
                session.Role == "ops";

            string header =
                "📊 DANH SÁCH CONTAINERS\n" +
                "ID | IMAGE | STATUS | PORTS | NAMES | DOMAIN | OWNER\n" +
                "-------------------------------------------------------------------------\n";

            List<DockerContainer> containers = await DockerService.Read.DockerReadContainer.GetContainersAsync();

            if (containers.Count == 0)
                return header + "Không có container nào.";

            var result = new List<string>();

            foreach (var container in containers)
            {
                if (container == null) continue;

                string id = container.Id;
                string image = container.Image;
                string status = container.Status.ToLower();
                string ports = container.Ports;
                string name = container.Name;

                if (!isAllCommand && (status.Contains("exited") || status.Contains("created") || status.Contains("dead")))
                {
                    continue;
                }

                ContainerMetadata? metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(name);
                if (metadata == null)
                    continue;

                if (!string.IsNullOrWhiteSpace(instanceFilter))
                {
                    string app = metadata.AppProject?.ToLower() ?? "";
                    if (app != instanceFilter)
                        continue;
                }

                var ownerList = metadata.Owners;

                if (!isFullAccess)
                {
                    if (ownerList == null || !ownerList.Contains(session.Username))
                        continue;
                }

                string displayDomain = !string.IsNullOrWhiteSpace(metadata.Domain) ? metadata.Domain : "N/A";

                string displayOwner = isFullAccess
                    ? (ownerList != null && ownerList.Count > 0 ? string.Join(",", ownerList) : "system")
                    : session.Username;

                result.Add($"{id}|{image}|{container.Status}|{ports}|{name}|{displayDomain}|{displayOwner}");
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Docker API] Hoàn tất xử lý dữ liệu.");

            if (result.Count == 0)
                return header + "Không có container phù hợp với điều kiện lọc hoặc quyền truy cập.";

            return header + string.Join("\n", result);
        }

        public static async Task<string> Start(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline Start...");

            bool isAllCommand = parsed.TryGetValue("all", out var allVal) && allVal == "true";
            parsed.TryGetValue("instance", out string? instanceStr);
            parsed.TryGetValue("container", out string? containerStr);

            string instanceFilter = instanceStr?.Trim().ToLower() ?? "";
            string containerFilter = containerStr?.Trim().ToLower() ?? "";

            if (!isAllCommand && string.IsNullOrWhiteSpace(instanceFilter) && string.IsNullOrWhiteSpace(containerFilter))
                return "❌ Thiếu tham số! Vui lòng nhập ít nhất tham số 'all', 'instance' hoặc 'container'.";

            var result = new List<string>();

            if (isAllCommand)
            {
                List<DockerContainer> stoppedContainers = await DockerService.Read.DockerReadContainer.GetStoppedContainersAsync();
                if (stoppedContainers.Count == 0)
                    return "✅ Không có container nào đang ở trạng thái stop.";

                foreach (var container in stoppedContainers)
                {
                    if (container == null) continue;
                    string name = container.Name;

                    if (!await CanOperate(session, name))
                        continue;

                    result.Add(await DockerService.Update.DockerUpdateLifecycle.StartContainerAsync(name));
                }

                return result.Count == 0
                    ? "❌ Không có container nào phù hợp với quyền hạn của bạn."
                    : "🚀 Khởi động thành công các container:\n" + string.Join("\n", result);
            }

            if (!string.IsNullOrWhiteSpace(containerFilter))
            {
                DockerContainer? containerDetail = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(containerFilter);

                if (containerDetail == null || string.IsNullOrWhiteSpace(containerDetail.Name))
                    return "❌ Container không tồn tại hoặc hệ thống gặp lỗi khi truy vấn.";

                if (!await CanOperate(session, containerDetail.Name))
                    return "❌ Bạn không có quyền khởi động container này.";

                string runResult = await DockerService.Update.DockerUpdateLifecycle.StartContainerAsync(containerDetail.Name);

                return "🚀 Khởi động thành công:\n" + runResult;
            }

            List<DockerContainer> stoppedContainersList = await DockerService.Read.DockerReadContainer.GetStoppedContainersAsync();

            foreach (var container in stoppedContainersList)
            {
                if (container == null) continue;
                string name = container.Name;

                ContainerMetadata? metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(name);
                if (metadata == null) continue;
                string app = metadata.AppProject?.Trim().ToLower() ?? "";

                if (app != instanceFilter)
                    continue;

                if (!await CanOperate(session, name))
                    continue;

                result.Add(await DockerService.Update.DockerUpdateLifecycle.StartContainerAsync(name));
            }

            return result.Count == 0
                ? $"❌ Không tìm thấy container nào đang tắt thuộc cụm App '{instanceFilter}' phù hợp với quyền của bạn."
                : $"🚀 Khởi động cụm App '{instanceFilter}' thành công:\n" + string.Join("\n", result);
        }

        public static async Task<string> Stop(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline Stop...");

            bool isAllCommand = parsed.TryGetValue("all", out var allVal) && allVal == "true";
            parsed.TryGetValue("instance", out string? instanceStr);
            parsed.TryGetValue("container", out string? containerStr);

            string instanceFilter = instanceStr?.Trim().ToLower() ?? "";
            string containerFilter = containerStr?.Trim().ToLower() ?? "";

            if (!isAllCommand && string.IsNullOrWhiteSpace(instanceFilter) && string.IsNullOrWhiteSpace(containerFilter))
                return "❌ Thiếu tham số! Vui lòng nhập ít nhất tham số 'all', 'instance' hoặc 'container'.";

            var result = new List<string>();

            if (isAllCommand)
            {
                List<DockerContainer> runningContainers = await DockerService.Read.DockerReadContainer.GetContainersAsync();
                if (runningContainers.Count == 0)
                    return "✅ Không có container nào đang chạy.";

                foreach (var container in runningContainers)
                {
                    if (container == null) continue;
                    string name = container.Name;

                    if (!await CanOperate(session, name))
                        continue;

                    result.Add(await DockerService.Update.DockerUpdateLifecycle.StopContainerAsync(name));
                }

                return result.Count == 0
                    ? "❌ Không có container nào phù hợp với quyền hạn của bạn."
                    : "🛑 Dừng thành công các container:\n" + string.Join("\n", result);
            }

            if (!string.IsNullOrWhiteSpace(containerFilter))
            {
                DockerContainer? containerDetail = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(containerFilter);

                if (containerDetail == null || string.IsNullOrWhiteSpace(containerDetail.Name))
                    return "❌ Container không tồn tại hoặc hệ thống gặp lỗi khi truy vấn.";

                if (!await CanOperate(session, containerDetail.Name))
                    return "❌ Bạn không có quyền dừng container này.";

                string stopResult = await DockerService.Update.DockerUpdateLifecycle.StopContainerAsync(containerDetail.Name);

                return "🛑 Dừng thành công:\n" + stopResult;
            }

            List<DockerContainer> containersList = await DockerService.Read.DockerReadContainer.GetContainersAsync();

            foreach (var container in containersList)
            {
                if (container == null) continue;
                string name = container.Name;

                ContainerMetadata? metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(name);
                if (metadata == null) continue;

                string app = metadata.AppProject?.Trim().ToLower() ?? "";

                if (app != instanceFilter)
                    continue;

                if (!await CanOperate(session, name))
                    continue;

                result.Add(await DockerService.Update.DockerUpdateLifecycle.StopContainerAsync(name));
            }

            return result.Count == 0
                ? $"❌ Không tìm thấy container nào đang chạy thuộc cụm App '{instanceFilter}' phù hợp với quyền của bạn."
                : $"🛑 Dừng cụm App '{instanceFilter}' thành công:\n" + string.Join("\n", result);
        }

        public static async Task<string> Kill(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline Kill...");

            bool isAllCommand = parsed.TryGetValue("all", out var allVal) && allVal == "true";
            parsed.TryGetValue("instance", out string? instanceStr);
            parsed.TryGetValue("container", out string? containerStr);

            string instanceFilter = instanceStr?.Trim().ToLower() ?? "";
            string containerFilter = containerStr?.Trim().ToLower() ?? "";

            if (!isAllCommand && string.IsNullOrWhiteSpace(instanceFilter) && string.IsNullOrWhiteSpace(containerFilter))
                return "❌ Thiếu tham số! Vui lòng nhập ít nhất tham số 'all', 'instance' hoặc 'container'.";

            var result = new List<string>();

            if (isAllCommand)
            {
                List<DockerContainer> runningContainers = await DockerService.Read.DockerReadContainer.GetContainersAsync();
                if (runningContainers.Count == 0)
                    return "✅ Không có container nào đang chạy.";

                foreach (var container in runningContainers)
                {
                    if (container == null) continue;
                    string name = container.Name;

                    if (!await CanOperate(session, name))
                        continue;

                    result.Add(await DockerService.Update.DockerUpdateLifecycle.KillContainerAsync(name));
                }

                return result.Count == 0
                    ? "❌ Không có container nào phù hợp với quyền hạn của bạn."
                    : "💀 Cưỡng bức dừng thành công các container:\n" + string.Join("\n", result);
            }

            if (!string.IsNullOrWhiteSpace(containerFilter))
            {
                DockerContainer? containerDetail = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(containerFilter);

                if (containerDetail == null || string.IsNullOrWhiteSpace(containerDetail.Name))
                    return "❌ Container không tồn tại hoặc hệ thống gặp lỗi khi truy vấn.";

                if (!await CanOperate(session, containerDetail.Name))
                    return "❌ Bạn không có quyền kill container này.";

                string killResult = await DockerService.Update.DockerUpdateLifecycle.KillContainerAsync(containerDetail.Name);

                return "💀 Killed:\n" + killResult;
            }

            List<DockerContainer> activeContainers = await DockerService.Read.DockerReadContainer.GetContainersAsync();

            foreach (var container in activeContainers)
            {
                if (container == null) continue;
                string name = container.Name;

                ContainerMetadata? metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(name);
                if (metadata == null) continue;

                string app = metadata.AppProject?.Trim().ToLower() ?? "";

                if (app != instanceFilter)
                    continue;

                if (!await CanOperate(session, name))
                    continue;

                result.Add(await DockerService.Update.DockerUpdateLifecycle.KillContainerAsync(name));
            }

            return result.Count == 0
                ? $"❌ Không tìm thấy container nào đang chạy thuộc cụm App '{instanceFilter}' phù hợp với quyền của bạn."
                : $"💀 Cưỡng bức dừng cụm App '{instanceFilter}' thành công:\n" + string.Join("\n", result);
        }

        public static async Task<string> Restart(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline Restart...");

            bool isAllCommand = parsed.TryGetValue("all", out var allVal) && allVal == "true";
            parsed.TryGetValue("instance", out string? instanceStr);
            parsed.TryGetValue("container", out string? containerStr);

            string instanceFilter = instanceStr?.Trim().ToLower() ?? "";
            string containerFilter = containerStr?.Trim().ToLower() ?? "";

            if (!isAllCommand && string.IsNullOrWhiteSpace(instanceFilter) && string.IsNullOrWhiteSpace(containerFilter))
                return "❌ Thiếu tham số! Vui lòng nhập ít nhất tham số 'all', 'instance' hoặc 'container'.";

            var result = new List<string>();

            if (isAllCommand)
            {
                List<DockerContainer> runningContainers = await DockerService.Read.DockerReadContainer.GetContainersAsync();
                if (runningContainers.Count == 0)
                    return "✅ Không có container nào đang chạy.";

                foreach (var container in runningContainers)
                {
                    if (container == null) continue;
                    string name = container.Name;

                    if (!await CanOperate(session, name))
                        continue;

                    result.Add(await DockerService.Update.DockerUpdateLifecycle.RestartContainerAsync(name));
                }

                return result.Count == 0
                    ? "❌ Không có container nào phù hợp với quyền hạn của bạn."
                    : "🔄 Khởi động lại thành công các container:\n" + string.Join("\n", result);
            }

            if (!string.IsNullOrWhiteSpace(containerFilter))
            {
                DockerContainer? containerDetail = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(containerFilter);

                if (containerDetail == null || string.IsNullOrWhiteSpace(containerDetail.Name))
                    return "❌ Container không tồn tại hoặc hệ thống gặp lỗi khi truy vấn.";

                if (!await CanOperate(session, containerDetail.Name))
                    return "❌ Bạn không có quyền restart container này.";

                string restartResult = await DockerService.Update.DockerUpdateLifecycle.RestartContainerAsync(containerDetail.Name);

                return "🔄 Restarted:\n" + restartResult;
            }

            List<DockerContainer> activeContainers = await DockerService.Read.DockerReadContainer.GetContainersAsync();

            foreach (var container in activeContainers)
            {
                if (container == null) continue;
                string name = container.Name;

                ContainerMetadata? metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(name);
                if (metadata == null) continue;
                string app = metadata.AppProject?.Trim().ToLower() ?? "";

                if (app != instanceFilter)
                    continue;

                if (!await CanOperate(session, name))
                    continue;

                result.Add(await DockerService.Update.DockerUpdateLifecycle.RestartContainerAsync(name));
            }

            return result.Count == 0
                ? $"❌ Không tìm thấy container nào đang chạy thuộc cụm App '{instanceFilter}' phù hợp với quyền của bạn."
                : $"🔄 Khởi động lại cụm App '{instanceFilter}' thành công:\n" + string.Join("\n", result);
        }

        public static async Task<string> Remove(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline Remove...");

            bool isAllCommand = parsed.TryGetValue("all", out var allVal) && allVal == "true";
            parsed.TryGetValue("instance", out string? instanceStr);
            parsed.TryGetValue("container", out string? containerStr);

            string instanceFilter = instanceStr?.Trim().ToLower() ?? "";
            string containerFilter = containerStr?.Trim().ToLower() ?? "";

            if (!isAllCommand && string.IsNullOrWhiteSpace(instanceFilter) && string.IsNullOrWhiteSpace(containerFilter))
                return "❌ Thiếu tham số! Vui lòng nhập ít nhất tham số 'all', 'instance' hoặc 'container'.";

            var result = new List<string>();
            var processedApps = new HashSet<string>();

            if (isAllCommand)
            {
                List<DockerContainer> allContainers = await DockerService.Read.DockerReadContainer.GetContainersAsync();
                var qualifiedContainers = new List<(string Name, string InstanceApp)>();

                foreach (var container in allContainers)
                {
                    if (container == null) continue;
                    string name = container.Name;

                    if (!await CanOperate(session, name))
                        continue;

                    ContainerMetadata? metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(name);
                    string instanceApp = metadata?.AppProject?.Trim().ToLower() ?? "";

                    qualifiedContainers.Add((name, instanceApp));
                }

                foreach (var item in qualifiedContainers)
                {
                    if (!string.IsNullOrEmpty(item.InstanceApp))
                    {
                        if (processedApps.Contains(item.InstanceApp))
                            continue;

                        await CleanupEntireInstanceApp(item.InstanceApp, session, result);
                        processedApps.Add(item.InstanceApp);
                    }
                    else
                    {
                        await CleanupContainerResources(item.Name, result);
                    }
                }

                await CleanupGlobalResources(result);

                return result.Count == 0
                    ? "✅ Không tìm thấy container nào phù hợp quyền hạn của bạn để xóa."
                    : "⚠️ SCOPE CLEANED:\n" + string.Join("\n", result);
            }

            if (!string.IsNullOrWhiteSpace(containerFilter))
            {
                DockerContainer? containerDetail = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(containerFilter);

                if (containerDetail == null || string.IsNullOrWhiteSpace(containerDetail.Name))
                    return "❌ Container không tồn tại hoặc hệ thống gặp lỗi khi truy vấn.";

                if (!await CanOperate(session, containerDetail.Name))
                    return "❌ Bạn không có quyền xóa container này.";

                ContainerMetadata? metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(containerDetail.Name);
                string instanceApp = metadata?.AppProject?.Trim().ToLower() ?? "";

                if (!string.IsNullOrEmpty(instanceApp))
                {
                    await CleanupEntireInstanceApp(instanceApp, session, result);
                }
                else
                {
                    await CleanupContainerResources(containerDetail.Name, result);
                }

                await CleanupGlobalResources(result);

                return "⚠️ Removed Resources:\n" + string.Join("\n", result);
            }

            await CleanupEntireInstanceApp(instanceFilter, session, result);

            if (result.Count == 0)
                return $"❌ Không tìm thấy container hoặc tài nguyên nào thuộc cụm App '{instanceFilter}' phù hợp quyền của bạn.";

            await CleanupGlobalResources(result);

            return $"⚠️ Removed app '{instanceFilter}':\n" + string.Join("\n", result);
        }

        public static async Task<string> RemoveImage(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline RemoveImage...");

            bool isAllCommand = parsed.TryGetValue("all", out var allVal) && allVal == "true";
            parsed.TryGetValue("image", out string? imageStr);

            string imageFilter = imageStr?.Trim() ?? "";

            if (!isAllCommand && string.IsNullOrWhiteSpace(imageFilter))
                return "❌ Thiếu tham số! Vui lòng nhập ít nhất tham số 'all' hoặc tên/ID 'image'.";

            if (isAllCommand)
            {
                try
                {
                    string pruneOutput = await DockerService.Delete.DockerDeleteImage.PruneImagesAsync();
                    return "🧹 SYSTEM IMAGES PRUNED:\n" + pruneOutput;
                }
                catch (Exception ex)
                {
                    return $"❌ Lỗi khi dọn dẹp toàn bộ image trên hệ thống: {ex.Message}";
                }
            }

            try
            {
                string rmiOutput = await DockerService.Delete.DockerDeleteImage.RemoveImageAsync(imageFilter);
                return "⚠️ Removed Image:\n" + rmiOutput;
            }
            catch (Exception ex)
            {
                return $"❌ Không thể thực hiện xóa image '{imageFilter}':\n{ex.Message}";
            }
        }

        public static async Task<string> Rename(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline Rename...");

            parsed.TryGetValue("container", out string? containerStr);
            parsed.TryGetValue("newcname", out string? newcontainerStr);

            string containerTarget = containerStr?.Trim() ?? "";
            string newContainerName = newcontainerStr?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(containerTarget) || string.IsNullOrWhiteSpace(newContainerName))
            {
                return "❌ Thiếu tham số! Vui lòng nhập đầy đủ cả tham số 'container' và 'newcname'.";
            }

            DockerContainer? containerDetail = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(containerTarget);
            if (containerDetail == null || string.IsNullOrWhiteSpace(containerDetail.Name))
                return "❌ Container không tồn tại hoặc hệ thống gặp lỗi khi truy vấn.";

            string containerName = containerDetail.Name;

            ContainerMetadata? metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(containerName);
            string instanceApp = metadata?.AppProject?.Trim() ?? "";

            if (!string.IsNullOrEmpty(instanceApp))
            {
                return $"❌ Lỗi nghiệp vụ! Container '{containerName}' đang thuộc cụm App '{instanceApp}'. Hệ thống chỉ hỗ trợ đổi tên (setcname) cho container độc lập.";
            }

            if (!await CanOperate(session, containerName))
            {
                return "❌ Bạn không có quyền thao tác trên container này.";
            }

            bool exists = await DockerService.Read.DockerReadContainer.IsContainerExistsAsync(newContainerName);

            if (exists)
            {
                return $"❌ Đổi tên thất bại! Tên container '{newContainerName}' đã tồn tại trên hệ thống.";
            }

            string renameResult = await DockerService.Update.DockerUpdateLifecycle.RenameContainerAsync(containerName, newContainerName);

            if (renameResult.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                return $"❌ Lỗi từ Docker Engine, cấu trúc rename thất bại:\n{renameResult}";
            }

            try
            {
                var networkDetail = await DockerService.Read.DockerReadNetwork.GetContainerNetworkDetailsAsync(newContainerName);
                if (networkDetail != null)
                {
                    var networks = networkDetail.Networks;
                    string role = metadata?.ComposeService ?? "";

                    foreach (var net in networks)
                    {
                        if (net == "bridge" || net == "host" || net == "none") continue;

                        await DockerService.Delete.DockerDeleteNetworkStorage.DisconnectNetworkAsync(net, newContainerName);
                        await DockerService.Update.DockerUpdateNetwork.ConnectNetworkAsync(net, newContainerName, newContainerName, role);
                    }
                }
            }
            catch
            {
                // Thực thi ngầm để tránh crash luồng chính nếu gãy kết nối mạng tạm thời
            }

            return $"✅ RENAME SUCCESS\n\n" +
                $"📦 Old Name: {containerName}\n" +
                $"📦 New Name: {newContainerName}";
        }

        public static async Task<string> Setcusername(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline Setcusername...");

            parsed.TryGetValue("instance", out string? instanceStr);
            parsed.TryGetValue("container", out string? containerStr);
            parsed.TryGetValue("newcusername", out string? newcusernameStr);

            string instanceFilter = instanceStr?.Trim().ToLower() ?? "";
            string containerFilter = containerStr?.Trim().ToLower() ?? "";
            string extraUser = newcusernameStr?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(instanceFilter) && string.IsNullOrWhiteSpace(containerFilter))
                return "❌ Thiếu tham số! Vui lòng chỉ định đối tượng thay đổi qua tham số 'instance' hoặc 'container'.";

            string owner = "company";
            if (session.Role == "dev" || session.Role == "ops")
                owner = session.Role;

            if (!string.IsNullOrWhiteSpace(extraUser))
                owner += $",{extraUser}";

            if (!string.IsNullOrWhiteSpace(instanceFilter))
            {
                return await SetInstanceOwnerAsync(instanceFilter, session, connectionId, owner);
            }
            else
            {
                return await SetSingleContainerOwnerAsync(containerFilter, session, connectionId, owner);
            }
        }

        public static async Task<string> Setcdomain(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline Setcdomain...");

            parsed.TryGetValue("instance", out string? instanceStr);
            parsed.TryGetValue("container", out string? containerStr);
            parsed.TryGetValue("newcdomain", out string? newcdomainStr);

            string instanceFilter = instanceStr?.Trim().ToLower() ?? "";
            string containerFilter = containerStr?.Trim().ToLower() ?? "";
            string newcdomain = newcdomainStr?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(instanceFilter) && string.IsNullOrWhiteSpace(containerFilter))
                return "❌ Thiếu tham số! Vui lòng chỉ định đối tượng thay đổi qua tham số 'instance' hoặc 'container'.";

            string newfulldomain = $"{newcdomain}.{AppContext.ServerDomain}";

            if (!string.IsNullOrWhiteSpace(instanceFilter))
            {
                return await SetInstanceOwnerAsync(instanceFilter, session, connectionId, null, newfulldomain);
            }
            else
            {
                return await SetSingleContainerOwnerAsync(containerFilter, session, connectionId, null, newfulldomain);
            }
        }

        public static async Task<string> Inspect(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động pipeline Inspect...");

            parsed.TryGetValue("container", out string? containerStr);
            string containerFilter = containerStr?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(containerFilter))
            {
                return "❌ Thiếu tham số! Vui lòng chỉ định tên hoặc ID container qua tham số 'container'.";
            }

            bool isFullAccess =
                session.Role == "admin" ||
                session.Role == "manager" ||
                session.Role == "dev" ||
                session.Role == "ops";

            DockerContainer? containerdetail = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(containerFilter);

            if (containerdetail == null ||
                string.IsNullOrWhiteSpace(containerdetail.Name) ||
                containerdetail.Name.Contains("<no value>") ||
                containerdetail.Name.Contains("error"))
            {
                return $"❌ Không tìm thấy hoặc hệ thống gặp lỗi khi truy vấn container: '{containerFilter}'";
            }

            string containerName = containerdetail.Name;

            var networkdetail = await DockerService.Read.DockerReadNetwork.GetContainerNetworkDetailsAsync(containerName);
            var metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(containerName);

            var ownerList = metadata?.Owners;
            if (!isFullAccess)
            {
                if (ownerList == null || !ownerList.Contains(session.Username))
                {
                    return "❌ Bạn không có quyền truy cập thông tin cấu hình chi tiết của container này.";
                }
            }

            string ownersDisplay = isFullAccess
                ? (ownerList != null && ownerList.Count > 0 ? string.Join(", ", ownerList) : "system")
                : session.Username;

            string networksDisplay = (networkdetail?.Networks != null && networkdetail.Networks.Count > 0)
                ? string.Join(", ", networkdetail.Networks)
                : "None";

            string labelsDisplay = (containerdetail.Labels != null && containerdetail.Labels.Count > 0)
                ? string.Join("\n", containerdetail.Labels.Select(l => $"  - {l.Key} = {l.Value}"))
                : "  (No labels found)";

            string envDisplay = (metadata?.Environments != null && metadata.Environments.Count > 0)
                ? string.Join("\n", metadata.Environments.Select(e => $"  - {e.Key} = {e.Value}"))
                : "  (No environment variables found)";

            return
                $"📋 CONTAINER INSPECT REPORT\n" +
                $"--------------------------------------------------\n\n" +

                $"📦 Core Information:\n" +
                $"  - Name: {containerName}\n" +
                $"  - ID: {containerdetail.Id}\n" +
                $"  - Image: {containerdetail.Image}\n" +
                $"  - Status: {containerdetail.Status} (Running: {containerdetail.IsRunning})\n\n" +

                $"👤 Ownership & Logic Cluster:\n" +
                $"  - Owners: {ownersDisplay}\n" +
                $"  - Service Key: {metadata?.Service ?? "N/A"}\n" +
                $"  - App Project: {metadata?.AppProject ?? "N/A"}\n" +
                $"  - Compose Service: {metadata?.ComposeService ?? "N/A"}\n" +
                $"  - Scale Index: {metadata?.ScaleIndex ?? "N/A"}\n\n" +

                $"🛜 Network & Routing Profiles:\n" +
                $"  - Domain Routing: {metadata?.Domain ?? "N/A"}\n" +
                $"  - Networks Connected: {networksDisplay}\n" +
                $"  - Raw Ports (ps): {containerdetail.Ports}\n" +
                $"  - Internal Ports (In): {containerdetail.InPorts}\n" +
                $"  - External Ports (Out): {containerdetail.OutPorts}\n\n" +

                $"💾 System Database & Policies:\n" +
                $"  - DB Type: {metadata?.DbType ?? "N/A"}\n" +
                $"  - Restart Policy: {metadata?.RestartPolicy ?? "no"}\n\n" +

                $"⚙️ Environment Variables:\n" +
                $"{envDisplay}\n\n" +

                $"🏷️ Docker Engine Labels:\n" +
                $"{labelsDisplay}\n";
        }

        private static async Task CleanupEntireInstanceApp(string instanceApp, UserSession session, List<string> result)
        {
            List<DockerContainer> containersList = await DockerService.Read.DockerReadContainer.GetContainersAsync();

            foreach (var container in containersList)
            {
                if (container == null) continue;
                string name = container.Name;

                ContainerMetadata? metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(name);
                string app = metadata?.AppProject?.Trim().ToLower() ?? "";

                if (app != instanceApp)
                    continue;

                if (!await CanOperate(session, name))
                    continue;

                await CleanupContainerResources(name, result);
            }

            CleanupInstanceAppDirectory(instanceApp, result);
        }

        private static async Task CleanupContainerResources(string containerName, List<string> result)
        {
            DockerContainer? containerDetail = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(containerName);
            string image = containerDetail?.Image?.ToLower() ?? "";
            ContainerMetadata? containermetadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(containerName);
            string domain = containermetadata?.Domain?.ToLower() ?? "";

            result.Add(await DockerService.Delete.DockerDeleteContainer.RemoveContainerForceAsync(containerName));
            await RedisContainerService.DeleteContainerAsync(containerName);

            if (!string.IsNullOrEmpty(domain))
            {
                await RedisDomainService.DeleteDomainAsync(domain);
            }

            CleanupContainerDirectory(image, containerName, result);
        }

        private static void CleanupInstanceAppDirectory(string instanceApp, List<string> result)
        {
            try
            {
                string appDir = Path.Combine("/home/ubuntu/ChatOps/docker/Apps", instanceApp);
                if (Directory.Exists(appDir))
                {
                    Directory.Delete(appDir, true);
                    result.Add($"🧹 App Directory Removed: {appDir}");
                }
            }
            catch (Exception ex)
            {
                result.Add($"⚠️ App directory wipe error: {ex.Message}");
            }
        }

        private static void CleanupContainerDirectory(string image, string containerName, List<string> result)
        {
            if (image.Contains("pgadmin4"))
            {
                try
                {
                    string targetDir = Path.Combine("/home/ubuntu/ChatOps/docker/Containers", containerName);
                    if (Directory.Exists(targetDir))
                    {
                        Directory.Delete(targetDir, true);
                        result.Add($"🧹 Host Directory Removed: {targetDir}");
                    }
                }
                catch (Exception ex)
                {
                    result.Add($"⚠️ Container directory wipe error: {ex.Message}");
                }
            }
        }

        private static async Task CleanupGlobalResources(List<string> result)
        {
            try
            {
                var volumes = await DockerService.Read.DockerReadStorage.GetVolumeListAsync();
                foreach (var volume in volumes)
                {
                    if (!string.IsNullOrWhiteSpace(volume))
                    {
                        result.Add(await DockerService.Delete.DockerDeleteNetworkStorage.RemoveVolumesAsync(volume));
                    }
                }
            }
            catch { }

            try
            {
                result.Add(await DockerService.Delete.DockerDeleteNetworkStorage.PruneNetworksAsync());
            }
            catch { }
        }

        private static async Task<bool> CanOperate(UserSession session, string containerName)
        {
            if (session.Role == "admin")
                return true;

            var meta = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(containerName);
            if (meta == null)
                return false;

            var ownerslist = meta.Owners;

            if (session.Role == "dev")
                return ownerslist.Contains("dev");

            if (session.Role == "ops")
                return ownerslist.Contains("ops");

            if (ownerslist.Contains("company"))
                return true;

            return false;
        }

        private static async Task<string> SetInstanceOwnerAsync(string instanceFilter, UserSession session, string connectionId, string? owners = null, string? domains = null)
        {
            await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang quét các container thuộc cụm App: '{instanceFilter}'...");

            List<DockerContainer> allContainers = await DockerService.Read.DockerReadContainer.GetContainersAsync();
            var qualifiedContainers = new List<string>();

            foreach (var container in allContainers)
            {
                if (container == null) continue;
                string name = container.Name;

                ContainerMetadata? metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(name);
                string app = metadata?.AppProject?.Trim().ToLower() ?? "";

                if (app != instanceFilter) continue;
                if (!await CanOperate(session, name)) continue;

                qualifiedContainers.Add(name);
            }

            if (qualifiedContainers.Count == 0)
                return $"❌ Không tìm thấy container nào thuộc App '{instanceFilter}' phù hợp với quyền hạn của bạn.";

            var result = new List<string>();
            string targetLogValue = owners ?? domains ?? "";
            await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] Đang gán thông số cấu hình hạ tầng mới '{targetLogValue}' cho các thành viên trong App...");

            foreach (var name in qualifiedContainers)
            {
                result.Add($"✅ Target Container Update Scheduled: {name} -> {targetLogValue}");
            }

            string runtimeDir = $"/home/ubuntu/ChatOps/docker/Apps/{instanceFilter}";
            string envPath = Path.Combine(runtimeDir, ".env");
            bool isEnvUpdated = false;

            try
            {
                if (File.Exists(envPath))
                {
                    await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"📝 [Node {AppContext.ServerID}] Đang ghi nhãn đồng bộ vào tệp `.env` hạ tầng...");

                    string[] lines = await File.ReadAllLinesAsync(envPath);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (owners != null)
                        {
                            if (lines[i].StartsWith("OWNER=", StringComparison.OrdinalIgnoreCase))
                            {
                                lines[i] = $"OWNER={owners}";
                            }
                        }
                        if (domains != null)
                        {
                            if (lines[i].StartsWith("DOMAIN=", StringComparison.OrdinalIgnoreCase))
                            {
                                lines[i] = $"DOMAIN={domains}";
                            }
                        }
                    }
                    await File.WriteAllLinesAsync(envPath, lines);
                    result.Add($"🧹 Host Configuration Synced: {envPath}");
                    isEnvUpdated = true;
                }
                else
                {
                    result.Add($"⚠️ Không tìm thấy file .env tại {runtimeDir} để ghi đè.");
                }
            }
            catch (Exception ex)
            {
                result.Add($"⚠️ Host environment sync error: {ex.Message}");
            }

            if (isEnvUpdated)
            {
                try
                {
                    await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🚀 [Node {AppContext.ServerID}] Khởi động tiến trình nạp lại cấu hình toàn diện cho cụm App...");

                    string cdCmd = $"cd {runtimeDir}";
                    string coreComposeFile = "docker-git.yml";
                    if (!File.Exists(Path.Combine(runtimeDir, coreComposeFile)))
                    {
                        coreComposeFile = "docker-registry.yml";
                    }

                    if (File.Exists(Path.Combine(runtimeDir, coreComposeFile)))
                    {
                        await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🚚 [Node {AppContext.ServerID}] Đang ép tái khởi động các dịch vụ Core App ({coreComposeFile})...");
                        string coreOutput = await Task.Run(async () => await SystemCommandService.RunAsync($"{cdCmd} && docker compose -f {coreComposeFile} up -d --build --force-recreate 2>&1"));
                        result.Add($"⚙️ Core App Recreated ({coreComposeFile}):\n{coreOutput}");
                    }

                    string lbComposeFile = "docker-compose-lb.yml";
                    if (File.Exists(Path.Combine(runtimeDir, lbComposeFile)))
                    {
                        await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"⚖️ [Node {AppContext.ServerID}] Đang làm mới hệ thống cân bằng tải biên dịch ({lbComposeFile})...");
                        string lbOutput = await Task.Run(async () => await SystemCommandService.RunAsync($"{cdCmd} && docker compose -f {lbComposeFile} up -d --build --force-recreate 2>&1"));
                        result.Add($"⚙️ Load Balancer Recreated:\n{lbOutput}");
                    }
                }
                catch (Exception ex)
                {
                    result.Add($"❌ Gặp lỗi nghiêm trọng khi ra lệnh ép tái khởi động hạ tầng: {ex.Message}");
                }
            }

            return "⚠️ CONFIGURATION UPDATED AND APP REBOOTED:\n" + string.Join("\n", result);
        }

        private static async Task<string> SetSingleContainerOwnerAsync(string containerFilter, UserSession session, string connectionId, string? owners = null, string? domains = null)
        {
            await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang truy vấn cấu trúc container chỉ định: '{containerFilter}'...");

            DockerContainer? dockerContainer = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(containerFilter);
            string containerName = dockerContainer?.Name ?? "";

            if (dockerContainer == null || string.IsNullOrWhiteSpace(dockerContainer.Name))
                return "❌ Container không tồn tại hoặc hệ thống gặp lỗi khi truy vấn.";

            ContainerMetadata? containerMetadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(containerName);
            string parentApp = containerMetadata?.AppProject?.Trim() ?? "";

            if (!string.IsNullOrEmpty(parentApp))
            {
                return $"❌ Lỗi nghiệp vụ! Container '{containerName}' đang thuộc cụm App '{parentApp}'. Để đổi cấu hình và nạp lại dự án, vui lòng sử dụng tham số 'instance'.";
            }

            if (!await CanOperate(session, containerName))
            {
                return "❌ Bạn không có quyền chuyển giao quyền quản lý container này.";
            }

            try
            {
                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"⚙️ [Node {AppContext.ServerID}] Đang bóc tách thông số mạng và cấu hình phục vụ Re-deploy chuyên sâu...");

                string dockerImage = dockerContainer.Image;
                ImageCategories.ImageServices.TryGetValue(dockerImage, out var service);

                string serviceType = service.Type ?? "web";
                string serviceKey = containerMetadata?.Service ?? "custom";

                List<int> extPorts = new List<int>();
                List<int> inPorts = new List<int>();
                string? finalAllocatedPorts = "";
                string containerOldPort = dockerContainer.OutPorts ?? "";

                if (serviceType == "web" || serviceType == "tool" || serviceType == "db")
                {
                    string rawInPorts = service.InPort ?? dockerContainer.InPorts ?? "";
                    inPorts = rawInPorts
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.TryParse(s.Trim(), out var p) ? p : (int?)null)
                        .Where(p => p.HasValue)
                        .Select(p => p!.Value)
                        .ToList();

                    finalAllocatedPorts = await GetFreePorts(inPorts.Count, session, connectionId);
                    if (finalAllocatedPorts != null)
                    {
                        extPorts = finalAllocatedPorts
                            .Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => int.TryParse(s.Trim(), out var p) ? p : (int?)null)
                            .Where(p => p.HasValue)
                            .Select(p => p!.Value)
                            .ToList();
                    }
                    if (extPorts.Count > 0 && extPorts.Count != inPorts.Count)
                    {
                        return $"❌ Lỗi cấu hình cổng! Số lượng cổng ngoài cấp phát ({extPorts.Count}) không khớp với số lượng cổng trong của mã nguồn ({inPorts.Count}).";
                    }
                }

                string fullDomain = "";
                if (domains != null)
                {
                    fullDomain = domains;
                }
                else
                {
                    fullDomain = containerMetadata?.Domain ?? $"{containerName}.{AppContext.ServerDomain}";
                }

                string owner = "";
                if (owners != null)
                {
                    owner = owners.Trim();
                }
                else
                {
                    owner = containerMetadata?.Owners != null ? string.Join(",", containerMetadata.Owners) : "company";
                }

                string labelArgs = ChatDeployService.BuildDockerLabels(serviceKey, dockerImage, serviceType, fullDomain, owner);

                string dbContainer = "";
                if (dockerImage.Contains("phpmyadmin"))
                {
                    if (containerMetadata?.Environments != null &&
                        containerMetadata.Environments.TryGetValue("PMA_HOSTS", out string? toolpmaHosts) &&
                        !string.IsNullOrEmpty(toolpmaHosts))
                    {
                        dbContainer = toolpmaHosts;
                    }
                }
                else if (dockerImage.Contains("pgadmin"))
                {
                    List<string> resultList = await PgAdminFileConfigurator.GetAttachedHosts(containerName);
                    if (resultList != null && resultList.Count != 0)
                        dbContainer = string.Join(",", resultList);
                }

                string rawEnvStr = service.Env ?? "";
                if (containerMetadata?.Environments != null && containerMetadata.Environments.Count > 0)
                {
                    rawEnvStr = string.Join(",", containerMetadata.Environments.Select(kv => $"{kv.Key}={kv.Value}"));
                }

                string backupcontainerName = $"{containerName}_backup";
                bool hasLocalBackup = false;

                try
                {
                    if (dockerContainer.IsRunning)
                    {
                        await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] [Local Backup] Đổi tên container sang '{backupcontainerName}' để dự phòng sự cố...");
                        string renameStatus = await DockerService.Update.DockerUpdateLifecycle.RenameContainerAsync(containerName, backupcontainerName);
                        if (renameStatus != "SUCCESS") throw new Exception($"Lỗi tạo bản sao tại Docker Engine: {renameStatus}");

                        hasLocalBackup = true;

                        await RedisContainerService.DeleteContainerAsync(containerName);
                        await RedisContainerService.UpdateContainerValueAsync(AppContext.ServerIP, backupcontainerName, containerOldPort);
                    }

                    await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] [Deploy] Khởi dựng Container mới '{containerName}' trên cổng mới [{finalAllocatedPorts}]...");

                    string result = serviceType switch
                    {
                        "web" => await ChatDeployService.DeployWebService(containerName, dockerImage, extPorts, inPorts, rawEnvStr, labelArgs, fullDomain, owner, session, connectionId),
                        "db" => await ChatDeployService.DeployDatabaseService(containerName, dockerImage, extPorts, inPorts, rawEnvStr, labelArgs, fullDomain, owner, session, connectionId),
                        "tool" => await ChatDeployService.DeployToolService(containerName, dockerImage, extPorts, inPorts, rawEnvStr, labelArgs, fullDomain, owner, session, connectionId, dbContainer),
                        _ => "❌ Loại dịch vụ của container không hợp lệ để tái thiết lập."
                    };

                    if (string.IsNullOrWhiteSpace(result) || result.Contains("error", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new Exception($"Docker Engine từ chối khởi chạy cấu hình mới. Chi tiết: {result}");
                    }

                    await RedisContainerService.UpdateContainerValueAsync(AppContext.ServerIP, containerName, finalAllocatedPorts);

                    if (hasLocalBackup)
                    {
                        await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] [Clean] Re-deploy thành công! Đang dọn dẹp bản sao dự phòng và cấu hình lại cổng...");

                        await DockerService.Delete.DockerDeleteContainer.RemoveContainerForceAsync(backupcontainerName);
                        await RedisContainerService.DeleteContainerAsync(backupcontainerName);

                        var oldPortsList = containerOldPort
                            .Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => int.TryParse(s.Trim(), out var p) ? p : (int?)null)
                            .Where(p => p.HasValue).Select(p => p!.Value).ToArray();

                        if (oldPortsList.Length > 0)
                        {
                            await RedisPortService.ReleasePortsAsync(AppContext.ServerIP, oldPortsList);
                        }

                        await RedisDomainService.DeleteDomainAsync(fullDomain);
                        foreach (var port in extPorts)
                        {
                            await RedisDomainService.InsertDomainAsync(fullDomain, AppContext.ServerIP + $":{port}");
                        }
                    }

                    return $"✅ UPDATE CONTAINER CONFIG SUCCESS\n\n" +
                        $"👤 Active Owner: {owner}\n" +
                        $"🌐 Active Domain: {fullDomain}\n" +
                        $"🔌 New Ports Assigned: {finalAllocatedPorts}\n" +
                        $"📊 Deploy Pipeline Output:\n{result}";
                }
                catch (Exception ex)
                {
                    await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"⚠️ [Node {AppContext.ServerID}] [Rollback] Phát hiện sự cố: {ex.Message}. Đang thực thi khôi phục hạ tầng nguyên bản...");

                    if (extPorts.Count > 0)
                    {
                        await RedisPortService.ReleasePortsAsync(AppContext.ServerIP, extPorts.ToArray());
                    }

                    if (await DockerService.Read.DockerReadContainer.IsContainerExistsAsync(containerName))
                    {
                        await DockerService.Delete.DockerDeleteContainer.RemoveContainerForceAsync(containerName);
                        await RedisContainerService.DeleteContainerAsync(containerName);
                    }

                    if (hasLocalBackup)
                    {
                        await DockerService.Update.DockerUpdateLifecycle.RenameContainerAsync(backupcontainerName, containerName);
                        await SystemCommandService.RunAsync($"docker start {containerName}");

                        await RedisContainerService.DeleteContainerAsync(backupcontainerName);
                        await RedisContainerService.UpdateContainerValueAsync(AppContext.ServerIP, containerName, containerOldPort);

                        return $"❌ Cập nhật cấu hình thất bại. Hệ thống đã khôi phục container về trạng thái cũ ổn định.\nChi tiết lỗi: {ex.Message}";
                    }

                    return $"❌ Tiến trình lỗi nặng và không thể phục hồi dữ liệu từ bản backup local.\nChi tiết lỗi: {ex.Message}";
                }
            }
            catch (Exception ex)
            {
                return $"❌ Re-deploy qua ChatDeployService cập nhật cấu hình thất bại: {ex.Message}";
            }
        }

        public static async Task<string?> GetFreePorts(int neededCount, UserSession session, string connectionId)
        {
            bool allPortsAvailable = false;
            var finalPortsList = new List<int>();
            string validatedPortsStr = "";
            var noderesult = await RedisNodeService.GetNodeAsync(AppContext.ServerIP);
            string nodeid = noderesult.nodes[$"{AppContext.ServerIP}"];

            int currentStartPort = 7000;
            int chunkSize = 60;

            while (currentStartPort <= 7999)
            {
                int currentEndPort = Math.Min(currentStartPort + chunkSize - 1, 7999);
                if (currentEndPort - currentStartPort + 1 < neededCount) break;

                var candidatePorts = Enumerable.Range(currentStartPort, currentEndPort - currentStartPort + 1).ToList();
                int consecutiveCount = 0;
                var tempRange = new List<int>();

                foreach (var portOpt in candidatePorts)
                {
                    bool isReserved = await RedisPortService.IsPortReservedAsync(AppContext.ServerIP, portOpt);
                    bool isFreePhysically = await SystemCommandService.IsHostPortAvailableAsync(portOpt);

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

            if (allPortsAvailable && finalPortsList.Any())
            {
                await RedisPortService.ReservePortsAsync(AppContext.ServerIP, finalPortsList.ToArray());
                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"⚙️ [Node {AppContext.ServerID}] Xác thực dải cổng [{validatedPortsStr}] thành công trên Node: {nodeid}");
                return validatedPortsStr;
            }
            else
            {
                await RedisChannelService.SendMessageToClientAsync(session.Debug, connectionId, $"⚠️ [Node {AppContext.ServerID}] Node {nodeid} không đủ tài nguyên hoặc trùng port.");
                return null;
            }
        }
    }
}
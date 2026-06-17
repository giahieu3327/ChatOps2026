using AppContext = ChatOps.Data.AppContext;
using ChatOps.Models;
using ChatOps.Services.RedisService;

namespace ChatOps.Services.ChatService
{
    public static class ChatDeployService
    {
        private static async Task SendLogWithDelayAsync(bool debug, string connectionId, string message)
        {
            await Task.Delay(100);
            await RedisChannelService.SendMessageToClientAsync(debug, connectionId, message);
        }

        public static async Task<string> DeployWebService(string containerName, string dockerImage, List<int> extPorts, List<int> defaultPort, string envStr, string labelArgs, string fullDomain, string owner, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] [WEB] Tiến hành khởi chạy Container Web...");

            string portMapping = extPorts.Any(p => p > 0)
                ? string.Join(" ", extPorts.Zip(defaultPort, (ext, def) => $"-p {ext}:{def}"))
                : "";

            string env = !string.IsNullOrWhiteSpace(envStr) ? $"{envStr}" : "";

            string resultRun = await DockerService.Create.DockerCreateContainer.DeploySingleContainerAsync(containerName, dockerImage, "ChatOps-net", portMapping, env, "", labelArgs);
            resultRun = resultRun.Trim();
            foreach (var port in extPorts)
            {
                await RedisPortService.ReleasePortsAsync(AppContext.ServerIP, extPorts.ToArray());
            }

            if (string.IsNullOrWhiteSpace(resultRun) || resultRun.Contains("error", StringComparison.OrdinalIgnoreCase) || resultRun.Length < 12)
                return "❌ [WEB] Docker Error:\n" + resultRun;

            string containerId = resultRun[..12];

            await SendLogWithDelayAsync(session.Debug, connectionId, $"⚙️ [Node {AppContext.ServerID}] [WEB] Khởi chạy thành công. Đồng bộ Redis Cluster và định tuyến Nginx Proxy...");
            string? containerexport = extPorts.Count > 0 ? string.Join(",", extPorts) : null;
            await RedisContainerService.UpdateContainerValueAsync(AppContext.ServerIP, containerName, containerexport);
            string url = "🔒 Internal only";

            var validExtPorts = extPorts.Where(p => p > 0).ToList();
            if (validExtPorts.Count > 0)
            {
                foreach (var port in validExtPorts)
                {
                    await RedisDomainService.InsertDomainAsync(fullDomain, AppContext.ServerIP + $":{port}");
                }
                string fallbackUrls = string.Join("", validExtPorts.Select(p => $" | http://{AppContext.ServerIP}:{p}"));
                url = $"🔗 URL công khai: http://{fullDomain}:880" + fallbackUrls;
            }

            var net = await DockerService.Read.DockerReadNetwork.GetContainerNetworkDetailsAsync(containerName);
            string? containernet = net?.Networks.FirstOrDefault();

            return $"✅ [WEB] Deploy thành công\n" +
                   $"📦 Node thực thi: {AppContext.ServerID}\n" +
                   $"📦 Image: {dockerImage}\n" +
                   $"📦 Container: {containerName}\n" +
                   $"🆔 ID: {containerId}\n" +
                   $"🌐 Network: {containernet}\n" +
                   $"👤 Owner: {owner}\n" +
                   $"{url}";
        }

        public static async Task<string> DeployDatabaseService(string containerName, string dockerImage, List<int> extPorts, List<int> defaultPort, string envStr, string labelArgs, string fullDomain, string owner, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] [DB] Tiến hành khởi chạy Container Database...");

            string portMapping = extPorts.Any(p => p > 0)
                ? string.Join(" ", extPorts.Zip(defaultPort, (ext, def) => $"-p {ext}:{def}"))
                : "";

            string env = !string.IsNullOrWhiteSpace(envStr) ? $"{envStr}" : "";

            string resultRun = await DockerService.Create.DockerCreateContainer.DeploySingleContainerAsync(containerName, dockerImage, "ChatOps-net", portMapping, env, "", labelArgs);
            resultRun = resultRun.Trim();

            foreach (var port in extPorts)
            {
                await RedisPortService.ReleasePortsAsync(AppContext.ServerIP, extPorts.ToArray());
            }

            if (string.IsNullOrWhiteSpace(resultRun) || resultRun.Contains("error", StringComparison.OrdinalIgnoreCase) || resultRun.Length < 12)
                return "❌ [DB] Docker Error:\n" + resultRun;

            string containerId = resultRun[..12];

            await SendLogWithDelayAsync(session.Debug, connectionId, $"💾 [Node {AppContext.ServerID}] [DB] Khởi chạy thành công. Tiến hành đồng bộ hạ tầng lên hệ thống...");
            string? containerexport = extPorts.Count > 0 ? string.Join(",", extPorts) : null;
            await RedisContainerService.UpdateContainerValueAsync(AppContext.ServerIP, containerName, containerexport);

            string url = "🔒 Internal only (Database Cluster)";

            var validExtPorts = extPorts.Where(p => p > 0).ToList();
            if (validExtPorts.Count > 0)
            {
                foreach (var port in validExtPorts)
                {
                    await RedisDomainService.InsertDomainAsync(fullDomain, AppContext.ServerIP + $":{port}");
                }
                string fallbackUrls = string.Join("", validExtPorts.Select(p => $" | http://{AppContext.ServerIP}:{p}"));
                url = $"🔗 URL công khai: http://{fullDomain}:880" + fallbackUrls;
            }

            var net = await DockerService.Read.DockerReadNetwork.GetContainerNetworkDetailsAsync(containerName);
            string? containernet = net?.Networks.FirstOrDefault();

            return $"✅ [DB] Deploy thành công\n" +
                   $"📦 Node thực thi: {AppContext.ServerID}\n" +
                   $"📦 Image: {dockerImage}\n" +
                   $"📦 Container: {containerName}\n" +
                   $"🆔 ID: {containerId}\n" +
                   $"🌐 Network: {containernet}\n" +
                   $"👤 Owner: {owner}\n" +
                   $"{url}";
        }

        public static async Task<string> DeployToolService(string containerName, string dockerImage, List<int> extPorts, List<int> defaultPort, string envStr, string labelArgs, string fullDomain, string owner, UserSession session, string connectionId, string dbcontainer)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] [TOOL] Tiến hành xác thực cấu trúc và liên kết cụm Admin Tool chuyên dụng...");

            string imageName = dockerImage.Trim().ToLower();
            var resolvedDbHosts = new List<string>();
            var dbTypes = new List<string>();
            string dbNetwork = "ChatOps-net";

            string portMapping = extPorts.Any(p => p > 0)
                ? string.Join(" ", extPorts.Zip(defaultPort, (ext, def) => $"-p {ext}:{def}"))
                : "";

            if (!string.IsNullOrWhiteSpace(dbcontainer))
            {
                await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang kiểm tra thông tin định danh và phân loại outport của các DB Container mục tiêu...");

                var rawDbList = dbcontainer.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()).Distinct().ToList();

                try
                {
                    var inspectTasks = rawDbList.Select(async dbName =>
                    {
                        string currentdbNodeIp = "";
                        var dbnode = await RedisContainerService.GetContainerAsync(dbName);
                        if (dbnode.success && dbnode.nodes != null && dbnode.nodes.Count > 0)
                        {
                            foreach (var node in dbnode.nodes)
                            {
                                string[] rawvalue = node.Value.Split("|");
                                if (rawvalue[0] == dbName)
                                {
                                    currentdbNodeIp = node.Key;
                                    break;
                                }
                            }
                        }
                        if (currentdbNodeIp == AppContext.ServerIP)
                        {
                            var db = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(dbName);
                            var metadata = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(dbName);
                            var networks = await DockerService.Read.DockerReadNetwork.GetContainerNetworkDetailsAsync(dbName);
                            return (DbName: db?.Name, Meta: metadata, Net: networks, Original: dbName);
                        }
                        else
                        {
                            var (db, metadata, networks) = await RedisService.RedisService.SendGetDetailContainerRequestAsync(currentdbNodeIp, dbName);
                            return (DbName: db?.Name, Meta: metadata, Net: networks, Original: dbName);
                        }
                    });

                    var inspectResults = await Task.WhenAll(inspectTasks);

                    var resolveTasks = rawDbList.Select(async dbName => await ClusterRoutingService.ResolveHostFromContainerNameAsync(dbName));
                    string[] resolvedHosts = await Task.WhenAll(resolveTasks);

                    for (int i = 0; i < inspectResults.Length; i++)
                    {
                        var res = inspectResults[i];
                        string hostResolved = resolvedHosts[i];

                        if (res.DbName == null || res.Meta == null || res.Net == null)
                        {
                            return $"❌ Lấy dữ liệu hạ tầng của container '{res.Original}' bị lỗi hoặc không tồn tại.";
                        }

                        if (hostResolved.StartsWith("Không tìm thấy", StringComparison.OrdinalIgnoreCase))
                        {
                            return $"❌ Lỗi định tuyến Cluster: {hostResolved}";
                        }

                        resolvedDbHosts.Add(hostResolved);

                        if (!string.IsNullOrWhiteSpace(res.Meta.DbType))
                        {
                            dbTypes.Add(res.Meta.DbType.ToLower());
                        }

                        if (dbNetwork == "ChatOps-net" && res.Net.Networks != null && res.Net.Networks.Any())
                        {
                            dbNetwork = res.Net.Networks.FirstOrDefault() ?? "ChatOps-net";
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Detailed Error Inspecting/Resolving Multi-DB: {ex}");
                    return $"❌ Inspect/Resolve DB containers failed:\n{ex.Message}";
                }
            }

            string networkError = await DockerService.Create.DockerCreateNetwork.InitializeDockerNetworkAsync(dbNetwork);
            if (!string.IsNullOrEmpty(networkError)) return networkError;

            string resultRun;
            if (imageName.StartsWith("phpmyadmin"))
            {
                resultRun = await RunPhpMyAdmin(imageName, resolvedDbHosts, dbTypes, dbNetwork, portMapping, containerName, labelArgs, session, connectionId);
            }
            else if (imageName.Contains("pgadmin"))
            {
                resultRun = await RunPgAdmin(imageName, resolvedDbHosts, dbTypes, dbNetwork, portMapping, containerName, labelArgs, session, connectionId, envStr);
            }
            else
            {
                return "❌ [TOOL] Dịch vụ tool quản trị cấu hình không được hỗ trợ.";
            }

            resultRun = resultRun.Trim();

            if (extPorts.Any(p => p > 0))
            {
                await RedisPortService.ReleasePortsAsync(AppContext.ServerIP, extPorts.Where(p => p > 0).ToArray());
            }

            if (string.IsNullOrWhiteSpace(resultRun) || resultRun.Contains("error", StringComparison.OrdinalIgnoreCase) || resultRun.Length < 12)
                return "❌ [TOOL] Docker Error:\n" + resultRun;

            string containerId = resultRun[..12];

            await SendLogWithDelayAsync(session.Debug, connectionId, $"⚙️ [Node {AppContext.ServerID}] [TOOL] Khởi chạy thành công. Đồng bộ trạng thái cụm và cấu hình Proxy định tuyến...");

            string? containerexport = extPorts.Count > 0 ? string.Join(",", extPorts) : null;
            await RedisContainerService.UpdateContainerValueAsync(AppContext.ServerIP, containerName, containerexport);

            string url = "🔒 Internal only";
            var validExtPorts = extPorts.Where(p => p > 0).ToList();
            if (validExtPorts.Count > 0)
            {
                foreach (var port in validExtPorts)
                {
                    await RedisDomainService.InsertDomainAsync(fullDomain, AppContext.ServerIP + $":{port}");
                }
                string fallbackUrls = string.Join("", validExtPorts.Select(p => $" | http://{AppContext.ServerIP}:{p}"));
                url = $"🔗 URL công khai: http://{fullDomain}:880" + fallbackUrls;
            }

            var net = await DockerService.Read.DockerReadNetwork.GetContainerNetworkDetailsAsync(containerName);
            string? containernet = net?.Networks.FirstOrDefault();
            string dbLinkStatus = resolvedDbHosts.Count == 0 ? "Không liên kết" : $"Đã liên kết tới [{string.Join(", ", resolvedDbHosts)}]";

            return $"✅ [TOOL] Deploy thành công\n" +
                $"📦 Node thực thi: {AppContext.ServerID}\n" +
                $"📦 Image: {imageName}\n" +
                $"📦 Container: {containerName}\n" +
                $"🆔 ID: {containerId}\n" +
                $"🌐 Network: {containernet}\n" +
                $"🔗 Database Hosts: {dbLinkStatus}\n" +
                $"👤 Owner: {owner}\n" +
                $"{url}";
        }

        public static async Task<string> RunPhpMyAdmin(string dockerImage, List<string> dbHosts, List<string> dbTypes, string dbNetwork, string portMapping, string containerName, string labelArgs, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Xác thực tính tương thích của phpMyAdmin với danh sách Database...");

            if (dbTypes.Any(t => t != "mysql"))
            {
                return $"❌ phpMyAdmin chỉ hỗ trợ duy nhất hệ cơ sở dữ liệu MySQL.\n🗄 Danh sách loại DB phát hiện: {string.Join(", ", dbTypes.Distinct())}";
            }

            string pmaHostsArg = dbHosts.Count > 0 ? $"-e PMA_HOSTS={string.Join(",", dbHosts)}" : "";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"⚙️ [Node {AppContext.ServerID}] Cấu hình cụm tham số môi trường hoàn tất. Tiến hành khởi dựng phpMyAdmin Container...");

            string result = await DockerService.Create.DockerCreateContainer.DeploySingleContainerAsync(
                containerName: containerName,
                dockerImage: dockerImage,
                networkName: dbNetwork,
                portMapping: portMapping,
                envArgs: pmaHostsArg,
                labelArgs: labelArgs
            );

            if (string.IsNullOrWhiteSpace(result) || result.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            if (dbHosts.Count > 0)
            {
                await SendLogWithDelayAsync(session.Debug, connectionId, $"🌐 [Node {AppContext.ServerID}] Đang đồng bộ hóa liên kết và kết nối bổ sung Tool vào toàn bộ mạng phụ của các Database...");

                var networkNamesToConnect = new HashSet<string>();
                foreach (var dbHost in dbHosts)
                {
                    if (!dbHost.Contains(":"))
                    {
                        var allNetworks = await DockerService.Read.DockerReadNetwork.GetContainerNetworkDetailsAsync(dbHost);
                        if (allNetworks?.Networks != null)
                        {
                            foreach (string net in allNetworks.Networks)
                            {
                                if (net != dbNetwork) networkNamesToConnect.Add(net);
                            }
                        }
                    }
                }

                foreach (var net in networkNamesToConnect)
                {
                    await DockerService.Update.DockerUpdateNetwork.ConnectNetworkAsync(net, containerName);
                }
            }

            return result;
        }

        public static async Task<string> RunPgAdmin(string dockerImage, List<string> dbHosts, List<string> dbTypes, string dbNetwork, string portMapping, string containerName, string labelArgs, UserSession session, string connectionId, string envStr)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Xác thực tính tương thích của pgAdmin với danh sách Database...");

            if (dbTypes.Any(t => t != "postgres"))
            {
                return $"❌ pgAdmin chỉ hỗ trợ duy nhất hệ cơ sở dữ liệu PostgreSQL.\n🗄 Danh sách loại DB phát hiện: {string.Join(", ", dbTypes.Distinct())}";
            }

            string? volumeArg = null;
            if (dbHosts.Count > 0)
            {
                await SendLogWithDelayAsync(session.Debug, connectionId, $"💾 [Node {AppContext.ServerID}] Đang khởi tạo thư mục cấu hình tổng hợp và kết xuất tệp servers.json cho pgAdmin...");

                string jsonFile = await FileService.PgAdminFileConfigurator.GenerateBulkServersConfiguration(containerName, dbHosts);

                if (!string.IsNullOrEmpty(jsonFile) && !jsonFile.StartsWith("❌"))
                {
                    volumeArg = $"-v {jsonFile}:/pgadmin4/servers.json";
                }
                else if (jsonFile.StartsWith("❌"))
                {
                    return jsonFile;
                }
            }

            string envArgs = $"{envStr}";
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⚙️ [Node {AppContext.ServerID}] Đã ghi nhận tệp cấu hình Server tổng hợp. Tiến hành khởi dựng pgAdmin Container...");

            string result = await DockerService.Create.DockerCreateContainer.DeploySingleContainerAsync(
                containerName: containerName,
                dockerImage: dockerImage,
                networkName: dbNetwork,
                portMapping: portMapping,
                envArgs: envArgs,
                volumeArgs: volumeArg,
                labelArgs: labelArgs
            );

            if (string.IsNullOrWhiteSpace(result) || result.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            if (dbHosts.Count > 0)
            {
                await SendLogWithDelayAsync(session.Debug, connectionId, $"🌐 [Node {AppContext.ServerID}] Đang đồng bộ hóa liên kết và kết nối bổ sung Tool vào toàn bộ mạng phụ của các Database...");

                var networkNamesToConnect = new HashSet<string>();
                foreach (var dbHost in dbHosts)
                {
                    if (!dbHost.Contains(":"))
                    {
                        var allNetworks = await DockerService.Read.DockerReadNetwork.GetContainerNetworkDetailsAsync(dbHost);
                        if (allNetworks?.Networks != null)
                        {
                            foreach (string net in allNetworks.Networks)
                            {
                                if (net != dbNetwork) networkNamesToConnect.Add(net);
                            }
                        }
                    }
                }

                foreach (var net in networkNamesToConnect)
                {
                    await DockerService.Update.DockerUpdateNetwork.ConnectNetworkAsync(net, containerName);
                }
            }

            return result;
        }

        public static string BuildDockerLabels(string serviceKey, string dockerImage, string serviceType, string containerDomain, string owner)
        {
            var labels = new List<string>();

            if (!string.IsNullOrEmpty(owner))
            {
                labels.Add($"--label owner=\"{owner}\"");
            }

            if (!string.IsNullOrEmpty(containerDomain))
            {
                labels.Add($"--label domain=\"{containerDomain}\"");
            }

            if (serviceType == "db" && !string.IsNullOrEmpty(dockerImage))
            {
                string type = dockerImage.Contains("mysql", StringComparison.OrdinalIgnoreCase) ? "mysql" :
                             dockerImage.Contains("postgres", StringComparison.OrdinalIgnoreCase) ? "postgres" : "unknown";

                labels.Add($"--label dbtype=\"{type}\"");
            }

            if (!string.IsNullOrEmpty(serviceKey))
            {
                labels.Add($"--label service=\"{serviceKey}\"");
            }

            return string.Join(" ", labels);
        }

        public static string BuildOrUpdatePgAdminJson(string jsonFile, string dbName, object newServerNode, out bool alreadyAttached)
        {
            alreadyAttached = false;
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };

            if (System.IO.File.Exists(jsonFile))
            {
                try
                {
                    string existingContent = System.IO.File.ReadAllText(jsonFile);
                    var jsonDoc = System.Text.Json.Nodes.JsonNode.Parse(existingContent);
                    var serversObj = jsonDoc?["Servers"]?.AsObject();

                    if (serversObj != null)
                    {
                        int maxIndex = 0;
                        foreach (var item in serversObj)
                        {
                            if (int.TryParse(item.Key, out int idx) && idx > maxIndex)
                                maxIndex = idx;

                            string host = item.Value?["Host"]?.ToString() ?? "";
                            if (host == dbName)
                            {
                                alreadyAttached = true;
                                return string.Empty;
                            }
                        }

                        serversObj.Add((maxIndex + 1).ToString(), System.Text.Json.JsonSerializer.SerializeToNode(newServerNode));
                        if (jsonDoc == null) return "";
                        return jsonDoc.ToJsonString(options);
                    }
                }
                catch
                {
                    return BuildPgAdminServersJson(newServerNode);
                }
            }

            return BuildPgAdminServersJson(newServerNode);
        }

        public static string BuildPgAdminServersJson(object serverNode)
        {
            return System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    Servers = new Dictionary<string, object>
                    {
                        { "1", serverNode }
                    }
                },
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                }
            );
        }
    }
}
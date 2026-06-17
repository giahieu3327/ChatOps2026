using ChatOps.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AppContext = ChatOps.Data.AppContext;
using ChatOps.Models;
using Microsoft.EntityFrameworkCore;
using ChatOps.Services.RedisService;
using ChatOps.Services.SystemService;
using ChatOps.Services.FileService;
using ChatOps.Services.StartupService;

namespace ChatOps.Services.ChatService
{
    public static class ChatDeployServiceDeploy
    {
        private static async Task SendLogWithDelayAsync(bool debug, string connectionId, string message)
        {
            await Task.Delay(100);
            await RedisChannelService.SendMessageToClientAsync(debug, connectionId, message);
        }

        public static async Task<string> Deploy(Dictionary<string, string> parsed, UserSession session, AppDbContext _db, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang trích xuất và xác thực tham số cấu hình...");

            string serviceKey = parsed.GetValueOrDefault("image", "").Trim().ToLower();
            string containerName = parsed.GetValueOrDefault("name", "").Trim();
            string dbContainer = parsed.GetValueOrDefault("db", "").Trim().ToLower();
            string extraUser = parsed.GetValueOrDefault("username", "").Trim();
            string containerDomain = parsed.GetValueOrDefault("domain", "").Trim();

            if (string.IsNullOrWhiteSpace(serviceKey))
                return "❌ Thiếu tham số! Vui lòng nhập ít nhất tham số 'image'.";

            if (!ImageCategories.ImageServices.TryGetValue(serviceKey, out var service))
                return "❌ Service không hỗ trợ trên hệ thống.";

            string dockerImage = service.Image;
            string serviceType = service.Type;

            if (string.IsNullOrWhiteSpace(containerName))
            {
                var rand = new Random();
                containerName = $"container_{rand.Next(100000, 999999)}";
            }
            else
            {
                if (!Regex.IsMatch(containerName, "^[a-zA-Z0-9_]+$"))
                    return "❌ Container name chỉ được chứa chữ cái (hoa/thường), số và dấu gạch dưới (_).";
            }

            if (!string.IsNullOrWhiteSpace(extraUser))
            {
                if (!Regex.IsMatch(extraUser, "^[a-zA-Z0-9]+$"))
                    return "❌ Username tài khoản phối hợp chỉ được chứa chữ cái (hoa/thường) và số.";
            }

            if (!string.IsNullOrWhiteSpace(dbContainer) && serviceType != "tool")
                return "❌ Chỉ các service thuộc loại 'tool' mới dùng được tham số 'db' để liên kết.";

            if (!string.IsNullOrWhiteSpace(extraUser))
            {
                bool isExtraUserValid = await _db.Users.AnyAsync(x => x.Username.ToLower() == extraUser.ToLower());
                if (!isExtraUserValid)
                    return $"❌ Tài khoản phụ phối hợp (username) '{extraUser}' không tồn tại trên hệ thống.";
            }

            List<int> extPorts = new List<int>();
            List<int> inPorts = new List<int>();

            if (serviceType == "web" || serviceType == "tool" || serviceType == "db")
            {
                string finalAllocatedPorts = parsed.GetValueOrDefault("port", "");
                extPorts = finalAllocatedPorts
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), out var p) ? p : (int?)null)
                    .Where(p => p.HasValue)
                    .Select(p => p!.Value)
                    .ToList();

                string rawInPorts = service.InPort ?? "";
                inPorts = rawInPorts
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), out var p) ? p : (int?)null)
                    .Where(p => p.HasValue)
                    .Select(p => p!.Value)
                    .ToList();

                if (extPorts.Count > 0 && extPorts.Count != inPorts.Count)
                {
                    return $"❌ Lỗi cấu hình cổng! Số lượng cổng ngoài cấp phát ({extPorts.Count}) không khớp với số lượng cổng trong của mã nguồn ({inPorts.Count}).";
                }
            }

            string fullDomain = "";
            if (extPorts.Any(p => p > 0))
            {
                if (string.IsNullOrWhiteSpace(containerDomain))
                {
                    fullDomain = $"{containerName}.{AppContext.ServerDomain}";
                }
                else
                {
                    if (!Regex.IsMatch(containerDomain, "^[a-zA-Z0-9-]+$"))
                        return "❌ Domain cấu hình chỉ được chứa chữ cái (hoa/thường), số và dấu gạch ngang (-).";
                    fullDomain = $"{containerDomain}.{AppContext.ServerDomain}";
                }
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang kiểm tra trùng lặp Container & Domain trên Cluster...");

            string duplicateCheckError = await SystemCommandService.CheckDuplicatesAsync(containerName, fullDomain);
            if (!string.IsNullOrEmpty(duplicateCheckError))
                return duplicateCheckError;

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] Đang khởi tạo môi trường mạng và chuẩn bị Docker Image...");

            string netError = await DockerService.Create.DockerCreateNetwork.InitializeDockerNetworkAsync();
            if (!string.IsNullOrEmpty(netError)) return netError;

            string? pullError = await DockerService.Create.DockerCreateImage.PullDockerImageAsync(dockerImage);
            if (!string.IsNullOrEmpty(pullError)) return pullError;

            string owner = "company";
            if (session.Role == "dev" || session.Role == "ops")
                owner = session.Role;
            if (!string.IsNullOrWhiteSpace(extraUser))
                owner += $",{extraUser}";

            string labelArgs = ChatDeployService.BuildDockerLabels(serviceKey, dockerImage, serviceType, fullDomain, owner);

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Chuyển giao xử lý khởi dựng cho dịch vụ [{serviceType.ToUpper()}]...");

            return serviceType switch
            {
                "web" => await ChatDeployService.DeployWebService(containerName, dockerImage, extPorts, inPorts, service.Env, labelArgs, fullDomain, owner, session, connectionId),
                "db" => await ChatDeployService.DeployDatabaseService(containerName, dockerImage, extPorts, inPorts, service.Env, labelArgs, fullDomain, owner, session, connectionId),
                "tool" => await ChatDeployService.DeployToolService(containerName, dockerImage, extPorts, inPorts, service.Env, labelArgs, fullDomain, owner, session, connectionId, dbContainer),
                _ => "❌ Loại dịch vụ không hợp lệ."
            };
        }

        public static async Task<string> DeployGit(Dictionary<string, string> parsed, UserSession session, AppDbContext _db, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang trích xuất và kiểm tra tham số Git Repository...");

            string repoUrl = parsed.GetValueOrDefault("url", "").Trim();
            string portStr = parsed.GetValueOrDefault("port", "");
            string extraUser = parsed.GetValueOrDefault("username", "").Trim();
            string containerDomain = parsed.GetValueOrDefault("domain", "").Trim();

            if (string.IsNullOrWhiteSpace(repoUrl))
                return "❌ Thiếu tham số! Vui lòng nhập tham số 'url' của kho mã nguồn Git.";

            var PortsList = portStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(p => int.TryParse(p.Trim(), out int portVal) ? portVal : (int?)null)
                                .Where(p => p.HasValue)
                                .Select(p => p!.Value)
                                .ToList();

            // ĐỘNG HÓA ĐƯỜNG DẪN THƯ MỤC GIT SOURCE
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string basePath = Path.Combine(userHome, "ChatOps", "services", "Trial");
            string repoName = repoUrl.Split('/').Last().Replace(".git", "").ToLower();
            string projectPath = Path.Combine(basePath, repoName);
            string app = repoName + "_trial";

            if (!Regex.IsMatch(app, "^[a-zA-Z0-9_]+$"))
                return "❌ Tên ứng dụng (từ tên Repository) không hợp lệ. Chỉ được chứa chữ cái (hoa/thường), số và dấu gạch dưới (_).";

            if (!string.IsNullOrWhiteSpace(extraUser))
            {
                if (!Regex.IsMatch(extraUser, "^[a-zA-Z0-9]+$"))
                    return "❌ Username tài khoản phối hợp chỉ được chứa chữ cái (hoa/thường) và số.";
            }

            string fullDomain = "";
            if (string.IsNullOrWhiteSpace(containerDomain))
            {
                fullDomain = $"{app}.{AppContext.ServerDomain}";
            }
            else
            {
                if (!Regex.IsMatch(containerDomain, "^[a-zA-Z0-9-]+$"))
                    return "❌ Domain cấu hình chỉ được chứa chữ cái (hoa/thường), số và dấu gạch ngang (-).";
                fullDomain = $"{containerDomain}.{AppContext.ServerDomain}";
            }

            if (!string.IsNullOrWhiteSpace(extraUser))
            {
                bool isExtraUserValid = await _db.Users.AnyAsync(x => x.Username.ToLower() == extraUser.ToLower());
                if (!isExtraUserValid)
                    return $"❌ Tài khoản phụ (username) '{extraUser}' không tồn tại trên hệ thống.";
            }

            string owner = "company";
            if (session.Role == "dev" || session.Role == "ops")
                owner = session.Role;
            if (!string.IsNullOrWhiteSpace(extraUser))
                owner += $",{extraUser}";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang kiểm tra định danh ứng dụng và cấu hình Domain trên Cluster...");

            var Instances = await RedisInstanceService.GetInstanceAsync(app);
            foreach (var Instance in Instances.nodes)
            {
                string nodeip = Instance.Key;
                string instance = Instance.Value;
                if (instance == app)
                {
                    return $"❌ Ứng dụng '{app}' đã tồn tại trên hệ thống cluster!";
                }
            }

            string duplicateCheckError = await SystemCommandService.CheckDuplicatesAsync(null, fullDomain);
            if (!string.IsNullOrEmpty(duplicateCheckError))
                return duplicateCheckError;

            await SendLogWithDelayAsync(session.Debug, connectionId, $"📥 [Node {AppContext.ServerID}] Kiểm tra hạ tầng hoàn tất. Tiến hành đồng bộ mã nguồn từ Git Repository...");

            if (Directory.Exists(projectPath))
            {
                await GitService.GitService.CleanDirectory(projectPath);
            }

            string cloneResult = await GitService.GitService.CloneRepository(repoUrl, projectPath);
            if (cloneResult.Contains("fatal", StringComparison.OrdinalIgnoreCase))
            {
                return $"❌ Clone mã nguồn thất bại:\n{cloneResult}";
            }

            string filepath = Path.Combine(projectPath, "docker-git.yml");
            string[] components = await DockerComposeAnalyzer.GetBuildServicesAsync(filepath);
            if (components.Length == 0)
                return "❌ Tệp cấu hình không chứa bất kỳ dịch vụ nào có khai báo thuộc tính 'build' để phát hành.";

            string servicetype = string.Join(",", components);
            AppCategories.AppServices[repoName] = (repoUrl, servicetype, false);
            await RedisAppService.UpdateAppValueAsync(repoName, repoUrl, servicetype, false);
            await SystemStartupService.SeedDefaultAppServicesAsync();

            await SendLogWithDelayAsync(session.Debug, connectionId, $"⚙️ [Node {AppContext.ServerID}] Khởi chạy bộ dựng hạ tầng Docker Compose Pipeline...");

            string clusterDeployResult = await DockerService.Create.DockerCreateContainer.DeployFromGitSource(
                projectPath: projectPath,
                appName: app,
                owner: owner,
                extPort: PortsList,
                domain: fullDomain,
                IMGDB: $"{app}-db:trial",
                IMGBACKEND: $"{app}-backend:trial",
                IMGWEB: $"{app}-web:trial",
                IMGLB: $"{app}-lb:trial",
                isDebug: session.Debug,
                connectionId: connectionId
            );

            await SendLogWithDelayAsync(session.Debug, connectionId, $"📝 [Node {AppContext.ServerID}] Đang đăng ký định tuyến Nginx Gateway và đồng bộ dữ liệu Cluster (Redis)...");

            foreach (var port in PortsList)
            {
                await RedisPortService.ReleasePortsAsync(AppContext.ServerIP, PortsList.ToArray());
            }

            await RedisInstanceService.InsertInstanceAsync(AppContext.ServerIP);
            await RedisInstanceService.UpdateInstanceValueAsync(AppContext.ServerIP, app);

            var result = await DockerService.Read.DockerReadContainer.GetContainersByFilterAsync(app);
            foreach (var container in result)
            {
                string containerappname = container.Name;
                string containerappexport = container.OutPorts;
                await RedisContainerService.InsertContainerAsync(AppContext.ServerIP);
                await RedisContainerService.UpdateContainerValueAsync(AppContext.ServerIP, containerappname, containerappexport);
            }

            string nginxStatus = "";
            if (PortsList.Count > 0)
            {
                List<string> portsListStr = PortsList
                    .Select(p => $"{AppContext.ServerIP}:{p}")
                    .ToList();

                foreach (var port in portsListStr)
                {
                    (bool success, string message) = await RedisDomainService.InsertDomainAsync(fullDomain, port);
                    nginxStatus += message + "\n";
                }
            }

            var containers = await DockerService.Read.DockerReadContainer.GetContainersByFilterAsync(app);
            string containername = "";
            foreach (var container in containers)
            {
                containername += container.Name + "\n";
            }

            string displayRuntimePath = Path.Combine(userHome, "ChatOps", "docker", "runtime", app).Replace('\\', '/');

            return $"✅ DEPLOY GIT CLUSTER SUCCESS\n\n" +
                $"📦 Repo: {repoUrl}\n" +
                $"📦 App: {app}\n" +
                $"👤 Owner: {owner}\n" +
                $"🌐 Domain: {fullDomain}\n" +
                $"🔌 Public Port (LB): {string.Join(",", PortsList)}\n" +
                $"📂 Runtime Active Path: `{displayRuntimePath}`\n\n" +
                $"📋 Trạng thái thực thi Container:\n{containername}\n" +
                $"🌐 Mạng cách ly nội bộ: {app}_app-net\n\n" +
                $"⚙️ Chi tiết logs deploy:\n{clusterDeployResult}\n\n" +
                $"🌐 Redis Sync Gateway Status: {nginxStatus}";
        }

        public static async Task<string> DeployCompose(Dictionary<string, string> parsed, UserSession session, AppDbContext _db, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang trích xuất và xác thực cấu hình Cloud Registry...");

            string service = parsed.GetValueOrDefault("app", "").ToLower().Trim();
            string tagParam = parsed.GetValueOrDefault("version", "").Trim();
            string portStr = parsed.GetValueOrDefault("port", "").Trim();
            string extraUser = parsed.GetValueOrDefault("username", "").Trim();
            string containerDomain = parsed.GetValueOrDefault("domain", "").Trim();

            if (string.IsNullOrWhiteSpace(service))
                return "❌ Thiếu tham số! Vui lòng nhập tên ứng dụng tại tham số 'app'.";

            if (!Regex.IsMatch(service, "^[a-zA-Z0-9_]+$"))
                return "❌ Tên ứng dụng (app) không hợp lệ. Chỉ được chứa chữ cái (hoa/thường), số và dấu gạch dưới (_).";

            (bool success, var targetServices, string errorMessage) = ChatDeployServiceDeployRelease.GetTargetServices(service);
            if (!success) return errorMessage;

            var PortsList = portStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(p => int.TryParse(p.Trim(), out int portVal) ? portVal : (int?)null)
                                .Where(p => p.HasValue)
                                .Select(p => p!.Value)
                                .ToList();

            // ĐỘNG HÓA ĐƯỜNG DẪN THƯ MỤC PRODUCTION COMPOSE
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string basePath = Path.Combine(userHome, "ChatOps", "services", "Final");
            string projectPath = Path.Combine(basePath, service);
            string app = service;

            if (!string.IsNullOrWhiteSpace(extraUser))
            {
                if (!Regex.IsMatch(extraUser, "^[a-zA-Z0-9]+$"))
                    return "❌ Username tài khoản phối hợp chỉ được chứa chữ cái (hoa/thường) và số.";
            }

            string fullDomain = "";
            if (string.IsNullOrWhiteSpace(containerDomain))
            {
                fullDomain = $"{app}.{AppContext.ServerDomain}";
            }
            else
            {
                if (!Regex.IsMatch(containerDomain, "^[a-zA-Z0-9-]+$"))
                    return "❌ Domain cấu hình chỉ được chứa chữ cái (hoa/thường), số và dấu gạch ngang (-).";
                fullDomain = $"{containerDomain}.{AppContext.ServerDomain}";
            }

            if (!string.IsNullOrWhiteSpace(extraUser))
            {
                bool isExtraUserValid = await _db.Users.AnyAsync(x => x.Username.ToLower() == extraUser.ToLower());
                if (!isExtraUserValid)
                    return $"❌ Tài khoản phụ (username) '{extraUser}' không tồn tại trên hệ thống.";
            }

            string owner = "company";
            if (session.Role == "dev" || session.Role == "ops")
                owner = session.Role;
            if (!string.IsNullOrWhiteSpace(extraUser))
                owner += $",{extraUser}";

            var Instances = await RedisInstanceService.GetInstanceAsync(app);
            foreach (var Instance in Instances.nodes)
            {
                string nodeip = Instance.Key;
                string instance = Instance.Value;
                if (instance == app)
                {
                    return $"❌ Ứng dụng '{app}' đã tồn tại trên hệ thống cluster!";
                }
            }

            string duplicateCheckError = await SystemCommandService.CheckDuplicatesAsync(null, fullDomain);
            if (!string.IsNullOrEmpty(duplicateCheckError))
                return duplicateCheckError;

            if (!Directory.Exists(projectPath))
                return $"❌ Không tìm thấy service '{service}' trong thư mục Final (Vui lòng chạy release trước)";

            string composeFile = Path.Combine(projectPath, "docker-registry.yml");
            if (!File.Exists(composeFile))
                return $"❌ Thiếu file cấu hình production 'docker-registry.yml'";

            string newestTag = tagParam;
            if (string.IsNullOrWhiteSpace(newestTag))
            {
                try
                {
                    using (var client = new HttpClient())
                    {
                        var listRes = await client.GetAsync($"https://hub.docker.com/v2/repositories/{AppContext.dockerUser}/{service}-{targetServices[0]}/tags/?page_size=3");
                        string listJson = await listRes.Content.ReadAsStringAsync();
                        var matches = Regex.Matches(listJson, "\"name\":\"(.*?)\"");

                        newestTag = matches.Cast<Match>()
                            .Select(m => m.Groups[1].Value)
                            .FirstOrDefault(t => t != "latest" && t != "trial") ?? "latest";
                    }
                }
                catch
                {
                    newestTag = "latest";
                }
            }
            else
            {
                foreach (var comp in targetServices)
                {
                    bool isTagValid = await DockerHubService.DockerHubService.IsTagExistAsync(service, comp, newestTag);
                    if (!isTagValid)
                    {
                        return $"❌ Phiên bản `{newestTag}` của thành phần `{comp}` không tồn tại trên Cloud Registry Docker Hub.";
                    }
                }
            }

            string imgDb = $"{AppContext.dockerUser}/{service}-db:{newestTag}";
            string imgBackend = $"{AppContext.dockerUser}/{service}-backend:{newestTag}";
            string imgWeb = $"{AppContext.dockerUser}/{service}-web:{newestTag}";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] Khởi chạy Production Pipeline độc lập (Tag Version: `{newestTag}`)....");

            string clusterDeployResult = await DockerService.Create.DockerCreateContainer.DeployFromRegistry(
                projectPath: projectPath,
                appName: app,
                owner: owner,
                extPort: PortsList,
                domain: fullDomain,
                IMGDB: imgDb,
                IMGBACKEND: imgBackend,
                IMGWEB: imgWeb,
                IMGLB: $"{app}-lb:{newestTag}",
                isDebug: session.Debug,
                connectionId: connectionId
            );

            await SendLogWithDelayAsync(session.Debug, connectionId, $"📝 [Node {AppContext.ServerID}] Hoàn tất đồng bộ dữ liệu điều phối và cập nhật trạng thái Gateway...");

            foreach (var port in PortsList)
            {
                await RedisPortService.ReleasePortsAsync(AppContext.ServerIP, PortsList.ToArray());
            }

            await RedisInstanceService.InsertInstanceAsync(AppContext.ServerIP);
            await RedisInstanceService.UpdateInstanceValueAsync(AppContext.ServerIP, app);

            var result = await DockerService.Read.DockerReadContainer.GetContainersByFilterAsync(app);
            foreach (var container in result)
            {
                string containerappname = container.Name;
                string containerappexport = container.OutPorts;
                await RedisContainerService.InsertContainerAsync(AppContext.ServerIP);
                await RedisContainerService.UpdateContainerValueAsync(AppContext.ServerIP, containerappname, containerappexport);
            }

            string nginxStatus = "";
            if (PortsList.Count > 0)
            {
                List<string> portsListStr = PortsList
                            .Select(p => $"{AppContext.ServerIP}:{p}")
                            .ToList();

                foreach (var port in portsListStr)
                {
                    (_, string message) = await RedisDomainService.InsertDomainAsync(fullDomain, port);
                    nginxStatus += message + "\n";
                }
            }

            string displayRuntimePath = Path.Combine(userHome, "ChatOps", "docker", "runtime", app).Replace('\\', '/');

            return $"✅ DEPLOY COMPOSE PRODUCTION SUCCESS\n\n" +
                $"📦 Service: {service}\n" +
                $"🏷️ Tag Version Active: `{newestTag}`\n" +
                $"👤 Owner: {owner}\n" +
                $"🌐 Domain: {fullDomain}\n" +
                $"🔌 Public Port (LB): {string.Join(",", PortsList)}\n" +
                $"📂 Runtime Active Path: `{displayRuntimePath}`\n\n" +
                $"⚙️ Chi tiết logs deploy:\n{clusterDeployResult}\n\n" +
                $"🌐 Redis Sync Gateway Status: {nginxStatus}";
        }
    }
}

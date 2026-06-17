using ChatOps.Controllers;
using ChatOps.Models;
using ChatOps.Services.ChatService;
using ChatOps.Services.SystemService;
using System.Collections.Concurrent;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.BackGroundService
{
    public partial class ChatOpsBackgroundService
    {
        private static readonly ConcurrentDictionary<string, DateTime> _serviceScaleTime = new ConcurrentDictionary<string, DateTime>();
        private static readonly ConcurrentDictionary<string, int> _serviceIdleCounters = new ConcurrentDictionary<string, int>();

        private async Task StartMonitor(CancellationToken stoppingToken)
        {
            Console.WriteLine("🚀 Monitor started");

            while (!stoppingToken.IsCancellationRequested)
            {
                if (_isRunning)
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                _isRunning = true;

                try
                {
                    await MonitorAllApps();
                }
                catch (Exception ex)
                {
                    LogService.LogService.WriteAppLog("system", "❌ MONITOR ERROR: " + ex.Message);
                }
                finally
                {
                    _isRunning = false;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        private async Task MonitorAllApps()
        {
            var apps = AppContext.AlertTargets.ToList();

            var monitorTasks = apps.Select(async app =>
            {
                try
                {
                    await MonitorSingleApp(app);
                }
                catch (Exception ex)
                {
                    LogService.LogService.WriteAppLog(app, "❌ APP MONITOR ERROR: " + ex.Message);
                }
            });

            await Task.WhenAll(monitorTasks);
        }

        private async Task MonitorSingleApp(string app)
        {
            var webListTask = DockerService.Read.DockerReadContainer.GetContainersByFilterAsync(app, "web");
            var backendListTask = DockerService.Read.DockerReadContainer.GetContainersByFilterAsync(app, "backend");
            var lbListTask = DockerService.Read.DockerReadContainer.GetContainersByFilterAsync(app, "lb");

            await Task.WhenAll(webListTask, backendListTask, lbListTask);

            var webList = await webListTask;
            var backendList = await backendListTask;
            var lbList = await lbListTask;

            if (webList.Count == 0 && backendList.Count == 0 && lbList.Count == 0)
            {
                return;
            }

            var webCpusTask = webList.Count > 0
                ? Task.WhenAll(webList.Select(c => GetCpuWithTimeoutAsync(c.Name)))
                : Task.FromResult(Array.Empty<double>());

            var backendCpusTask = backendList.Count > 0
                ? Task.WhenAll(backendList.Select(c => GetCpuWithTimeoutAsync(c.Name)))
                : Task.FromResult(Array.Empty<double>());

            var lbCpusTask = lbList.Count > 0
                ? Task.WhenAll(lbList.Select(c => GetCpuWithTimeoutAsync(c.Name)))
                : Task.FromResult(Array.Empty<double>());

            await Task.WhenAll(webCpusTask, backendCpusTask, lbCpusTask);

            var webCpus = (await webCpusTask).ToList();
            var backendCpus = (await backendCpusTask).ToList();
            var lbCpus = (await lbCpusTask).ToList();

            double maxWebCpu = webCpus.Count > 0 ? webCpus.Where(c => c >= 0).DefaultIfEmpty(0).Max() : 0;
            double maxBackendCpu = backendCpus.Count > 0 ? backendCpus.Where(c => c >= 0).DefaultIfEmpty(0).Max() : 0;
            double maxLbCpu = lbCpus.Count > 0 ? lbCpus.Where(c => c >= 0).DefaultIfEmpty(0).Max() : 0;

            LogService.LogService.WriteAppLog(app, $"📊 [CPU MONITOR] LB_COUNT={lbList.Count} (MAX_CPU={maxLbCpu:F1}%) | WEB_COUNT={webList.Count} (MAX_CPU={maxWebCpu:F1}%) | BACKEND_COUNT={backendList.Count} (MAX_CPU={maxBackendCpu:F1}%)");

            string runtimeDir = $"/home/ubuntu/ChatOps/docker/Apps/{app}";
            string coreComposeFile = File.Exists(Path.Combine(runtimeDir, "docker-registry.yml")) ? "docker-registry.yml" : "docker-git.yml";
            string lbComposeFile = "docker-compose-lb.yml";

            var systemSession = new UserSession { Username = "admin", Role = "admin", Debug = false };
            var scaleTasks = new List<Task>();

            if (webList.Count > 0)
            {
                string key = $"{app}_web";
                bool hasErrorValue = webCpus.Any(cpu => cpu < 0);

                if (hasErrorValue)
                {
                    ResetIdleCounter(key); 
                }
                else
                {
                    bool isAnyWebOverloaded = webCpus.Any(cpu => cpu >= 70.0);
                    if (isAnyWebOverloaded && webList.Count < 10)
                    {
                        ResetIdleCounter(key);
                        if (CanScaleService(key))
                        {
                            int target = webList.Count + 1;
                            LogService.LogService.WriteAppLog(app, $"🚀 [WEB OVERLOAD] Tu dong tang quy mo Web: {webList.Count} → {target}");
                            scaleTasks.Add(ExecuteScaleAndUpdateCooldownAsync(app, "web", target, new List<string> { "web" }, runtimeDir, coreComposeFile, lbComposeFile, systemSession, "UP", key));
                        }
                    }
                    else if (webCpus.All(cpu => cpu < 20.0) && webList.Count > 1)
                    {
                        int currentIdle = IncrementIdleCounter(key);
                        if (currentIdle >= 5)
                        {
                            if (CanScaleService(key))
                            {
                                int target;
                                string modeMsg;

                                if (webCpus.All(cpu => cpu < 1.0))
                                {
                                    target = 1;
                                    modeMsg = "[CRITICAL IDLE - FAST DROP]";
                                }
                                else
                                {
                                    target = webList.Count - 1;
                                    modeMsg = "[WEB IDLE]";
                                }

                                LogService.LogService.WriteAppLog(app, $"📉 {modeMsg} Du 5 lan kiem tra ranh lien tiep. Ha quy mo Web: {webList.Count} → {target}");
                                scaleTasks.Add(ExecuteScaleAndUpdateCooldownAsync(app, "web", target, new List<string> { "web" }, runtimeDir, coreComposeFile, lbComposeFile, systemSession, "DOWN", key));
                                ResetIdleCounter(key);
                            }
                        }
                    }
                    else
                    {
                        ResetIdleCounter(key);
                    }
                }
            }

            if (backendList.Count > 0)
            {
                string key = $"{app}_backend";
                bool hasErrorValue = backendCpus.Any(cpu => cpu < 0);

                if (hasErrorValue)
                {
                    ResetIdleCounter(key);
                }
                else
                {
                    bool isAnyBackendOverloaded = backendCpus.Any(cpu => cpu >= 75.0);
                    if (isAnyBackendOverloaded && backendList.Count < 10)
                    {
                        ResetIdleCounter(key);
                        if (CanScaleService(key))
                        {
                            int target = backendList.Count + 1;
                            LogService.LogService.WriteAppLog(app, $"🚀 [BACKEND OVERLOAD] Tu dong tang quy mo Backend: {backendList.Count} → {target}");
                            scaleTasks.Add(ExecuteScaleAndUpdateCooldownAsync(app, "backend", target, new List<string> { "backend" }, runtimeDir, coreComposeFile, lbComposeFile, systemSession, "UP", key));
                        }
                    }
                    else if (backendCpus.All(cpu => cpu < 20.0) && backendList.Count > 1)
                    {
                        int currentIdle = IncrementIdleCounter(key);
                        if (currentIdle >= 5)
                        {
                            if (CanScaleService(key))
                            {
                                int target;
                                string modeMsg;

                                if (backendCpus.All(cpu => cpu < 1.0))
                                {
                                    target = 1;
                                    modeMsg = "[CRITICAL IDLE - FAST DROP]";
                                }
                                else
                                {
                                    target = backendList.Count - 1;
                                    modeMsg = "[BACKEND IDLE]";
                                }

                                LogService.LogService.WriteAppLog(app, $"📉 {modeMsg} Du 5 lan kiem tra ranh lien tiep. Ha quy mo Backend: {backendList.Count} → {target}");
                                scaleTasks.Add(ExecuteScaleAndUpdateCooldownAsync(app, "backend", target, new List<string> { "backend" }, runtimeDir, coreComposeFile, lbComposeFile, systemSession, "DOWN", key));
                                ResetIdleCounter(key);
                            }
                        }
                    }
                    else
                    {
                        ResetIdleCounter(key);
                    }
                }
            }

            if (lbList.Count > 0)
            {
                string key = $"{app}_lb";
                bool hasErrorValue = lbCpus.Any(cpu => cpu < 0);

                if (hasErrorValue)
                {
                    ResetIdleCounter(key);
                }
                else
                {
                    bool isAnyLbOverloaded = lbCpus.Any(cpu => cpu >= 70.0);
                    if (isAnyLbOverloaded && lbList.Count < 10)
                    {
                        ResetIdleCounter(key);
                        if (CanScaleService(key))
                        {
                            int target = lbList.Count + 1;
                            LogService.LogService.WriteAppLog(app, $"🚀 [LB OVERLOAD] Tu dong tang quy mo LB: {lbList.Count} → {target}");
                            scaleTasks.Add(ExecuteScaleAndUpdateCooldownAsync(app, "lb", target, new List<string>(), runtimeDir, coreComposeFile, lbComposeFile, systemSession, "UP", key));
                        }
                    }
                    else if (lbCpus.All(cpu => cpu < 20.0) && lbList.Count > 1)
                    {
                        int currentIdle = IncrementIdleCounter(key);
                        if (currentIdle >= 5)
                        {
                            if (CanScaleService(key))
                            {
                                int target;
                                string modeMsg;

                                if (lbCpus.All(cpu => cpu < 1.0))
                                {
                                    target = 1;
                                    modeMsg = "[CRITICAL IDLE - FAST DROP]";
                                }
                                else
                                {
                                    target = lbList.Count - 1;
                                    modeMsg = "[LB IDLE]";
                                }

                                LogService.LogService.WriteAppLog(app, $"📉 {modeMsg} Du 5 lan kiem tra ranh lien tiep. Ha quy mo LB: {lbList.Count} → {target}");
                                scaleTasks.Add(ExecuteScaleAndUpdateCooldownAsync(app, "lb", target, new List<string>(), runtimeDir, coreComposeFile, lbComposeFile, systemSession, "DOWN", key));
                                ResetIdleCounter(key);
                            }
                        }
                    }
                    else
                    {
                        ResetIdleCounter(key);
                    }
                }
            }

            if (scaleTasks.Count > 0)
            {
                await Task.WhenAll(scaleTasks);
                return;
            }

            await DockerService.Update.DockerUpdateStorageApp.CheckDatabaseAsync(app);
        }

        private async Task<double> GetCpuWithTimeoutAsync(string containerName)
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    return await DockerService.Read.DockerReadLogsStats.GetContainerCpuAsync(containerName);
                }
            }
            catch (Exception)
            {
                return -1.0;
            }
        }

        private async Task ExecuteScaleAndUpdateCooldownAsync(string app, string typeStr, int target, List<string> scalableCoreServices, string runtimeDir, string coreComposeFile, string lbComposeFile, UserSession session, string direction, string serviceKey)
        {
            var result = await ChatDevOpsService.ExecuteScaleLogicAsync(app, typeStr, target, scalableCoreServices, runtimeDir, coreComposeFile, lbComposeFile, session);

            if (result.Success)
            {
                LogService.LogService.WriteAppLog(app, $"✅ AUTO SCALE {direction} {typeStr.ToUpper()} THANH CONG");
                _serviceScaleTime[serviceKey] = DateTime.Now;
            }
            else
            {
                LogService.LogService.WriteAppLog(app, $"❌ AUTO SCALE {direction} {typeStr.ToUpper()} THAT BAI: {result.Log}");
            }
        }

        private bool CanScaleService(string serviceKey)
        {
            if (!_serviceScaleTime.ContainsKey(serviceKey))
            {
                _serviceScaleTime[serviceKey] = DateTime.MinValue;
            }

            TimeSpan elapsed = DateTime.Now - _serviceScaleTime[serviceKey];
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

            return elapsed > TimeSpan.FromSeconds(60);
        }

        private int IncrementIdleCounter(string serviceKey)
        {
            return _serviceIdleCounters.AddOrUpdate(serviceKey, 1, (key, oldVal) => oldVal + 1);
        }

        private void ResetIdleCounter(string serviceKey)
        {
            _serviceIdleCounters[serviceKey] = 0;
        }
    }
}
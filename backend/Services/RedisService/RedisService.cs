using System.Text.Json;
using ChatOps.Controllers;
using ChatOps.Models;
using ChatOps.Services.SystemService;
using AppContext = ChatOps.Data.AppContext;
using ChatOps.Services.FileService;

namespace ChatOps.Services.RedisService
{
    public class RedisService
    {
        private readonly IServiceProvider _serviceProvider;

        public RedisService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            RegisterRedisEvents();
        }

        private void RegisterRedisEvents()
        {
            RedisChannelService.OnClusterRequestReceived += async (request) =>
            {
                var response = new ClusterMessage { IsSuccess = false };

                using var ctsKeepAlive = new CancellationTokenSource();
                var keepAliveTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!ctsKeepAlive.Token.IsCancellationRequested)
                        {
                            await Task.Delay(15000, ctsKeepAlive.Token);
                            if (ctsKeepAlive.Token.IsCancellationRequested) break;

                            Console.WriteLine($"[RedisService] ⏳ Đang thực thi lâu... Gửi Ping_Alive cập nhật timeout tới {request.SenderIp}");
                            await RedisChannelService.SendPingAliveAsync(request.SenderIp, request.RequestId);
                        }
                    }
                    catch (TaskCanceledException) {}
                });

                try
                {
                    switch (request.Type)
                    {
                        case ClusterPayloadType.DockerCommand:
                            var payload = JsonSerializer.Deserialize<DockerCommandPayload>(request.Content);
                            if (payload == null)
                            {
                                response.ErrorMessage = "Payload is null";
                                break;
                            }

                            using (var scope = _serviceProvider.CreateScope())
                            {
                                var chatController = scope.ServiceProvider.GetRequiredService<ChatController>();

                                // Giai đoạn trung gian: Báo tiến độ cho Client biết Node B đang làm việc
                                await RedisChannelService.SendMessageToClientAsync(payload.Session.Debug, payload.connectionId, $"🚀 [Node {AppContext.ServerID}] Đang tiến hành thực thi cục bộ từ xa: {payload.action}...");

                                string result = await chatController.ExecuteCommandRouteAsync(payload.action, payload.parsed, payload.Session, payload.connectionId);

                                // THAY ĐỔI QUAN TRỌNG: Không gửi thẳng 'result' cho client tại đây nữa.
                                // Node B đóng gói kết quả vào gói tin trả về để gửi ngược lại cho Node A tổng hợp.
                                response.Content = result;
                                response.IsSuccess = true;
                            }
                            break;

                        case ClusterPayloadType.TextMessage:
                            var msgParts = request.Content.Split('|');
                            string cmdType = msgParts[0];

                            if (cmdType == "Req_Check_Port")
                            {
                                var ports = msgParts[1].Split(',').Select(p => p.Trim()).ToList();
                                var resultsList = new List<string>();

                                foreach (var port in ports)
                                {
                                    if (int.TryParse(port, out int portNumber))
                                    {
                                        bool isAvailable = await SystemCommandService.IsHostPortAvailableAsync(portNumber);
                                        resultsList.Add($"{portNumber}:{(isAvailable ? "FREE" : "BUSY")}");
                                    }
                                    else { resultsList.Add($"{port}:INVALID"); }
                                }
                                response.Content = string.Join(",", resultsList);
                                response.IsSuccess = true;
                            }
                            else if (cmdType == "Req_Rename_Container")
                            {
                                string result = await DockerService.Update.DockerUpdateLifecycle.RenameContainerAsync(msgParts[1], msgParts[2]);
                                response.Content = result;
                                response.IsSuccess = true;
                            }
                            else if (cmdType == "Req_Delete_Container")
                            {
                                string result = await DockerService.Delete.DockerDeleteContainer.RemoveContainerForceAsync(msgParts[1]);
                                response.Content = result;
                                response.IsSuccess = true;
                            }
                            else if (cmdType == "Req_GetDetail_Container")
                            {
                                DockerContainer? detail = await DockerService.Read.DockerReadContainer.GetContainerDetailsAsync(msgParts[1]);
                                ContainerMetadata? meta = await DockerService.Read.DockerReadMetadata.GetMetadataAsync(msgParts[1]);
                                ContainerNetworkDetails? net = await DockerService.Read.DockerReadNetwork.GetContainerNetworkDetailsAsync(msgParts[1]);

                                response.Content = JsonSerializer.Serialize(new { detail, meta, net });
                                response.IsSuccess = true;
                            }
                            else if(cmdType == "Req_GetFileServers_Container")
                            {
                                List<string> resultList = await PgAdminFileConfigurator.GetAttachedHosts(msgParts[1]);
                                string result = "";
                                if(resultList.Count != 0)
                                    result = string.Join("\n", resultList);
                                response.Content = result;
                                response.IsSuccess = true;
                            }
                            else if(cmdType == "Req_GetFileWeb_Container")
                            {
                                string result = await FileWebContainer.GetHTML(msgParts[1]);
                                response.Content = result;
                                response.IsSuccess = true;
                            }
                            else if(cmdType == "Req_UpdateFileWeb_Container")
                            {
                                string result = await FileWebContainer.SetHTML(msgParts[1], msgParts[2]);
                                response.Content = result;
                                response.IsSuccess = true;
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    response.IsSuccess = false;
                    response.ErrorMessage = ex.Message;
                    Console.WriteLine($"[RedisService] Execution Error: {ex}");
                }
                finally
                {
                    ctsKeepAlive.Cancel();
                    try { await keepAliveTask; } catch {}
                }

                return response;
            };
        }

        // THAY ĐỔI: Node A gọi hàm này, nhận kết quả toàn vẹn từ Node B, tổng hợp lại rồi mới đẩy cho client.
        public static async Task<string> ExecuteDockerRemoteAsync(string action, Dictionary<string, string> parsed, UserSession session, string connectionId, string targetIp)
        {
            var payload = new DockerCommandPayload
            {
                action = action,
                parsed = parsed,
                Session = session,
                connectionId = connectionId
            };

            string jsonPayload = JsonSerializer.Serialize(payload);
            
            // Node A phát lệnh sang Node B và đợi phản hồi qua hệ thống Pub/Sub
            var response = await RedisChannelService.SendRequestToBackendAsync(targetIp, ClusterPayloadType.DockerCommand, jsonPayload);
            
            if (!response.IsSuccess)
            {
                throw new Exception($"Thực thi từ xa thất bại: {response.ErrorMessage}");
            }

            // Trả chuỗi kết quả về cho luồng gọi của Node A tiếp tục tổng hợp hoặc xử lý logic
            return response.Content;
        }

        public static async Task<string> SendPortCheckRequestAsync(string targetBackendIp, string portsToCheck)
        {
            var response = await RedisChannelService.SendRequestToBackendAsync(targetBackendIp, ClusterPayloadType.TextMessage, $"Req_Check_Port|{portsToCheck}");
            return response.IsSuccess ? response.Content : $"ERROR|{response.ErrorMessage}";
        }

        public static async Task<string> SendRenameContainerRequestAsync(string targetBackendIp, string containername, string newname)
        {
            var response = await RedisChannelService.SendRequestToBackendAsync(targetBackendIp, ClusterPayloadType.TextMessage, $"Req_Rename_Container|{containername}|{newname}");
            return response.IsSuccess ? response.Content : $"ERROR|{response.ErrorMessage}";
        }

        public static async Task<string> SendDeleteContainerRequestAsync(string targetBackendIp, string containername)
        {
            var response = await RedisChannelService.SendRequestToBackendAsync(targetBackendIp, ClusterPayloadType.TextMessage, $"Req_Delete_Container|{containername}");
            return response.IsSuccess ? response.Content : $"ERROR|{response.ErrorMessage}";
        }

        public static async Task<(DockerContainer? detail, ContainerMetadata? meta, ContainerNetworkDetails? net)> SendGetDetailContainerRequestAsync(string targetBackendIp, string containername)
        {
            var response = await RedisChannelService.SendRequestToBackendAsync(targetBackendIp, ClusterPayloadType.TextMessage, $"Req_GetDetail_Container|{containername}");
            
            if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content)) 
                return (null, null, null);

            try
            {
                using var doc = JsonDocument.Parse(response.Content);
                var root = doc.RootElement;

                // Trích xuất các thuộc tính dạng Json thô và Deserialize sang Object đích
                var detail = root.TryGetProperty("detail", out var detailProp) && detailProp.ValueKind != JsonValueKind.Null
                    ? JsonSerializer.Deserialize<DockerContainer>(detailProp.GetRawText()) 
                    : null;

                var meta = root.TryGetProperty("meta", out var metaProp) && metaProp.ValueKind != JsonValueKind.Null
                    ? JsonSerializer.Deserialize<ContainerMetadata>(metaProp.GetRawText()) 
                    : null;

                var net = root.TryGetProperty("net", out var netProp) && netProp.ValueKind != JsonValueKind.Null
                    ? JsonSerializer.Deserialize<ContainerNetworkDetails>(netProp.GetRawText()) 
                    : null;

                return (detail, meta, net);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [RedisService][SendGetDetailContainerRequestAsync] Parse Error: {ex.Message}");
                return (null, null, null);
            }
        }

        public static async Task<string> SendGetFileServersRequestAsync(string targetBackendIp, string containername)
        {
            var response = await RedisChannelService.SendRequestToBackendAsync(targetBackendIp, ClusterPayloadType.TextMessage, $"Req_GetFileServers_Container|{containername}");
            return response.IsSuccess ? response.Content : $"ERROR|{response.ErrorMessage}";
        }

        public static async Task<string> SendGetFileWebRequestAsync(string targetBackendIp, string containername)
        {
            var response = await RedisChannelService.SendRequestToBackendAsync(targetBackendIp, ClusterPayloadType.TextMessage, $"Req_GetFileWeb_Container|{containername}");
            return response.IsSuccess ? response.Content : $"ERROR|{response.ErrorMessage}";
        }

        public static async Task<string> SendUpdateFileWebRequestAsync(string targetBackendIp, string containername, string content)
        {
            var response = await RedisChannelService.SendRequestToBackendAsync(targetBackendIp, ClusterPayloadType.TextMessage, $"Req_UpdateFileWeb_Container|{containername}|{content}");
            return response.IsSuccess ? response.Content : $"ERROR|{response.ErrorMessage}";
        }
    }
}
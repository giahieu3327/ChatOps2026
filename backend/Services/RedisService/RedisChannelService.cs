using System.Collections.Concurrent;
using System.Text.Json;
using ChatOps.Data;
using ChatOps.Hubs;
using ChatOps.Models;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.RedisService
{
    public class TimeoutTracker
    {
        public TaskCompletionSource<ClusterMessage> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts = new();
        private readonly int _timeoutMs;
        private Action? _onTimeout;

        public TimeoutTracker(int timeoutMs, Action onTimeout)
        {
            _timeoutMs = timeoutMs;
            _onTimeout = onTimeout;
            ResetTimeout();
        }

        public void ResetTimeout()
        {
            _cts.CancelAfter(_timeoutMs);
            var registration = _cts.Token.Register(() =>
            {
                if (Tcs.TrySetException(new TimeoutException($"Yêu cầu xử lý vượt quá thời gian quy định.")))
                {
                    _onTimeout?.Invoke();
                }
            });
        }

        public void Close()
        {
            _onTimeout = null;
            _cts.Cancel();
            _cts.Dispose();
        }
    }

    public static class RedisChannelService
    {
        private static readonly ConcurrentDictionary<string, TimeoutTracker> _pendingRequests = new();

        public static event Func<ClusterMessage, Task<ClusterMessage>>? OnClusterRequestReceived;

        public static void RegisterInternalChannels(IHubContext<ChatHub> hubContext)
        {
            RegisterBackendChannel();
            RegisterSignalRChannel(hubContext);
        }

        private static void RegisterBackendChannel()
        {
            var channelName = RedisChannel.Literal(ClusterChannels.BACKEND_CLUSTER_CHANNEL);

            AppContext.RedisPubSub.Subscribe(channelName, async (channel, value) =>
            {
                try
                {
                    var clusterMsg = JsonSerializer.Deserialize<ClusterMessage>(value.ToString());
                    if (clusterMsg == null) return;
                    if (clusterMsg.SenderIp == AppContext.ServerIP) return;

                    if (clusterMsg.Type == ClusterPayloadType.Ping_Alive)
                    {
                        if (_pendingRequests.TryGetValue(clusterMsg.RequestId, out var controller))
                        {
                            Console.WriteLine($"[RedisChannelService] 💓 Nhận gói Ping_Alive từ {clusterMsg.SenderIp}. Gia hạn thêm timeout.");
                            controller.ResetTimeout();
                        }
                        return;
                    }

                    if (clusterMsg.Type == ClusterPayloadType.Response)
                    {
                        if (_pendingRequests.TryRemove(clusterMsg.RequestId, out var controller))
                        {
                            controller.Tcs.TrySetResult(clusterMsg);
                            controller.Close();
                        }
                        return;
                    }

                    if (clusterMsg.TargetIp != AppContext.ServerIP) return;

                    if (OnClusterRequestReceived != null)
                    {
                        var response = await OnClusterRequestReceived.Invoke(clusterMsg);

                        response.RequestId = clusterMsg.RequestId;
                        response.Type = ClusterPayloadType.Response;
                        response.SenderIp = AppContext.ServerIP;
                        response.TargetIp = clusterMsg.SenderIp;

                        await AppContext.RedisPubSub.PublishAsync(channelName, JsonSerializer.Serialize(response));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [RedisChannelService][BACKEND_CHANNEL] Error: {ex.Message}");
                }
            });
        }

        private static void RegisterSignalRChannel(IHubContext<ChatHub> hubContext)
        {
            var channelName = RedisChannel.Literal(ClusterChannels.SIGNALR_CLUSTER_CHANNEL);

            AppContext.RedisPubSub.Subscribe(channelName, async (channel, value) =>
            {
                try
                {
                    string data = value.ToString();
                    int firstPipe = data.IndexOf('|');
                    if (firstPipe <= 0) return;

                    string connectionId = data.Substring(0, firstPipe);
                    string message = data.Substring(firstPipe + 1);

                    await hubContext.Clients.Client(connectionId).SendAsync("ReceiveMessage", message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [RedisChannelService][SIGNALR_CHANNEL] Error: {ex.Message}");
                }
            });
        }

        public static async Task SendMessageToClientAsync(bool isDebugMode, string connectionId, string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(connectionId)) return;

                // 1. Kiểm tra bộ lọc log dựa trên trạng thái debug trước khi publish vào hệ thống Cluster
                if (!ShouldSendMessage(message, isDebugMode))
                {
                    return; // Chặn lại ngay tại Node xử lý, giảm tải băng thông Redis Pub/Sub
                }

                string payload = $"{connectionId}|{message}";
                var channelName = RedisChannel.Literal(ClusterChannels.SIGNALR_CLUSTER_CHANNEL);
                await AppContext.RedisPubSub.PublishAsync(channelName, payload);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [RedisChannelService][SendMessageToClientAsync] Error: {ex.Message}");
            }
        }

        public static bool ShouldSendMessage(string message, bool isDebugMode)
        {
            if (string.IsNullOrEmpty(message)) return false;

            // Nếu bật debug mode thì hiển thị tuốt tuột, không cần lọc
            if (isDebugMode) return true;

            // Lọc theo AllowedIcons: Kiểm tra xem tin nhắn có bắt đầu bằng icon hợp lệ nào không
            foreach (var icon in AppContext.AllowedIcons)
            {
                if (message.StartsWith(icon))
                {
                    return true; // Hợp lệ, cho phép gửi về client
                }
            }

            return false; // Không nằm trong AllowedIcons thì chặn lại
        }

        public static async Task SendPingAliveAsync(string targetBackendIp, string requestId)
        {
            try
            {
                var pingMsg = new ClusterMessage
                {
                    RequestId = requestId,
                    Type = ClusterPayloadType.Ping_Alive,
                    SenderIp = AppContext.ServerIP,
                    TargetIp = targetBackendIp
                };
                var channelName = RedisChannel.Literal(ClusterChannels.BACKEND_CLUSTER_CHANNEL);
                await AppContext.RedisPubSub.PublishAsync(channelName, JsonSerializer.Serialize(pingMsg));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [RedisChannelService][SendPingAliveAsync] Error: {ex.Message}");
            }
        }

        public static async Task<ClusterMessage> SendRequestToBackendAsync(string targetBackendIp, ClusterPayloadType type, string content, int timeoutMilliseconds = 30000)
        {
            try
            {
                var request = new ClusterMessage
                {
                    Type = type,
                    SenderIp = AppContext.ServerIP,
                    TargetIp = targetBackendIp,
                    Content = content
                };

                var controller = new TimeoutTracker(timeoutMilliseconds, () =>
                {
                    _pendingRequests.TryRemove(request.RequestId, out _);
                });

                _pendingRequests[request.RequestId] = controller;

                var channelName = RedisChannel.Literal(ClusterChannels.BACKEND_CLUSTER_CHANNEL);
                await AppContext.RedisPubSub.PublishAsync(channelName, JsonSerializer.Serialize(request));

                return await controller.Tcs.Task;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [RedisChannelService][SendRequestToBackendAsync] Error: {ex.Message}");
                throw;
            }
        }
    }
}
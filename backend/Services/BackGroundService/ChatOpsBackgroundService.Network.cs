using System.Net;
using System.Net.Sockets;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.BackGroundService
{
    public partial class ChatOpsBackgroundService
    {
        private async Task StartIpUpdater(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    UpdateControllerIp();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Update IP lỗi: {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        private void UpdateControllerIp()
        {
            string oldIp = AppContext.ServerIP;
            string newIp = GetLocalIp();

            if (oldIp != newIp)
            {
                Console.WriteLine($"🔄 IP changed: {oldIp} -> {newIp}");
                AppContext.ServerIP = newIp;
            }
            else
            {
                Console.WriteLine($"✔ IP unchanged: {newIp}");
            }
        }

        public string GetLocalIp()
        {
            try
            {
                using var socket = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Dgram,
                    ProtocolType.Udp);

                socket.Connect(AppContext.RedisIP, 6379);

                if (socket.LocalEndPoint is IPEndPoint endPoint)
                {
                    return endPoint.Address.ToString();
                }
            }
            catch
            {
                try
                {
                    var host =
                        Dns.GetHostEntry(Dns.GetHostName());

                    foreach (var ip in host.AddressList)
                    {
                        if (ip.AddressFamily == AddressFamily.InterNetwork
                            && !IPAddress.IsLoopback(ip))
                        {
                            return ip.ToString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"❌ GetLocalIp Error: {ex.Message}");
                }
            }

            throw new Exception(
                "Không thể lấy Local IP.");
        }
    }
}
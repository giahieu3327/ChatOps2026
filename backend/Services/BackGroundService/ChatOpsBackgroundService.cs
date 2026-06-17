
namespace ChatOps.Services.BackGroundService
{
    public partial class ChatOpsBackgroundService : BackgroundService
    {
        private bool _isRunning = false;

        private readonly Dictionary<string, long> _lastReqCount = new();
        private readonly Dictionary<string, DateTime> _lastScaleTime = new();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("🚀 ChatOps Background Service Started");

            _ = StartMonitor(stoppingToken);
            _ = StartIpUpdater(stoppingToken);
            _ = ProcessNodeHeartbeat(stoppingToken);
            _ = ProcessServicesHeartbeat(stoppingToken);
            _ = ProcessContainerHeartbeat(stoppingToken);
            _ = ProcessHistoryReconcile(stoppingToken);
            _ = ProcessSessionReconcile(stoppingToken);
            _ = ProcessInstanceHeartbeat(stoppingToken);
            _ = ProcessImageServiceHeartbeat(stoppingToken);
            _ = ProcessAppServiceHeartbeat(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(10000, stoppingToken);
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await ShutdownNodeCluster();
            await ShutdownServicesCluster();
            await ShutdownContainersCluster();
            await ShutdownSessionReconcile();
            await ShutdownInstancesCluster();
            await ShutdownImageServiceCluster();
            await ShutdownAppServiceCluster();
            await base.StopAsync(cancellationToken);
        }
    }
}
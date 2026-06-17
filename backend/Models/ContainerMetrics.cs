namespace ChatOps.Models
{
    public class ContainerMetrics
    {
        public string ContainerName { get; set; } = string.Empty;
        public double CpuPercentage { get; set; }
        public long RequestDelta { get; set; } // Số lượng request mới phát sinh trong chu kỳ kiểm tra
    }
}
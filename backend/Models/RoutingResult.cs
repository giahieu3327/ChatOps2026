    public class RoutingResult
    {
        public bool IsError { get; set; }
        public bool IsForwarding { get; set; }
        public string? ErrorMessage { get; set; }
        public string TargetNodeIp { get; set; } = string.Empty;
    }
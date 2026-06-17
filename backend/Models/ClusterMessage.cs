using System;

namespace ChatOps.Models
{
    public enum ClusterPayloadType
    {
        TextMessage,
        DockerCommand,
        Ping_Alive,
        Response
    }

    public class ClusterMessage
    {
        public string RequestId { get; set; } = Guid.NewGuid().ToString();
        public ClusterPayloadType Type { get; set; }
        public string SenderIp { get; set; } = string.Empty;
        public string TargetIp { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public bool IsSuccess { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
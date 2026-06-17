using System;
using System.Collections.Generic;

namespace ChatOps.Models
{
    public class ContainerMetadata
    {
        public string ContainerName { get; set; } = string.Empty;
        public string Service { get; set; } = string.Empty;
        public string AppProject { get; set; } = string.Empty; // com.docker.compose.project
        public string ComposeService { get; set; } = string.Empty; // com.docker.compose.service
        public string ScaleIndex { get; set; } = string.Empty; // com.docker.compose.container-number
        public string DependsOn { get; set; } = string.Empty;
        public List<string> Owners { get; set; } = new List<string>();
        public string DbType { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string RestartPolicy { get; set; } = string.Empty;
        public Dictionary<string, string> Environments { get; set; } = new Dictionary<string, string>();
    }
}
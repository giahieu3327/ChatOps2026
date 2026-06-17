using System;
using System.Collections.Generic;

namespace ChatOps.Models
{
    public class DockerContainer
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty; // Trạng thái thô (running, exited, paused)
        public bool IsRunning { get; set; }
        public string Ports { get; set; } = string.Empty;   // Chuỗi port thô từ docker ps
        public string InPorts { get; set; } = string.Empty; // Cổng nội bộ container (Ví dụ: 80,5000)
        public string OutPorts { get; set; } = string.Empty;// Cổng public ra ngoài Host (Ví dụ: 8080,8081)
        
        // Bổ sung để hỗ trợ tính năng quét Label hệ thống
        public Dictionary<string, string> Labels { get; set; } = new Dictionary<string, string>();
    }
}
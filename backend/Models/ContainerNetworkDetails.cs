using System;
using System.Collections.Generic;

namespace ChatOps.Models
{
    public class ContainerNetworkDetails
    {
        public string ContainerName { get; set; } = string.Empty;
        public List<string> Networks { get; set; } = new List<string>();
        public string PrimaryInPort { get; set; } = string.Empty;  // Cổng nội bộ chính (Ví dụ: "80")
        public List<string> AllInPorts { get; set; } = new List<string>(); // Tất cả các cổng exposed nội bộ
        public string RawPortMappings { get; set; } = string.Empty; // Chuỗi map dạng "80/tcp -> 0.0.0.0:8080"
        public List<string> Links { get; set; } = new List<string>(); // Danh sách các container liên kết cũ (nếu có)
    }
}
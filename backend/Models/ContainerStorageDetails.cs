using System;
using System.Collections.Generic;

namespace ChatOps.Models
{
    public class StorageMountItem
    {
        public string Type { get; set; } = string.Empty;        // volume hoặc bind
        public string Source { get; set; } = string.Empty;      // Đường dẫn trên Host VPS
        public string Destination { get; set; } = string.Empty; // Đường dẫn bên trong Container
        public bool ReadOnly { get; set; }
    }

    public class ContainerStorageDetails
    {
        public string ContainerName { get; set; } = string.Empty;
        public List<StorageMountItem> Mounts { get; set; } = new List<StorageMountItem>();
    }
}
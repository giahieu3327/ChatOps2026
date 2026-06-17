using ChatOps.Data;

namespace ChatOps.Services.CommandParserService
{
    /// <summary>
    /// Đầu não phân tích cú pháp câu lệnh ChatOps và điều phối luồng xử lý xác thực payload.
    /// </summary>
    public static partial class CommandParserService
    {
        /// <summary>
        /// Tập hợp toàn bộ các KEY hợp lệ được tự động cấu thành từ 10 danh mục partial.
        /// </summary>
        private static readonly HashSet<string> UserAccountKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "email", "newemail", "role", "newrole", "username", "newusername", "password", "oldpass", "newpass"
        };
        private static readonly HashSet<string> DeployKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "image", "port", "name", "db", "username", "domain", "tool", "url", "app", "tag", "node", "version"
        };
        private static readonly HashSet<string> ContainerManagementKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "instance", "container", "image", "newcname", "newcusername", "newcdomain", "node"
        };
        private static readonly HashSet<string> AccessKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "instance", "container"
        };
        private static readonly HashSet<string> DebugKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "instance", "container", "lines"
        };
        private static readonly HashSet<string> BackupRollbackKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "instance", "tag"
        };
        private static readonly HashSet<string> MonitoringKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "instance", "container", "node"
        };
        private static readonly HashSet<string> DevOpsKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "instance", "type", "n"
        };
        private static readonly HashSet<string> CleanupKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "node"
        };
        private static readonly HashSet<string> SupportKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "command", "node"
        };
        private static readonly HashSet<string> ValidKeys = CombineAllCategories();

        // =====================================================
        // CORE PARSER METHOD (TÁCH KEY - VALUE)
        // =====================================================
        public static Dictionary<string, string> Parse(string cmd)
        {
            var result = new Dictionary<string, string>();

            if (string.IsNullOrWhiteSpace(cmd))
            {
                result["error"] = "❌ Câu lệnh trống.";
                return result;
            }

            var tokens = cmd.Trim().Split(" ", StringSplitOptions.RemoveEmptyEntries).ToList();
            if (tokens.Count == 0)
            {
                result["error"] = "❌ Câu lệnh trống.";
                return result;
            }

            result["rawcommand"] = cmd.Trim();

            string baseCommand = tokens[0].Trim().ToLower();
            result["base_command"] = baseCommand;

            // Kiểm tra cờ 'all' xuất hiện độc lập trong câu lệnh
            bool hasAllFlag = tokens.Any(t => t.Trim().Equals("all", StringComparison.OrdinalIgnoreCase));
            if (hasAllFlag)
            {
                result["all"] = "true";
            }

            // Duyệt từ token thứ 2 để bốc các cặp KEY và VALUE đi liền sau nó
            for (int i = 1; i < tokens.Count; i++)
            {
                string currentToken = tokens[i].Trim().ToLower();

                // Nếu từ hiện tại là một KEY hợp lệ đã được đăng ký
                if (ValidKeys.Contains(currentToken))
                {
                    // Lấy từ đứng kế tiếp làm VALUE
                    if (i + 1 < tokens.Count)
                    {
                        string valueToken = tokens[i + 1].Trim();

                        // Kiểm tra an toàn: Giá trị value không được trùng với một KEY khác hoặc từ 'all'
                        if (!ValidKeys.Contains(valueToken.ToLower()) && 
                            !valueToken.Equals("all", StringComparison.OrdinalIgnoreCase))
                        {
                            result[currentToken] = valueToken;
                            if(currentToken == "image" && !string.IsNullOrWhiteSpace(valueToken) && ImageCategories.ImageServices.TryGetValue(valueToken, out var service))
                            {
                                string serviceType = service.Type.ToLower().Trim();
                                result["serviceType"] = serviceType;
                                result["neededCount"] = service.neededCount;
                            }
                            i++; // Bỏ qua token tiếp theo vì đã lấy làm Value rồi
                        }
                    }
                }
            }

            result["success"] = "true";
            return result;
        }

        /// <summary>
        /// Gộp tự động các danh sách Key đăng ký từ 10 file partial lại làm một để tối ưu hóa tìm kiếm.
        /// </summary>
        private static HashSet<string> CombineAllCategories()
        {
            var combined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            combined.UnionWith(UserAccountKeys);
            combined.UnionWith(DeployKeys);
            combined.UnionWith(ContainerManagementKeys);
            combined.UnionWith(AccessKeys);
            combined.UnionWith(DebugKeys);
            combined.UnionWith(BackupRollbackKeys);
            combined.UnionWith(MonitoringKeys);
            combined.UnionWith(DevOpsKeys);
            combined.UnionWith(CleanupKeys);
            combined.UnionWith(SupportKeys);
            return combined;
        }
    }
}
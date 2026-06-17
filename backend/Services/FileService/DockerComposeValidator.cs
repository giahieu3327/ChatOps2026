using YamlDotNet.RepresentationModel;

namespace ChatOps.Services.FileService
{
    public static class DockerComposeValidator
    {
        private static readonly HashSet<string> AllowedTypes = new()
        {
            "frontend",
            "backend",
            "db"
        };

        public static async Task<(bool IsValid, string Message)> ValidateForDeployAsync(string composeFilePath)
        {
            var errors = new List<string>();

            try
            {
                if (!File.Exists(composeFilePath))
                    return (false, $"Không tìm thấy file: {composeFilePath}");

                string yamlContent = await File.ReadAllTextAsync(composeFilePath);

                var yaml = new YamlStream();

                using (var reader = new StringReader(yamlContent))
                {
                    yaml.Load(reader);
                }

                if (yaml.Documents.Count == 0)
                    return (false, "File YAML rỗng.");

                var root = (YamlMappingNode)yaml.Documents[0].RootNode;

                if (!root.Children.TryGetValue("services", out var servicesNode))
                    return (false, "Không tìm thấy section services.");

                var services = (YamlMappingNode)servicesNode;

                int frontendCount = 0;
                int backendCount = 0;

                var usedPorts = new Dictionary<string, string>();

                foreach (var serviceEntry in services.Children)
                {
                    string serviceName = serviceEntry.Key.ToString();

                    if (serviceEntry.Value is not YamlMappingNode serviceConfig)
                    {
                        errors.Add($"Service '{serviceName}' có cấu trúc không hợp lệ.");
                        continue;
                    }

                    // =====================================================
                    // IMAGE
                    // =====================================================

                    if (!serviceConfig.Children.ContainsKey("image"))
                    {
                        errors.Add($"Service '{serviceName}' chưa khai báo image.");
                    }

                    // =====================================================
                    // EXPOSE
                    // =====================================================

                    string exposePort = string.Empty;

                    if (!serviceConfig.Children.TryGetValue("expose", out var exposeNode))
                    {
                        errors.Add($"Service '{serviceName}' chưa khai báo expose.");
                    }
                    else
                    {
                        var exposeList = exposeNode as YamlSequenceNode;

                        if (exposeList == null || exposeList.Children.Count == 0)
                        {
                            errors.Add($"Service '{serviceName}' có expose nhưng chưa khai báo port.");
                        }
                        else
                        {
                            exposePort = exposeList.Children[0]
                                .ToString()
                                .Replace("\"", "");

                            if (usedPorts.ContainsKey(exposePort))
                            {
                                errors.Add(
                                    $"Port expose '{exposePort}' đang được sử dụng bởi cả '{usedPorts[exposePort]}' và '{serviceName}'.");
                            }
                            else
                            {
                                usedPorts[exposePort] = serviceName;
                            }
                        }
                    }

                    // =====================================================
                    // LABELS
                    // =====================================================

                    if (!serviceConfig.Children.TryGetValue("labels", out var labelsNode))
                    {
                        errors.Add($"Service '{serviceName}' chưa khai báo labels.");
                        continue;
                    }

                    if (labelsNode is not YamlMappingNode labels)
                    {
                        errors.Add($"Service '{serviceName}' có labels không hợp lệ.");
                        continue;
                    }

                    // owner
                    if (!labels.Children.ContainsKey("owner"))
                    {
                        errors.Add($"Service '{serviceName}' chưa khai báo labels.owner.");
                    }

                    // service
                    if (!labels.Children.ContainsKey("service"))
                    {
                        errors.Add($"Service '{serviceName}' chưa khai báo labels.service.");
                    }

                    // type
                    if (!labels.Children.TryGetValue("type", out var typeNode))
                    {
                        errors.Add($"Service '{serviceName}' chưa khai báo labels.type.");
                        continue;
                    }

                    string type = typeNode
                        .ToString()
                        .Trim()
                        .ToLower();

                    if (!AllowedTypes.Contains(type))
                    {
                        errors.Add(
                            $"Service '{serviceName}' có labels.type='{type}' không hợp lệ. Chỉ chấp nhận: frontend, backend, db.");
                        continue;
                    }

                    switch (type)
                    {
                        case "frontend":
                            frontendCount++;
                            break;

                        case "backend":
                            backendCount++;
                            break;
                    }
                }

                // =====================================================
                // FRONTEND
                // =====================================================

                if (frontendCount == 0)
                {
                    errors.Add("Không tìm thấy service có labels.type = frontend.");
                }
                else if (frontendCount > 1)
                {
                    errors.Add($"Tìm thấy {frontendCount} service frontend. Chỉ được phép có 1 frontend.");
                }

                // =====================================================
                // BACKEND
                // =====================================================

                if (backendCount == 0)
                {
                    errors.Add("Không tìm thấy service có labels.type = backend.");
                }
                else if (backendCount > 1)
                {
                    errors.Add($"Tìm thấy {backendCount} service backend. Chỉ được phép có 1 backend.");
                }

                if (errors.Any())
                {
                    return (
                        false,
                        string.Join(Environment.NewLine, errors.Select(x => $"• {x}"))
                    );
                }

                return (true, "SUCCESS");
            }
            catch (Exception ex)
            {
                return (false, $"YAML không hợp lệ hoặc lỗi khi phân tích file: {ex.Message}");
            }
        }
    }
}
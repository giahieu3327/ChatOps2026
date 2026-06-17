using YamlDotNet.RepresentationModel;

namespace ChatOps.Services.FileService
{
    public static class DockerComposeAnalyzer
    {
        public static async Task<NginxTarget> GetNginxTargetsAsync(string composeFilePath)
        {
            var result = new NginxTarget();

            if (!File.Exists(composeFilePath))
                return result;

            string yamlContent = await File.ReadAllTextAsync(composeFilePath);

            var yaml = new YamlStream();

            using (var reader = new StringReader(yamlContent))
            {
                yaml.Load(reader);
            }

            var root = (YamlMappingNode)yaml.Documents[0].RootNode;

            if (!root.Children.TryGetValue("services", out var servicesNode))
                return result;

            var services = (YamlMappingNode)servicesNode;

            foreach (var service in services.Children)
            {
                string serviceName = service.Key.ToString();

                var serviceConfig = (YamlMappingNode)service.Value;

                string port = "";
                string type = "";

                // expose
                if (serviceConfig.Children.TryGetValue("expose", out var exposeNode))
                {
                    var exposeList = (YamlSequenceNode)exposeNode;

                    if (exposeList.Children.Count > 0)
                    {
                        port = exposeList.Children[0]
                            .ToString()
                            .Replace("\"", "");
                    }
                }

                // labels.type
                if (serviceConfig.Children.TryGetValue("labels", out var labelsNode))
                {
                    var labels = (YamlMappingNode)labelsNode;

                    if (labels.Children.TryGetValue("type", out var typeNode))
                    {
                        type = typeNode.ToString();
                    }
                }

                switch (type.ToLower())
                {
                    case "frontend":
                        result.Frontend = serviceName;
                        result.FrontendPort = port;
                        break;

                    case "backend":
                        result.Backend = serviceName;
                        result.BackendPort = port;
                        break;
                }
            }

            return result;
        }
        public static async Task<string[]> GetBuildServicesAsync(string composeFilePath)
        {
            var buildServices = new List<string>();

            if (!File.Exists(composeFilePath))
                return buildServices.ToArray();

            string yamlContent = await File.ReadAllTextAsync(composeFilePath);
            var yaml = new YamlStream();

            using (var reader = new StringReader(yamlContent))
            {
                yaml.Load(reader);
            }

            var root = (YamlMappingNode)yaml.Documents[0].RootNode;

            if (!root.Children.TryGetValue("services", out var servicesNode))
                return buildServices.ToArray();

            var services = (YamlMappingNode)servicesNode;

            foreach (var service in services.Children)
            {
                string serviceName = service.Key.ToString();
                var serviceConfig = (YamlMappingNode)service.Value;

                if (serviceConfig.Children.ContainsKey("build"))
                {
                    buildServices.Add(serviceName);
                }
            }

            return buildServices.ToArray();
        }
    }
}
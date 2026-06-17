using YamlDotNet.RepresentationModel;

namespace ChatOps.Services.FileService
{
    /// <summary>
    /// Class chuyên trách xử lý đọc, ghi và biến đổi nội dung các file cấu hình Docker Compose
    /// </summary>
    public static class DockerComposeFileTransformer
    {
        /// <summary>
        /// Biến đổi file cấu hình từ dạng Git (Development/Build tại chỗ) sang dạng Registry (Production/Kéo ảnh sẵn)
        /// </summary>
        /// <param name="sourceGitComposePath">Đường dẫn tuyệt đối tới file docker-git.yml gốc</param>
        /// <param name="destRegistryComposePath">Đường dẫn tuyệt đối nơi lưu file docker-registry.yml kết quả</param>
        /// <returns>Chuỗi "SUCCESS" nếu thành công, ngược lại trả về chuỗi thông báo lỗi chi tiết</returns>
        public static string TransformGitToRegistry(string sourceGitComposePath, string destRegistryComposePath)
        {
            try
            {
                if (!File.Exists(sourceGitComposePath))
                    return $"❌ Thất bại: Không tìm thấy file 'docker-git.yml' tại đường dẫn nguồn: {sourceGitComposePath}";

                var yaml = new YamlStream();

                using (var reader = new StringReader(File.ReadAllText(sourceGitComposePath)))
                {
                    yaml.Load(reader);
                }

                var root = (YamlMappingNode)yaml.Documents[0].RootNode;

                if (root.Children.TryGetValue("services", out var servicesNode))
                {
                    var services = (YamlMappingNode)servicesNode;

                    foreach (var service in services.Children)
                    {
                        var serviceConfig = (YamlMappingNode)service.Value;

                        // Xóa key build nếu tồn tại
                        serviceConfig.Children.Remove(new YamlScalarNode("build"));
                    }
                }

                using (var writer = new StringWriter())
                {
                    yaml.Save(writer, false);
                    File.WriteAllText(destRegistryComposePath, writer.ToString());
                }

                return "SUCCESS";
            }
            catch (Exception ex)
            {
                return $"❌ Thất bại do lỗi hệ thống khi xử lý File: {ex.Message}";
            }
        }
    }
}
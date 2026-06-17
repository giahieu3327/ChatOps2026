using ChatOps.Services.SystemService;

namespace ChatOps.Services.FileService
{
    public static class LoadBalancerFileService
    {
        /// <summary>
        /// Khởi tạo cấu trúc thư mục runtime cô lập cho ứng dụng và bản sao cấu hình mẫu
        /// </summary>
        public static async Task<string> PrepareRuntimeDirectory(string runtimeDir, string lbSubDir, string logDir, string projectPath, string templateAppFile, string destAppFile, string templateLbFile, string destLbFile)
        {
            // =====================================================
            // CHECK SOURCE FILE
            // =====================================================

            if (!File.Exists(templateAppFile))
            {
                return $"❌ Không tìm thấy file docker-compose.yml tại: {templateAppFile}";
            }

            // =====================================================
            // CREATE RUNTIME DIRECTORIES
            // =====================================================

            Directory.CreateDirectory(runtimeDir);
            Directory.CreateDirectory(lbSubDir);
            Directory.CreateDirectory(logDir);

            // =====================================================
            // COPY PROJECT SOURCE
            // =====================================================

            string copyCmd = $"cp -rf {projectPath.Trim()}/* {runtimeDir}/";
            await SystemCommandService.RunAsync(copyCmd);

            // Sao chép các tệp cấu hình mẫu sang thư mục runtime hoạt động
            File.Copy(templateAppFile, destAppFile, true);
            File.Copy(templateLbFile, destLbFile, true);

            return "Success";
        }

        /// <summary>
        /// Sinh tệp cấu hình Nginx động cho cấu trúc Load Balancer (Bất đồng bộ)
        /// </summary>
        public static async Task GenerateNginxConfigAsync(
            string lbSubDir,
            string frontend,
            string frontendPort,
            string backend,
            string backendPort)
        {
            string nginxConfig = $@"
        events {{
            worker_connections 1024;
            multi_accept on;
        }}

        http {{

            log_format main '$remote_addr - $remote_user [$time_local] ""$request"" $status $body_bytes_sent ""$http_referer"" ""$http_user_agent"" ""$http_x_forwarded_for""';

            access_log /runtime/access.log main;
            error_log  /runtime/error.log warn;

            sendfile on;
            tcp_nopush on;
            tcp_nodelay on;
            keepalive_timeout 65;
            client_max_body_size 100m;

            resolver 127.0.0.11 valid=5s;

            upstream web_pool {{
                server {frontend}:{frontendPort} max_fails=3 fail_timeout=10s;
            }}

            upstream backend_pool {{
                server {backend}:{backendPort} max_fails=3 fail_timeout=10s;
            }}

            server {{

                listen 80;

                proxy_http_version 1.1;

                proxy_set_header Host $host;
                proxy_set_header X-Real-IP $remote_addr;
                proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                proxy_set_header X-Forwarded-Proto $scheme;

                proxy_connect_timeout 5s;
                proxy_send_timeout 30s;
                proxy_read_timeout 30s;

                location / {{
                    proxy_pass http://web_pool;
                    proxy_next_upstream error timeout http_500 http_502 http_503 http_504;
                }}

                location /api/ {{
                    proxy_pass http://backend_pool;
                    proxy_next_upstream error timeout http_500 http_502 http_503 http_504;
                }}
            }}
        }}";

            string filePath = Path.Combine(lbSubDir, "nginx.conf");

            await File.WriteAllTextAsync(filePath, nginxConfig);
        }

        /// <summary>
        /// Sinh Dockerfile đóng gói riêng cho Nginx Load Balancer (Bất đồng bộ)
        /// </summary>
        public static async Task GenerateLbDockerfileAsync(string lbSubDir)
        {
            string dockerfile = "FROM nginx:alpine\n" +
                                "RUN mkdir -p /runtime && touch /runtime/access.log /runtime/error.log && chown -R nginx:nginx /runtime && chmod -R 755 /runtime\n" +
                                "COPY nginx.conf /etc/nginx/nginx.conf";
            
            string filePath = Path.Combine(lbSubDir, "Dockerfile");
            await File.WriteAllTextAsync(filePath, dockerfile);
        }

        /// <summary>
        /// Ghi tệp .env động (Bất đồng bộ)
        /// </summary>
        public static async Task WriteEnvFileAsync(string runtimeDir, string appName, string IMGDB, string IMGBACKEND, string IMGWEB, string IMGLB, string owner, string domain, List<int> extPort)
        {
            if (extPort == null || extPort.Count == 0)
            {
                throw new ArgumentException("Mảng extPort không được rỗng.", nameof(extPort));
            }
            int startPort = extPort[0];
            int endPort = extPort[extPort.Count - 1];

            var envContent = $@"
IMG_DB={IMGDB}
IMG_BACKEND={IMGBACKEND}
IMG_WEB={IMGWEB}
IMG_LB={IMGLB}
COMPOSE_PROJECT_NAME={appName}
OWNER={owner}
SERVICE={appName.Split('_')[0]}
DOMAIN={domain}
START_PORT={startPort}
END_PORT={endPort}
";

            string filePath = Path.Combine(runtimeDir, ".env");
            await File.WriteAllTextAsync(filePath, envContent);
        }
    }
}
namespace ChatOps.Data
{
    public static class ImageCategories
    {
        public static readonly Dictionary<string, (string Image, string Network, string InPort, string Type, string Env, string neededCount, string Path)> ImageServices = new() {
            { "nginx:latest", ("nginx:latest", "ChatOps-net", "80", "web", "", "1", "/usr/share/nginx/html/") },
            { "httpd:latest", ("httpd:latest", "ChatOps-net", "80", "web", "", "1", "/usr/local/apache2/htdocs/") },
            { "mysql:latest", ("mysql:latest", "ChatOps-net", "3306", "db", "-e MYSQL_ROOT_PASSWORD=123", "1", "") },
            { "postgres:latest", ("postgres:latest", "ChatOps-net", "5432", "db", "-e POSTGRES_PASSWORD=123", "1", "") },
            { "phpmyadmin:latest", ("phpmyadmin:latest", "ChatOps-net", "80", "tool", "", "1", "") },
            { "dpage/pgadmin4:latest", ("dpage/pgadmin4:latest", "ChatOps-net", "80", "tool", "-e PGADMIN_DEFAULT_EMAIL=admin@admin.com -e PGADMIN_DEFAULT_PASSWORD=123", "1", "") },
        };
    }
}
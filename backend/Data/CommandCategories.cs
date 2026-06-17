namespace ChatOps.Data
{
    public static class CommandCategories
    {
        // 1. USER & ACCOUNT
        public static readonly HashSet<string> UserAndAccount = new(StringComparer.OrdinalIgnoreCase)
        {
            "register", "whoami", "setupasswd", "setuname", 
            "setuemail", "seturole", "deluser", "users"
        };

        // 2. TRIỂN KHAI (DEPLOY)
        public static readonly HashSet<string> Deploy = new(StringComparer.OrdinalIgnoreCase)
        {
            "deploy", "attach-db", "detach-db", "deploy-git", 
            "release", "list-release", "unrelease", "deploy-compose"
        };

        // 3. CONTAINER MANAGEMENT
        public static readonly HashSet<string> ContainerManagement = new(StringComparer.OrdinalIgnoreCase)
        {
            "ps", "start", "stop", "kill", "restart", "rm", "rmi", 
            "setcname", "setcusername", "setcdomain", "inspect"
        };

        // 4. TRUY CẬP
        public static readonly HashSet<string> Access = new(StringComparer.OrdinalIgnoreCase)
        {
            "openweb", "opentool", "editweb"
        };

        // 5. DEBUG
        public static readonly HashSet<string> Debug = new(StringComparer.OrdinalIgnoreCase)
        {
            "logs"
        };

        // 6. BACKUP & ROLLBACK
        public static readonly HashSet<string> BackupAndRollback = new(StringComparer.OrdinalIgnoreCase)
        {
            "backup", "list-backup", "rollback", "delete-backup"
        };

        // 7. MONITORING
        public static readonly HashSet<string> Monitoring = new(StringComparer.OrdinalIgnoreCase)
        {
            "stats", "df", "health", "instances", "appservices", "imageservices"
        };

        // 8. DEVOPS
        public static readonly HashSet<string> DevOps = new(StringComparer.OrdinalIgnoreCase)
        {
            "scale", "set-alert", "rm-alert"
        };

        // 9. CLEANUP
        public static readonly HashSet<string> Cleanup = new(StringComparer.OrdinalIgnoreCase)
        {
            "prune"
        };

        // 10. HỖ TRỢ
        public static readonly HashSet<string> Support = new(StringComparer.OrdinalIgnoreCase)
        {
            "images", "ports", "nodes", "history", "clear", "help"
        };

        // Tập hợp tổng hợp tất cả các lệnh để Parser kiểm tra O(1)
        public static readonly HashSet<string> AllCommands = new(StringComparer.OrdinalIgnoreCase);

        static CommandCategories()
        {
            AllCommands.UnionWith(UserAndAccount);
            AllCommands.UnionWith(Deploy);
            AllCommands.UnionWith(ContainerManagement);
            AllCommands.UnionWith(Access);
            AllCommands.UnionWith(Debug);
            AllCommands.UnionWith(BackupAndRollback);
            AllCommands.UnionWith(Monitoring);
            AllCommands.UnionWith(DevOps);
            AllCommands.UnionWith(Cleanup);
            AllCommands.UnionWith(Support);
        }
    }
}
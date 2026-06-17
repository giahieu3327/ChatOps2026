
namespace ChatOps.Services.LogService
{
    public static class LogService
    {
        private static readonly object LockObj = new();
        private const string BaseAppLogPath = "/home/ubuntu/ChatOps/docker/Apps";

        private static string GetLogFolderPath(string app)
        {
            return Path.Combine(BaseAppLogPath, app, "logs");
        }

        public static void WriteAppLog(string app, string message)
        {
            lock (LockObj)
            {
                try
                {
                    string targetDirectory = GetLogFolderPath(app);
                    
                    if (!Directory.Exists(targetDirectory))
                    {
                        Directory.CreateDirectory(targetDirectory);
                    }

                    string fileName = $"{DateTime.Today:yyyy-MM-dd}.log";
                    string fullPath = Path.Combine(targetDirectory, fileName);
                    string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";

                    File.AppendAllText(fullPath, logLine);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error writing app log: {ex.Message}");
                }
            }
        }

        public static List<string> ReadAppLog(string app, int limit = 100, DateTime? date = null)
        {
            lock (LockObj)
            {
                try
                {
                    string targetDirectory = GetLogFolderPath(app);
                    DateTime targetDate = date ?? DateTime.Today;
                    string fileName = $"{targetDate:yyyy-MM-dd}.log";
                    string fullPath = Path.Combine(targetDirectory, fileName);

                    if (!File.Exists(fullPath))
                    {
                        return new List<string>();
                    }

                    return File.ReadLines(fullPath).TakeLast(limit).ToList();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading app log: {ex.Message}");
                    return new List<string>();
                }
            }
        }

        public static void ClearAppLogs(string app, DateTime? date = null)
        {
            lock (LockObj)
            {
                try
                {
                    string targetDirectory = GetLogFolderPath(app);
                    
                    if (!Directory.Exists(targetDirectory)) return;

                    if (date.HasValue)
                    {
                        string fileName = $"{date.Value:yyyy-MM-dd}.log";
                        string fullPath = Path.Combine(targetDirectory, fileName);
                        
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                        }
                    }
                    else
                    {
                        string parentDirectory = Path.Combine(BaseAppLogPath, app);
                        if (Directory.Exists(parentDirectory))
                        {
                            Directory.Delete(parentDirectory, true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error clearing app logs: {ex.Message}");
                }
            }
        }
    }
}
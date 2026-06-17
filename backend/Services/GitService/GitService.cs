using ChatOps.Services.SystemService;

namespace ChatOps.Services.GitService
{
    public static class GitService
    {
        public static async Task CleanDirectory(string path)
        {
            await SystemCommandService.RunAsync($"rm -rf {path}");
        }

        public static async Task<string> CloneRepository(string repoUrl, string targetPath)
        {
            return await SystemCommandService.RunAsync($"git clone {repoUrl} {targetPath} 2>&1");
        }
    }
}
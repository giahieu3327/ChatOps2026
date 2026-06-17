using System.Text;
using System.Text.RegularExpressions;
using ChatOps.Services.SystemService;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.DockerHubService
{
    public static class DockerHubService
    {
        public static async Task<string> GetTokenAsync()
        {
            using (var client = new HttpClient())
            {
                var loginUrl = "https://hub.docker.com/v2/users/login/";
                var loginContent = new StringContent($"{{\"username\": \"{AppContext.dockerUser}\", \"password\": \"{AppContext.dockerPass}\"}}", Encoding.UTF8, "application/json");

                var loginRes = await client.PostAsync(loginUrl, loginContent);
                string loginJson = await loginRes.Content.ReadAsStringAsync();
                string token = Regex.Match(loginJson, "\"token\":\"(.*?)\"").Groups[1].Value;
                return token;
            }
        }

        public static async Task<string> PushImageAsync(string appservice, string servicetype, string tag)
        {
            string imageName = $"{AppContext.dockerUser}/{appservice}-{servicetype}:{tag}";

            if (string.IsNullOrWhiteSpace(appservice) || string.IsNullOrWhiteSpace(servicetype) || string.IsNullOrWhiteSpace(tag))
            {
                return "❌ Thông tin định danh Docker Image để đẩy lên Registry không được để trống.";
            }

            return await Task.Run(async () => 
                await SystemCommandService.RunAsync($"docker push {imageName.Trim()} 2>&1")
            );
        }

        public static async Task<List<string>> GetImageAsync(string appservice, string servicetype)
        {
            using (var client = new HttpClient())
            {
                string token = await GetTokenAsync();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                string imgShort = $"{appservice}-{servicetype}";
                var listRes = await client.GetAsync($"https://hub.docker.com/v2/repositories/{AppContext.dockerUser}/{imgShort}/tags/?page_size=100");
                
                if (!listRes.IsSuccessStatusCode) return new List<string>();

                string listJson = await listRes.Content.ReadAsStringAsync();
                var matches = Regex.Matches(listJson, "\"name\":\"(.*?)\"");
                
                return matches.Cast<Match>()
                    .Select(m => m.Groups[1].Value)
                    .Where(t => t != "latest" && t != "trial")
                    .ToList();
            }
        }

        public static async Task<bool> DeleteImageAsync(string appservice, string servicetype, string tag)
        {
            using (var client = new HttpClient())
            {
                string token = await GetTokenAsync();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                string imgShort = $"{appservice}-{servicetype}";
                var res = await client.DeleteAsync($"https://hub.docker.com/v2/repositories/{AppContext.dockerUser}/{imgShort}/tags/{tag}/");
                
                return res.IsSuccessStatusCode;
            }
        }

        public static async Task<bool> IsTagExistAsync(string appservice, string servicetype, string tag)
        {
            using (var client = new HttpClient())
            {
                string token = await GetTokenAsync();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                string imgShort = $"{appservice}-{servicetype}";
                // Gọi thẳng tới endpoint cụ thể của tag để check nhanh qua Status Code
                var res = await client.GetAsync($"https://hub.docker.com/v2/repositories/{AppContext.dockerUser}/{imgShort}/tags/{tag}/");
                
                return res.IsSuccessStatusCode;
            }
        }
    }
}
namespace ChatOps.Models
{
    public class UserSession
    {
        public string Username { get; set; } = string.Empty;
        public string Role { get; set; } = "user";
        public bool Debug { get; set; } = true;
    }
}
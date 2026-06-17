namespace ChatOps.Models
{
    public class DockerCommandPayload
    {
        public string action {get; set; } = "";
        public Dictionary<string, string> parsed { get; set; } = new Dictionary<string, string>();
        public UserSession Session { get; set; } = new UserSession();
        public string connectionId {get; set; } = "";
    }
}
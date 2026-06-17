public class ServiceConfig {
    public string Domain { get; set; } = "";
    public List<string> Ports { get; set; } = new(); 
    public bool IsApp { get; set; } // True: App/LB, False: Single Container
    public string AppService { get; set; } = "";
    public string ContainerName { get; set; } = ""; // Cần để định danh khi cần Inspect
}
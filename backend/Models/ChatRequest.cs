namespace ChatOps.Models
{
    public class ChatRequest
    {
        public string Command { get; set; } = "";
        
        public string ConnectionId { get; set; } = string.Empty; // Để định danh tab chat của từng user

        /// <summary>
        /// Trạng thái Debug nhận từ Client (true: hiện tiến trình, false: chỉ hiện kết quả)
        /// </summary>
        public bool Debug { get; set; } = true; // Mặc định là true nếu client không truyền
    }
}
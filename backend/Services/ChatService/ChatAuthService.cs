using ChatOps.Data;
using AppContext = ChatOps.Data.AppContext;
using System.Net.Mail;
using System.Text.RegularExpressions;
using System.Text;
using ChatOps.Models;
using ChatOps.Services.RedisService;

namespace ChatOps.Services.ChatService
{
    public static class ChatAuthService
    {
        private static async Task SendLogWithDelayAsync(bool debug, string connectionId, string message)
        {
            await Task.Delay(100);
            await RedisChannelService.SendMessageToClientAsync(debug, connectionId, message);
        }

        public static async Task<string> Register(Dictionary<string, string> parsed, UserSession session, AppDbContext _db, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang trích xuất tham số lệnh...");

            string username = parsed.GetValueOrDefault("username", "").Trim();
            string password = parsed.GetValueOrDefault("password", "").Trim();
            string email = parsed.GetValueOrDefault("email", "").Trim();
            string role = parsed.GetValueOrDefault("role", "user").Trim().ToLower();

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return "❌ Thiếu tham số! Username và Password là bắt buộc.";

            if (!Regex.IsMatch(username, "^[a-zA-Z0-9]+$"))
                return "❌ Username chỉ được chứa chữ cái (hoa, thường) và số.";

            if (!Regex.IsMatch(password, "^[a-zA-Z0-9]+$"))
                return "❌ Mật khẩu chỉ được chứa chữ cái (hoa, thường) và số.";

            if (!string.IsNullOrWhiteSpace(email))
            {
                try
                {
                    if (new MailAddress(email).Address != email) throw new Exception();
                }
                catch
                {
                    return "❌ Định dạng Email không hợp lệ.";
                }
            }

            var validRoles = new[] { "admin", "manager", "dev", "ops", "user" };
            if (!validRoles.Contains(role))
                return "❌ Role mục tiêu không hợp lệ.";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Kiểm tra trùng lặp và phân quyền dữ liệu trên Database...");

            if (_db.Users.Any(x => x.Username.ToLower() == username.ToLower()))
                return $"❌ Username '{username}' đã tồn tại trên hệ thống.";

            if (!string.IsNullOrWhiteSpace(email) && _db.Users.Any(x => !string.IsNullOrEmpty(x.Email) && x.Email.ToLower() == email.ToLower()))
                return "❌ Email này đã được đăng ký trên hệ thống.";

            if (!session.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
            {
                if (session.Role != "admin" && session.Role != "manager")
                    return "❌ Bạn không có quyền thực hiện chức năng đăng ký thành viên.";

                if (session.Role == "admin" && role == "admin")
                    return "❌ Quyền [ADMIN] của bạn không thể cấp role [ADMIN].";

                if (session.Role == "manager" && (role == "admin" || role == "manager"))
                    return $"❌ Quyền [MANAGER] của bạn không thể cấp role [{role.ToUpper()}].";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"⚙️ [Node {AppContext.ServerID}] Mã hóa dữ liệu mật khẩu và lưu trữ vào Database...");

            string hashedPassword = BCrypt.Net.BCrypt.HashPassword(password);

            var newUser = new User
            {
                Email = email,
                Username = username,
                PasswordHash = hashedPassword,
                Role = role
            };

            _db.Users.Add(newUser);
            _db.SaveChanges();

            return $"✅ Tạo user thành công!\n👤 Username: {username}\n📧 Email: {(string.IsNullOrWhiteSpace(email) ? "(Trống)" : email)}\n🛡 Role: {role.ToUpper()}";
        }

        public static async Task<string> GetCurrentUser(UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang trích xuất thông tin tài khoản...");
            return $"👤 Bạn là: {session.Username}\n🛡 Quyền hạn: {session.Role.ToUpper()}";
        }

        public static async Task<string> ChangePasswd(Dictionary<string, string> parsed, UserSession session, AppDbContext _db, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang trích xuất tham số lệnh...");

            string oldPass = parsed.GetValueOrDefault("oldpass", "").Trim();
            string targetUsername = parsed.GetValueOrDefault("username", "").Trim();
            string newPass = parsed.GetValueOrDefault("newpass", "").Trim();

            bool hasOldPass = !string.IsNullOrWhiteSpace(oldPass);
            bool hasUsername = !string.IsNullOrWhiteSpace(targetUsername);

            if (hasOldPass && hasUsername)
                return "❌ Sai cấu trúc! Tự đổi pass chỉ nhập 'oldpass', đổi hộ chỉ nhập 'username'. Không được nhập cả hai.";

            if (!hasOldPass && !hasUsername)
                return "❌ Thiếu tham số! Vui lòng nhập 'oldpass' (tự đổi) hoặc 'username' (đổi hộ).";

            if (string.IsNullOrWhiteSpace(newPass))
                return "❌ Mật khẩu mới ('newpass') là bắt buộc.";

            if (!Regex.IsMatch(newPass, "^[a-zA-Z0-9]+$"))
                return "❌ Mật khẩu mới chỉ được chứa chữ cái (hoa, thường) và số.";

            if (hasOldPass)
            {
                if (!Regex.IsMatch(oldPass, "^[a-zA-Z0-9]+$"))
                    return "❌ Mật khẩu cũ chỉ được chứa chữ cái (hoa, thường) và số.";
            }
            else
            {
                if (!Regex.IsMatch(targetUsername, "^[a-zA-Z0-9]+$"))
                    return "❌ Username mục tiêu chỉ được chứa chữ cái (hoa, thường) và số.";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang kiểm tra logic tài khoản trên database...");

            User? userToUpdate = null;
            bool isChangeForOther = hasUsername;

            if (isChangeForOther)
            {
                userToUpdate = _db.Users.FirstOrDefault(x => x.Username.ToLower() == targetUsername.ToLower());
                if (userToUpdate == null)
                    return $"❌ Không tìm thấy user '{targetUsername}'.";
            }
            else
            {
                userToUpdate = _db.Users.FirstOrDefault(x => x.Username.ToLower() == session.Username.ToLower());
                if (userToUpdate == null)
                    return "❌ Không tìm thấy thông tin tài khoản của bạn.";

                if (!BCrypt.Net.BCrypt.Verify(oldPass, userToUpdate.PasswordHash))
                    return "❌ Mật khẩu cũ không chính xác.";

                if (oldPass == newPass)
                    return "❌ Mật khẩu mới không được trùng với mật khẩu cũ.";
            }

            if (isChangeForOther)
            {
                if (session.Role != "admin" && session.Role != "manager")
                    return "❌ Bạn không có quyền thay đổi mật khẩu của người khác.";

                if (!session.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
                {
                    if (userToUpdate.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
                        return "❌ Bạn không thể đổi mật khẩu của tài khoản 'admin' gốc.";

                    if (session.Role == "admin" && userToUpdate.Role == "admin")
                        return "❌ Quyền [ADMIN] của bạn không thể đổi mật khẩu cho tài khoản [ADMIN] khác.";

                    if (session.Role == "manager" && (userToUpdate.Role == "admin" || userToUpdate.Role == "manager"))
                        return $"❌ Quyền [MANAGER] của bạn không thể đổi mật khẩu cho tài khoản có role [{userToUpdate.Role.ToUpper()}].";
                }
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"⚙️ [Node {AppContext.ServerID}] Xác thực hợp lệ. Tiến hành mã hóa mật khẩu mới và lưu database...");

            string hashedPassword = BCrypt.Net.BCrypt.HashPassword(newPass);
            userToUpdate.PasswordHash = hashedPassword;
            _db.SaveChanges();

            return isChangeForOther
                ? $"✅ [{session.Role.ToUpper()}] Đã đặt lại mật khẩu thành công cho user: {userToUpdate.Username}"
                : "✅ Bạn đã tự thay đổi mật khẩu thành công!";
        }

        public static async Task<string> ChangeUsername(Dictionary<string, string> parsed, UserSession session, AppDbContext _db, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang trích xuất tham số lệnh...");

            string targetUsername = parsed.GetValueOrDefault("username", "").Trim();
            string newUsername = parsed.GetValueOrDefault("newusername", "").Trim();

            if (string.IsNullOrWhiteSpace(targetUsername) || string.IsNullOrWhiteSpace(newUsername))
                return "❌ Thiếu tham số! Vui lòng nhập đầy đủ cả hai tham số 'username' và 'newusername'.";

            if (!Regex.IsMatch(targetUsername, "^[a-zA-Z0-9]+$"))
                return "❌ Username mục tiêu chỉ được chứa chữ cái và số.";

            if (!Regex.IsMatch(newUsername, "^[a-zA-Z0-9]+$"))
                return "❌ Username mới chỉ được chứa chữ cái và số.";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang đối chiếu tài khoản và kiểm tra trùng lặp trên database...");

            var targetUser = _db.Users.FirstOrDefault(x => x.Username.ToLower() == targetUsername.ToLower());
            if (targetUser == null)
                return $"❌ Không tìm thấy user '{targetUsername}' trên hệ thống.";

            if (targetUser.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
                return "❌ Tài khoản 'admin' gốc là cố định, không được phép thay đổi Username.";

            if (targetUser.Username.Equals(newUsername, StringComparison.OrdinalIgnoreCase))
                return "❌ Tên mới trùng với Username hiện tại của tài khoản này.";

            if (_db.Users.Any(x => x.Username.ToLower() == newUsername.ToLower()))
                return $"❌ Username '{newUsername}' đã tồn tại trên hệ thống.";

            if (!session.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
            {
                if (session.Role != "admin" && session.Role != "manager")
                    return "❌ Bạn không có quyền sử dụng lệnh này.";

                if (session.Role == "admin" && targetUser.Role == "admin")
                    return "❌ Quyền [ADMIN] của bạn không thể thay đổi username cho tài khoản [ADMIN] khác.";

                if (session.Role == "manager" && (targetUser.Role == "admin" || targetUser.Role == "manager"))
                    return $"❌ Quyền [MANAGER] của bạn không thể thay đổi username cho tài khoản có role [{targetUser.Role.ToUpper()}].";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"⚙️ [Node {AppContext.ServerID}] Đang đồng bộ hóa dữ liệu và tái cấu trúc vùng nhớ Redis Cluster...");

            string oldUsername = targetUser.Username;
            targetUser.Username = newUsername;
            _db.SaveChanges();

            await RedisHistoryService.UpdateHistoryKeyAsync(oldUsername, newUsername);
            await RedisUserSessionService.UpdateUserSessionKeyAsync(oldUsername, newUsername);

            return $"✅ [{session.Role.ToUpper()}] Thay đổi Username thành công\n👤 Tên cũ: {oldUsername}\n🚀 Tên mới: {newUsername}";
        }

        public static async Task<string> ChangeEmail(Dictionary<string, string> parsed, UserSession session, AppDbContext _db, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang trích xuất tham số lệnh...");

            string targetUsername = parsed.GetValueOrDefault("username", "").Trim();
            string newEmail = parsed.GetValueOrDefault("newemail", "").Trim();

            if (string.IsNullOrWhiteSpace(targetUsername) || string.IsNullOrWhiteSpace(newEmail))
                return "❌ Thiếu tham số! Vui lòng nhập đầy đủ cả hai tham số 'username' và 'newemail'.";

            if (!Regex.IsMatch(targetUsername, "^[a-zA-Z0-9]+$"))
                return "❌ Username mục tiêu chỉ được chứa chữ cái và số.";

            try
            {
                var mailAddress = new MailAddress(newEmail);
                if (mailAddress.Address != newEmail) throw new Exception();
            }
            catch
            {
                return "❌ Định dạng Email mới không hợp lệ.";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang xác thực thông tin tài khoản và kiểm tra trùng lặp Email...");

            var targetUser = _db.Users.FirstOrDefault(x => x.Username.ToLower() == targetUsername.ToLower());
            if (targetUser == null)
                return $"❌ Không tìm thấy user '{targetUsername}' trên hệ thống.";

            if (!string.IsNullOrWhiteSpace(targetUser.Email) && targetUser.Email.Equals(newEmail, StringComparison.OrdinalIgnoreCase))
                return "❌ Email mới trùng khớp with Email hiện tại của người dùng này.";

            if (_db.Users.Any(x => !string.IsNullOrEmpty(x.Email) && x.Email.ToLower() == newEmail.ToLower()))
                return "❌ Email này đã tồn tại và đang được sử dụng bởi một tài khoản khác.";

            if (!session.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
            {
                if (session.Role != "admin" && session.Role != "manager")
                    return "❌ Bạn không có quyền sử dụng lệnh này.";

                if (session.Role == "admin" && targetUser.Role == "admin")
                    return "❌ Quyền [ADMIN] của bạn không thể thay đổi thông tin cho tài khoản [ADMIN] khác.";

                if (session.Role == "manager" && (targetUser.Role == "admin" || targetUser.Role == "manager"))
                    return $"❌ Quyền [MANAGER] của bạn không thể thay đổi thông tin cho tài khoản có role [{targetUser.Role.ToUpper()}].";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"⚙️ [Node {AppContext.ServerID}] Đang thực thi ghi nhận thay đổi Email vào Database...");

            string oldEmail = string.IsNullOrWhiteSpace(targetUser.Email) ? "(Trống)" : targetUser.Email;
            targetUser.Email = newEmail;
            _db.SaveChanges();

            return $"✅ [{session.Role.ToUpper()}] Thay đổi Email thành công\n👤 Tài khoản: {targetUser.Username}\n📧 Email cũ: {oldEmail}\n🚀 Email mới: {newEmail}";
        }

        public static async Task<string> SetRole(Dictionary<string, string> parsed, UserSession session, AppDbContext _db, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang trích xuất tham số lệnh...");

            string targetUsername = parsed.GetValueOrDefault("username", "").Trim();
            string newRole = parsed.GetValueOrDefault("newrole", "").Trim().ToLower();

            if (string.IsNullOrWhiteSpace(targetUsername) || string.IsNullOrWhiteSpace(newRole))
                return "❌ Thiếu tham số! Vui lòng nhập đầy đủ cả hai tham số 'username' và 'newrole'.";

            if (!Regex.IsMatch(targetUsername, "^[a-zA-Z0-9]+$"))
                return "❌ Username mục tiêu chỉ được chứa chữ cái và số.";

            var validRoles = new[] { "admin", "manager", "dev", "ops", "user" };
            if (!validRoles.Contains(newRole))
                return "❌ Role mục tiêu không hợp lệ.";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang truy vấn và kiểm tra quyền hạn thực thi trên database...");

            var targetUser = _db.Users.FirstOrDefault(x => x.Username.ToLower() == targetUsername.ToLower());
            if (targetUser == null)
                return $"❌ Không tìm thấy user '{targetUsername}' trên hệ thống.";

            if (targetUser.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
                return "❌ Tài khoản 'admin' gốc là cố định, không được phép thay đổi Role.";

            if (session.Username.Equals(targetUser.Username, StringComparison.OrdinalIgnoreCase))
                return "❌ Bạn không thể tự thay đổi hoặc hạ thấp quyền hạn của chính mình.";

            if (targetUser.Role == newRole)
                return $"ℹ️ Người dùng '{targetUser.Username}' hiện tại đã có quyền là '{newRole.ToUpper()}' rồi.";

            if (!session.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
            {
                if (session.Role != "admin" && session.Role != "manager")
                    return "❌ Bạn không có quyền thay đổi role của người khác.";

                if (session.Role == "admin" && (targetUser.Role == "admin" || newRole == "admin"))
                    return "❌ Quyền [ADMIN] (tài khoản phụ) của bạn không thể cấp role [ADMIN] hoặc thao tác trên tài khoản [ADMIN] khác.";

                if (session.Role == "manager" && (targetUser.Role == "admin" || targetUser.Role == "manager" || newRole == "admin" || newRole == "manager"))
                    return $"❌ Quyền [MANAGER] của bạn không thể cấp hoặc thao tác trên các role cao hơn hoặc bằng [{session.Role.ToUpper()}].";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"⚙️ [Node {AppContext.ServerID}] Cập nhật cấu trúc phân quyền mới vào hệ thống...");

            string oldRole = targetUser.Role;
            targetUser.Role = newRole;
            _db.SaveChanges();

            return $"✅ [{session.Role.ToUpper()}] Thay đổi Role thành công\n👤 Tài khoản: {targetUser.Username}\n🛡 Role cũ: {oldRole.ToUpper()}\n🚀 Role mới: {newRole.ToUpper()}";
        }

        public static async Task<string> DelUser(Dictionary<string, string> parsed, UserSession session, AppDbContext _db, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang trích xuất tham số lệnh...");

            string usernameToDelete = parsed.GetValueOrDefault("username", "").Trim();

            if (string.IsNullOrWhiteSpace(usernameToDelete))
                return "❌ Thiếu tham số! Vui lòng nhập tham số 'username'.";

            if (!Regex.IsMatch(usernameToDelete, "^[a-zA-Z0-9]+$"))
                return "❌ Username mục tiêu chỉ được chứa chữ cái và số.";

            if (usernameToDelete.Equals(session.Username, StringComparison.OrdinalIgnoreCase))
                return "❌ Bạn không thể tự xóa tài khoản của chính mình.";

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang đối chiếu thông tin tài khoản cần xóa...");

            var targetAccount = _db.Users.FirstOrDefault(x => x.Username.ToLower() == usernameToDelete.ToLower());
            if (targetAccount == null)
                return $"❌ Không tìm thấy user '{usernameToDelete}' trên hệ thống.";

            if (targetAccount.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
                return "❌ Tài khoản 'admin' gốc là cố định, không được phép xóa.";

            if (!session.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
            {
                if (session.Role != "admin" && session.Role != "manager")
                    return "❌ Bạn không có quyền sử dụng lệnh này.";

                if (session.Role == "admin" && targetAccount.Role == "admin")
                    return "❌ Quyền [ADMIN] (tài khoản phụ) của bạn không thể xóa tài khoản [ADMIN] khác.";

                if (session.Role == "manager" && (targetAccount.Role == "admin" || targetAccount.Role == "manager"))
                    return $"❌ Quyền [MANAGER] của bạn không thể xóa tài khoản có role [{targetAccount.Role.ToUpper()}].";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Tiến hành xóa vĩnh viễn dữ liệu trên Database và giải phóng Redis Cluster...");

            string deletedUsername = targetAccount.Username;
            string deletedRole = targetAccount.Role;

            _db.Users.Remove(targetAccount);
            _db.SaveChanges();

            await RedisUserSessionService.DeleteUserSessionAsync(deletedUsername);
            await RedisHistoryService.DeleteHistoryAsync(deletedUsername);

            return $"✅ [{session.Role.ToUpper()}] Đã xóa vĩnh viễn tài khoản thành công\n👤 Tài khoản bị xóa: {deletedUsername}\n🛡 Quyền hạn cũ: {deletedRole.ToUpper()}";
        }

        public static async Task<string> GetUsers(UserSession session, AppDbContext _db, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi tạo tiến trình và xác thực quyền truy vấn thông tin...");

            if (!session.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
            {
                if (session.Role != "admin" && session.Role != "manager")
                    return "❌ Bạn không có quyền xem danh sách người dùng trên hệ thống.";
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang kết xuất cấu trúc danh sách người dùng từ Database...");

            var allUsers = _db.Users
                .Select(u => new { u.Id, u.Username, u.Email, u.Role })
                .OrderBy(x => x.Id)
                .ToList();

            if (!allUsers.Any())
            {
                return "ℹ️ Hệ thống hiện tại chưa có tài khoản nào.";
            }

            var sb = new StringBuilder();
            sb.AppendLine("📊 DANH SÁCH NGƯỜI DÙNG HỆ THỐNG");
            sb.AppendLine("-----------------------------------------------------------------------------------------");
            sb.AppendLine(string.Format("{0,-6} | {1,-20} | {2,-35} | {3,-10}", "ID", "Username", "Email", "Role"));
            sb.AppendLine("-----------------------------------------------------------------------------------------");

            foreach (var u in allUsers)
            {
                string displayEmail = string.IsNullOrWhiteSpace(u.Email) ? "(Trống)" : u.Email;
                sb.AppendLine(string.Format("{0,-6} | {1,-20} | {2,-35} | {3,-10}", u.Id, u.Username, displayEmail, u.Role.ToUpper()));
            }

            sb.AppendLine("-----------------------------------------------------------------------------------------");
            sb.Append($"Tổng cộng: {allUsers.Count} tài khoản.");

            return sb.ToString();
        }
    }
}
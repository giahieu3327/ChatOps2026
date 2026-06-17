using ChatOps.Controllers;
using AppContext = ChatOps.Data.AppContext;
using ChatOps.Models;
using ChatOps.Services.RedisService;
using ChatOps.Services.SystemService;

namespace ChatOps.Services.ChatService
{
    public static class ChatSupportService
    {
        private static async Task SendLogWithDelayAsync(bool debug, string connectionId, string message)
        {
            await Task.Delay(100);
            await RedisChannelService.SendMessageToClientAsync(debug, connectionId, message);
        }

        public static async Task<string> ListLocalImages(UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] Đang truy vấn danh sách Docker Images cục bộ...");

            string imagesResult = await SystemCommandService.RunAsync("docker images --format \"table {{.Repository}}\\t{{.Tag}}\\t{{.ID}}\\t{{.Size}}\"");

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Hoàn tất nạp danh sách images.");

            if (string.IsNullOrWhiteSpace(imagesResult) || imagesResult.Contains("template parsing error"))
                return $"❌ [Node {AppContext.ServerID}] Không lấy được danh sách Docker Image hoặc có lỗi xảy ra.";

            return $"🖼️ DANH SÁCH DOCKER IMAGES [Local Node: {AppContext.ServerID}]\n" +
                $"--------------------------------------------------\n" +
                $"{imagesResult}";
        }

        public static async Task<string> ListOccupiedPorts(UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang thực hiện quét trạng thái Socket mạng (ss -ltn)...");

            string script = "ss -ltn | grep LISTEN | awk '{print $4}' | awk -F':' '{print $NF}' | sort -nu";
            string output = await SystemCommandService.RunAsync(script);

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Hoàn tất phân tích Socket ports.");

            if (string.IsNullOrWhiteSpace(output))
                return "ℹ️ Info: Không có port TCP nào đang mở ở trạng thái LISTEN.";

            string header = $"📍 DANH SÁCH PORT ĐANG MỞ [Local Node: {AppContext.ServerID}]\n" +
                            $"--------------------------------------------------\n";

            var ports = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => $"   ✅ Port: {p.Trim()}");

            return header + string.Join("\n", ports) + "\n--------------------------------------------------";
        }

        public static async Task<string> ListNodes(UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] Đang kết nối Redis Node Registry để quét danh sách Cluster Nodes...");

            (bool success, Dictionary<string, string> nodes) = await RedisNodeService.GetNodeAsync(isGetAll: true);

            if (!success || nodes == null || nodes.Count == 0)
            {
                return "⚠️ Hệ thống không phát hiện thấy Node nào đang hoạt động trong Registry.";
            }

            string result = "🌐 DANH SÁCH MẠNG LƯỚI HẠ TẦNG NODES (CLUSTER)\n" +
                            "STT | NODE IDENTITY (ID)     | LOCAL IP ADDRESS   | STATUS  \n" +
                            "-----------------------------------------------------------------\n";

            int index = 1;
            foreach (var node in nodes)
            {
                string nodeIp = node.Key;
                string nodeId = node.Value;
                string status = "🟢 ONLINE";

                result += $"{index,-3} | {nodeId,-22} | {nodeIp,-18} | {status}\n";
                index++;
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Xuất thông tin cấu trúc Cluster hoàn tất.");
            return result;
        }

        public static async Task<string> ClearHistory(UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🐳 [Node {AppContext.ServerID}] Đang tiến hành xóa lịch sử ChatOps của người dùng '{session.Username}' trên Redis...");

            (bool success, string message) result = await RedisHistoryService.DeleteHistoryAsync(session.Username);

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Tác vụ xử lý lịch sử hoàn tất. Kết quả: {(result.success ? "Thành công" : "Thất bại")}.");

            return result.message;
        }

        public static async Task<string> GetCommandHistory(UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang nạp lịch sử tương tác ChatOps của user '{session.Username}' từ Redis...");

            (bool success, Dictionary<string, string> historyData) = await RedisHistoryService.GetHistoryAsync(username: session.Username, isGetAll: false);

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Đồng bộ lịch sử hoàn tất.");

            if (!success || historyData == null || historyData.Count == 0)
            {
                return $"📜 Lịch sử lệnh của user [{session.Username}] hiện tại trống hoặc xảy ra lỗi truy vấn.";
            }

            if (!historyData.TryGetValue(session.Username, out string? rawHistory) || string.IsNullOrWhiteSpace(rawHistory))
            {
                return $"📜 Lịch sử lệnh của user [{session.Username}] hiện tại trống.";
            }

            string header = $"📜 LỊCH SỬ LỆNH ĐÃ THỰC THI ({session.Username})\n" +
                            $"--------------------------------------------------\n";

            int lineNum = 1;
            var lines = rawHistory.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                 .Select(cmd => $"{lineNum++}. {cmd.Trim()}");

            return header + string.Join("\n", lines);
        }

        public static async Task<string> GetHelpQuick(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Đang xử lý yêu cầu trợ giúp hệ thống...");

            if (parsed.TryGetValue("command", out var commandTarget) && !string.IsNullOrWhiteSpace(commandTarget))
            {
                string cleanAction = commandTarget.Trim().ToLower();

                await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang truy xuất tài liệu hướng dẫn chi tiết cho lệnh '{cleanAction}'...");

                string detailHelp = await GetHelp(cleanAction, session.Role);

                await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Hoàn tất trích xuất tài liệu chi tiết.");
                return detailHelp;
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Node {AppContext.ServerID}] Đang nạp menu tổng quan ChatOps cho vai trò '{session.Role}'...");

            string menuHelp = Help(session.Role);

            await SendLogWithDelayAsync(session.Debug, connectionId, $"➡️ [Node {AppContext.ServerID}] Xuất menu tổng quan thành công.");
            return menuHelp;
        }

        public static async Task<string> GetHelp(string cleanAction, string role)
        {
            if (!await ChatController.HasPermission(cleanAction, role))
            {
                return "❌ Permission denied";
            }

            return cleanAction switch
            {
                "whoami" or "setupasswd" or "register" or "setuname" or "setuemail" or "seturole" or "deluser" or "users"
                    => GetHelpUser(cleanAction, role),

                "deploy" or "attach-db" or "detach-db" or "deploy-git" or "release" or "list-release" or "unrelease" or "deploy-compose"
                    => GetHelpDeploy(cleanAction),

                "ps" or "start" or "stop" or "kill" or "restart" or "rm" or "rmi" or "setcname" or "setcusername" or "setcdomain" or "inspect"
                    => GetHelpContainer(cleanAction),

                "openweb" or "opentool" or "editweb"
                    => GetHelpAccess(cleanAction),

                "logs"
                    => GetHelpDebug(cleanAction),

                "backup" or "list-backup" or "rollback" or "delete-backup"
                    => GetHelpBackup(cleanAction),

                "stats" or "df" or "health" or "instances" or "appservices" or "imageservices"
                    => GetHelpMonitoring(cleanAction),

                "scale" or "set-alert" or "rm-alert"
                    => GetHelpDevOps(cleanAction),

                "prune"
                    => GetHelpCleanup(cleanAction),

                "images" or "ports" or "nodes" or "history" or "clear" or "help"
                    => GetHelpSupport(cleanAction),

                _ => $"❌ Không có dữ liệu hướng dẫn chi tiết cho câu lệnh: {cleanAction}"
            };
        }
        private static string GetHelpUser(string action, string role)
        {
            string userRole = role.Trim().ToLower();

            return action switch
            {
                "whoami" => @"🔐 WHOAMI
Tác dụng: Xem thông tin chi tiết tài khoản và vai trò hiện tại của phiên làm việc ChatOps.

Cấu trúc cú pháp hỗ trợ:
  • whoami : Trích xuất thông tin định danh cá nhân trực tiếp.

Cú pháp đầy đủ: whoami",

                "setupasswd" => (userRole == "admin" || userRole == "manager")
                    ? @"🔐 SETUPASSWD
Tác dụng: Quản lý và thay đổi mật khẩu xác thực trên hệ thống cho cá nhân hoặc người dùng khác.

Cấu trúc cú pháp hỗ trợ:
  • setupasswd oldpass [oldpass] newpass [newpass] : Tự thay đổi mật khẩu cho tài khoản cá nhân hiện tại.
  • setupasswd username [username] newpass [newpass] : Đổi mật khẩu cho một tài khoản người dùng khác trong trường hợp họ quên mật khẩu (Đặc quyền Admin/Manager).

Cú pháp đầy đủ: setupasswd oldpass [oldpass] username [username] newpass [newpass]"
                    : @"🔐 SETUPASSWD
Tác dụng: Tự thay đổi mật khẩu xác thực của tài khoản cá nhân hiện tại.

Cấu trúc cú pháp hỗ trợ:
  • setupasswd oldpass [oldpass] newpass [newpass] : Cập nhật mật khẩu mới thông qua việc xác thực mật khẩu cũ.

Cú pháp đầy đủ: setupasswd oldpass [oldpass] newpass [newpass]",

                "register" => @"🔐 REGISTER
Tác dụng: Khởi tạo một tài khoản vận hành mới trên hệ thống ChatOps.

Cấu trúc cú pháp hỗ trợ:
  • register email [email] role [role] username [username] password [password] : Cấp tài khoản mới với vai trò được định nghĩa rõ ràng.

Cú pháp đầy đủ: register email [email] role [role] username [username] password [password]",

                "setuname" => @"🔐 SETUNAME
Tác dụng: Cập nhật lại tên đăng nhập (Username) định danh cho một tài khoản.

Cấu trúc cú pháp hỗ trợ:
  • setuname username [username] newusername [newusername] : Tìm kiếm theo Username cũ và ghi đè bằng tên mới.

Cú pháp đầy đủ: setuname username [username] newusername [newusername]",

                "setuemail" => @"🔐 SETUEMAIL
Tác dụng: Thay đổi hoặc cập nhật địa chỉ Email liên kết của tài khoản được chỉ định.

Cấu trúc cú pháp hỗ trợ:
  • setuemail username [username] newemail [newemail] : Cấu hình Email mới nhận thông báo cho người dùng.

Cú pháp đầy đủ: setuemail username [username] newemail [newemail]",

                "seturole" => @"🔐 SETROLE
Tác dụng: Thay đổi vai trò phân quyền (Role) nhằm điều chỉnh hạn mức lệnh của tài khoản.

Cấu trúc cú pháp hỗ trợ:
  • seturole username [username] newrole [newrole] : Chuyển đổi quyền hạn của User sang phân hệ mới (admin, manager, dev, ops, user).

Cú pháp đầy đủ: seturole username [username] newrole [newrole]",

                "deluser" => @"🔐 DELUSER
Tác dụng: Gỡ bỏ hoàn toàn và xóa vĩnh viễn một tài khoản ra khỏi hệ thống cơ sở dữ liệu.

Cấu trúc cú pháp hỗ trợ:
  • deluser username [username] : Trục xuất tài khoản chỉ định ra khỏi danh sách vận hành.

Cú pháp đầy đủ: deluser username [username]",

                "users" => @"🔐 USERS
Tác dụng: Liệt kê danh sách tổng hợp tất cả các tài khoản đang tồn tại trên hệ thống.

Cấu trúc cú pháp hỗ trợ:
  • users : Xem toàn bộ thông tin các user cùng trạng thái phân quyền kèm theo.

Cú pháp đầy đủ: users",

                _ => string.Empty
            };
        }
        private static string GetHelpDeploy(string action)
        {
            string nodeNote = "\n\nCơ chế điều phối Node (Tham số mở rộng):\n" +
                              "  • Mặc định (Không truyền node): Thực thi triển khai trực tiếp tại Node hiện tại.\n" +
                              "  • node [nodeid] : Hệ thống tự động chuyển tiếp gói tin và ra lệnh deploy trên Node mục tiêu.";

            return action switch
            {
                "deploy" => @"🚀 DEPLOY
Tác dụng: Khởi chạy độc lập một container ứng dụng mới từ Docker Image có sẵn trên hệ thống.

Cấu trúc cú pháp hỗ trợ:
  • deploy image [imagename] : Khởi chạy nhanh container từ image với cấu hình mặc định.
  • deploy image [imagename] port [portout] name [containername] db [dbcontainer] username [username] domain [domain] : Khởi chạy container đính kèm đầy đủ tham số cổng, tên định danh, kết nối DB, chủ sở hữu và cấu hình routing domain." + nodeNote + @"

Cú pháp đầy đủ: deploy image [imagename] port [portout] name [containername] db [dbcontainer] username [username] domain [domain] node [nodeid]",

                "attach-db" => @"🔗 ATTACH-DB
Tác dụng: Gán và liên kết cấu hình kết nối của một container Cơ sở dữ liệu (Database) vào công cụ UI quản trị.

Cấu trúc cú pháp hỗ trợ:
  • attach-db db [containerdb] tool [containertool] : Thiết lập cầu nối kết nối giữa container DB và container UI quản lý tương ứng.

Cú pháp đầy đủ: attach-db db [containerdb] tool [containertool]",

                "detach-db" => @"🔌 DETACH-DB
Tác dụng: Ngắt liên kết kết nối và đóng luồng truy cập của một container Cơ sở dữ liệu khỏi công cụ UI quản trị.

Cấu trúc cú pháp hỗ trợ:
  • detach-db db [containerdb] tool [containertool] : Xóa bỏ cấu hình kết nối, cô lập an toàn giữa container DB và container UI quản lý.

Cú pháp đầy đủ: detach-db db [containerdb] tool [containertool]",

                "deploy-git" => @"🚀 DEPLOY-GIT
Tác dụng: Quy trình tự động hóa (Môi trường Test) thực hiện tải mã nguồn từ Git, kích hoạt Build Image cục bộ và khởi chạy ứng dụng thành một instance thử nghiệm (Ví dụ dạng: app_trial).

Cấu trúc cú pháp hỗ trợ:
  • deploy-git url [repo_url] : Tự động hóa triển khai nhanh một dự án từ kho lưu trữ Git từ xa.
  • deploy-git url [repo_url] port [portout] username [username] domain [domain] : Triển khai mã nguồn Git kèm theo định cấu hình cổng public, phân quyền sở hữu và gán domain proxy." + nodeNote + @"

Cú pháp đầy đủ: deploy-git url [repo_url] port [portout] username [username] domain [domain] node [nodeid]",

                "release" => @"🚀 RELEASE
Tác dụng: Đóng gói trạng thái hiện tại của ứng dụng dịch vụ và gắn thẻ phát hành (Tag) thành một phiên bản Image ổn định (Stable Image) sẵn sàng đưa lên môi trường Production.

Cấu trúc cú pháp hỗ trợ:
  • release app [appservice] tag [tag] : Gắn nhãn đóng gói phiên bản cụ thể cho dịch vụ nền tảng chỉ định.

Cú pháp đầy đủ: release app [appservice] tag [tag]",

                "list-release" => @"📋 LIST-RELEASE
Tác dụng: Tra cứu và xem danh sách lịch sử tất cả các phiên bản Image ứng dụng đã được phát hành và lưu trữ của dịch vụ.

Cấu trúc cú pháp hỗ trợ:
  • list-release app [appservice] : Liệt kê danh sách các thẻ Tag phiên bản đang sẵn sàng để triển khai của một dịch vụ.

Cú pháp đầy đủ: list-release app [appservice]",

                "unrelease" => @"❌ UNRELEASE
Tác dụng: Hủy bỏ trạng thái phát hành, xóa hoặc hạ tầng hóa một phiên bản Image cụ thể của dịch vụ ứng dụng ra khỏi danh sách lưu trữ công cộng.

Cấu trúc cú pháp hỗ trợ:
  • unrelease app [appservice] tag [tag] : Loại bỏ thẻ phiên bản chỉ định để ngăn chặn việc deploy nhầm bản lỗi.

Cú pháp đầy đủ: unrelease app [appservice] tag [tag]",

                "deploy-compose" => @"🚀 DEPLOY-COMPOSE
Tác dụng: Triển khai tổ hợp kiến trúc hệ thống Production hoàn chỉnh dựa trên tệp cấu hình Docker Compose cấu trúc đa container từ một App Service và phiên bản được release chính thức (Ví dụ tạo ra các instance dạng: shopmysql).

Cấu trúc cú pháp hỗ trợ:
  • deploy-compose app [appservice] version [version] : Triển khai cụm container phân hệ Production theo phiên bản chỉ định.
  • deploy-compose app [appservice] version [version] port [portout] username [username] domain [domain] : Khởi chạy Docker Compose Production tích hợp định cấu hình cổng, định danh vận hành và trỏ tên miền proxy." + nodeNote + @"

Cú pháp đầy đủ: deploy-compose app [appservice] version [version] port [portout] username [username] domain [domain] node [nodeid]",

                _ => string.Empty
            };
        }
        private static string GetHelpContainer(string action)
        {
            string nodeNote = "\n\nCơ chế điều phối Node (Tham số mở rộng):\n" +
                              "  • Mặc định (Không truyền node): Thực thi truy vấn ngay tại Node hiện tại mà user đang kết nối.\n" +
                              "  • node [nodeid] : Hệ thống tự động chuyển tiếp (Forward) lệnh sang Node mục tiêu chỉ định.\n" +
                              "  • node allnode : Đồng loạt kích hoạt lệnh trên toàn bộ các Node trong cụm hạ tầng.";

            return action switch
            {
                "ps" => @"📊 PS (PROCESS STATUS)
Tác dụng: Tra cứu và hiển thị danh sách các container ứng dụng trên hệ thống hạ tầng.

Cấu trúc cú pháp hỗ trợ:
  1. ps : Hiển thị các container đang hoạt động (Running) tại Node hiện tại.
  2. ps all : Hiển thị tất cả container (bao gồm cả container đang dừng/tắt) tại Node hiện tại.
  3. ps instance [instanceapp] : Hiển thị các container thuộc về cụm Instance cụ thể." + nodeNote + @"

Cú pháp đầy đủ: ps all instance [instanceapp] node [nodeid|allnode]",

                "start" => @"🚀 START
Tác dụng: Khởi động container hoặc cụm ứng dụng đang tạm dừng hoạt động.

Cấu trúc cú pháp hỗ trợ:
  1. start all : Khởi chạy lại toàn bộ tất cả các container đang tắt trên hệ thống.
  2. start instance [instanceapp] : Khởi chạy toàn bộ tất cả các container thuộc cụm Instance ứng dụng đó.
  3. start container [container] : Khởi chạy duy nhất một container được chỉ định cụ thể." + nodeNote + @"

Cú pháp đầy đủ: start all instance [instanceapp] container [container] node [nodeid|allnode]",

                "stop" => @"🛑 STOP
Tác dụng: Dừng hoạt động của container hoặc toàn bộ cụm ứng dụng.

Cấu trúc cú pháp hỗ trợ:
  1. stop all : Tắt đồng loạt toàn bộ tất cả các container đang chạy trên hệ thống.
  2. stop instance [instanceapp] : Tắt đồng loạt toàn bộ tất cả các container thuộc cụm Instance ứng dụng đó.
  3. stop container [container] : Tắt duy nhất một container được chỉ định cụ thể." + nodeNote + @"

Cú pháp đầy đủ: stop all instance [instanceapp] container [container] node [nodeid|allnode]",

                "kill" => @"⚡ KILL
Tác dụng: Cưỡng chế dừng khẩn cấp tiến trình (Force Stop) của container ngay lập tức.

Cấu trúc cú pháp hỗ trợ:
  1. kill all : Cưỡng chế dừng toàn bộ tất cả container đang hoạt động trên hệ thống.
  2. kill instance [instanceapp] : Cưỡng chế dừng toàn bộ container thuộc cụm Instance ứng dụng.
  3. kill container [container] : Cưỡng chế dừng duy nhất một container chỉ định." + nodeNote + @"

Cú pháp đầy đủ: kill all instance [instanceapp] container [container] node [nodeid|allnode]",

                "restart" => @"🔄 RESTART
Tác dụng: Khởi động lại container hoặc toàn bộ cụm ứng dụng để làm mới trạng thái.

Cấu trúc cú pháp hỗ trợ:
  1. restart all : Khởi động lại toàn bộ tất cả container trên hệ thống.
  2. restart instance [instanceapp] : Khởi động lại toàn bộ hệ thống container thuộc cụm Instance ứng dụng.
  3. restart container [container] : Khởi động lại duy nhất một container chỉ định." + nodeNote + @"

Cú pháp đầy đủ: restart all instance [instanceapp] container [container] node [nodeid|allnode]",

                "rm" => @"❌ RM
Tác dụng: Gỡ bỏ container hoàn toàn ra khỏi hệ thống máy chủ.

Cấu trúc cú pháp hỗ trợ:
  1. rm all : Xóa bỏ toàn bộ tất cả container trên hệ thống.
  2. rm instance [instanceapp] : Xóa bỏ toàn bộ tất cả container thuộc cụm Instance ứng dụng đó.
  3. rm container [container] : Xóa bỏ duy nhất một container chỉ định." + nodeNote + @"

Cú pháp đầy đủ: rm all instance [instanceapp] container [container] node [nodeid|allnode]",

                "rmi" => @"❌ RMI
Tác dụng: Xóa Docker Image lưu trữ trên phân đĩa Local của máy chủ Node.

Cấu trúc cú pháp hỗ trợ:
  1. rmi all : Kích hoạt dọn dẹp hàng loạt các Image không sử dụng/mồ côi trên hệ thống.
  2. rmi image [image] : Xóa một Image cụ thể ra khỏi máy chủ." + nodeNote + @"

Cú pháp đầy đủ: rmi all image [image] node [nodeid|allnode]",

                "setcname" => @"✏️ SETCNAME
Tác dụng: Thay đổi tên hiển thị (Container Name) của một container cụ thể.

Cấu trúc cú pháp hỗ trợ:
  1. setcname container [container] newcname [newcname] : Đổi tên của container chỉ định sang tên mới.

Cú pháp đầy đủ: setcname container [container] newcname [newcname]",

                "setcusername" => @"✏️ SETCUSERNAME
Tác dụng: Cập nhật lại tài khoản chủ sở hữu ứng dụng bên trong cụm instance hoặc container chỉ định.

Cấu trúc cú pháp hỗ trợ:
  1. setcusername instance [instanceapp] newcusername [newcusername] : Cập nhật tài khoản sở hữu cho toàn bộ cụm Instance.
  2. setcusername container [container] newcusername [newcusername] : Cập nhật tài khoản sở hữu cho duy nhất một container cụ thể.

Cú pháp đầy đủ: setcusername instance [instanceapp] container [container] newcusername [newcusername]",

                "setcdomain" => @"✏️ SETCDOMAIN
Tác dụng: Thay đổi cấu hình tên miền (Domain) ánh xạ trỏ tới cụm instance hoặc container chỉ định.

Cấu trúc cú pháp hỗ trợ:
  1. setcdomain instance [instanceapp] newcdomain [newcdomain] : Định cấu hình Domain ánh xạ cho toàn bộ cụm Instance.
  2. setcdomain container [container] newcdomain [newcdomain] : Định cấu hình Domain ánh xạ cho duy nhất một container cụ thể.

Cú pháp đầy đủ: setcdomain instance [instanceapp] container [container] newcdomain [newcdomain]",

                "inspect" => @"🔍 INSPECT
Tác dụng: Kết xuất chi tiết cấu hình mạng, biến môi trường và tài nguyên hạ tầng cấp phát cho container.

Cấu trúc cú pháp hỗ trợ:
  1. inspect container [container] : Xem chi tiết thông số kỹ thuật của container chỉ định.

Cú pháp đầy đủ: inspect container [container]",

                _ => string.Empty
            };
        }
        private static string GetHelpAccess(string action)
        {
            return action switch
            {
                "openweb" => @"🌐 OPENWEB
Tác dụng: Kiểm tra endpoint hoặc mở liên kết truy cập giao diện Web của ứng dụng.

Cấu trúc cú pháp hỗ trợ:
  1. openweb instance [instanceapp] : Mở liên kết Web đại diện cho toàn bộ cụm Instance ứng dụng.
  2. openweb container [container] : Truy cập trực tiếp vào endpoint giao diện Web của một container cụ thể.

Cú pháp đầy đủ: openweb instance [instanceapp] container [container]",

                "opentool" => @"🧰 OPENTOOL
Tác dụng: Khởi chạy và mở giao diện UI quản trị (như công cụ quản lý Database, Dashboard tích hợp) của container.

Cấu trúc cú pháp hỗ trợ:
  1. opentool container [container] : Kích hoạt mở bảng điều khiển công cụ cho container chỉ định.

Cú pháp đầy đủ: opentool container [container]",

                "editweb" => @"📝 EDITWEB
Tác dụng: Mở trình biên tập tệp tin trực tuyến (Web Editor) để chỉnh sửa trực tiếp các file cấu hình hoặc mã nguồn bên trong container.

Cấu trúc cú pháp hỗ trợ:
  1. editweb container [container] : Khởi mộc không gian chỉnh sửa tệp tin cho container chỉ định.

Cú pháp đầy đủ: editweb container [container]",

                _ => string.Empty
            };
        }
        private static string GetHelpDebug(string action)
        {
            return action switch
            {
                "logs" => @"📜 LOGS
Tác dụng: Truy xuất và xem nội dung nhật ký giám sát hiệu năng (Monitor Logs) cùng lịch sử sự kiện hệ thống (như tự động co giãn - Auto Scale) theo thời gian thực của ứng dụng.

Cấu trúc cú pháp hỗ trợ:
  1. logs instance [instanceapp] : Xem nhật ký giám sát hiệu năng tích lũy (Monitor) và các sự kiện Auto Scale của toàn bộ cụm Instance đó.
  2. logs container [container] : Xem nhật ký hoạt động chi tiết của duy nhất một container cụ thể được chỉ định.

Cơ chế giới hạn dòng (Tham số lines):
  • Mặc định (Không truyền lines): Hệ thống tự động trích xuất và hiển thị 100 dòng nhật ký gần nhất.
  • lines [number] : Hệ thống sẽ trích xuất chính xác số lượng dòng nhật ký được chỉ định (Ví dụ: lines 20).

Cú pháp đầy đủ: logs instance [instanceapp] container [container] lines [lines]",

                _ => string.Empty
            };
        }
        private static string GetHelpBackup(string action)
        {
            return action switch
            {
                "backup" => @"💾 BACKUP
Tác dụng: Tạo bản sao lưu trạng thái dữ liệu tức thời (Snapshot) cho cụm Instance ứng dụng.

Cấu trúc cú pháp hỗ trợ:
  1. backup instance [instanceapp] tag [tag] : Khởi tạo bản Snapshot dữ liệu và đặt tên nhãn định danh cụ thể (Ví dụ: tag v1.0_stable).

Cú pháp đầy đủ: backup instance [instanceapp] tag [tag]",

                "list-backup" => @"📋 LIST-BACKUP
Tác dụng: Truy xuất và hiển thị danh sách toàn bộ các bản Snapshot sao lưu dữ liệu hiện có của cụm Instance.

Cấu trúc cú pháp hỗ trợ:
  1. list-backup instance [instanceapp] : Xem danh sách các bản phục hồi kèm thông tin dung lượng và thời gian khởi tạo của Instance chỉ định.

Cú pháp đầy đủ: list-backup instance [instanceapp]",

                "rollback" => @"⏪ ROLLBACK
Tác dụng: Tiến hành khôi phục toàn bộ trạng thái dữ liệu của ứng dụng quay về điểm Snapshot đã sao lưu trước đó.

Cấu trúc cú pháp hỗ trợ:
  1. rollback instance [instanceapp] tag [tag] : Thực hiện Rollback dữ liệu của cụm Instance dựa trên nhãn Tag chỉ định.

Cú pháp đầy đủ: rollback instance [instanceapp] tag [tag]",

                "delete-backup" => @"❌ DELETE-BACKUP
Tác dụng: Xóa bỏ vĩnh viễn tệp tin sao lưu dữ liệu cũ của Instance ứng dụng ra khỏi đĩa lưu trữ để giải phóng tài nguyên.

Cấu trúc cú pháp hỗ trợ:
  1. delete-backup instance [instanceapp] tag [tag] : Gỡ bỏ bản Snapshot được chỉ định theo nhãn Tag cụ thể.

Cú pháp đầy đủ: delete-backup instance [instanceapp] tag [tag]",

                _ => string.Empty
            };
        }
        private static string GetHelpMonitoring(string action)
        {
            string nodeNote = "\n\nCơ chế điều phối Node (Tham số mở rộng):\n" +
                              "  • Mặc định (Không truyền node): Thực thi truy vấn ngay tại Node hiện tại.\n" +
                              "  • node [nodeid] : Chuyển tiếp truy vấn thông số sang Node mục tiêu chỉ định.\n" +
                              "  • node allnode : Đồng loạt thu thập thông số hiển thị từ toàn bộ các Node.";

            return action switch
            {
                "stats" => @"📈 STATS
Tác dụng: Giám sát thông số hiệu năng và tài nguyên phần cứng (CPU, RAM, Network I/O) tiêu thụ thực tế.

Cấu trúc cú pháp hỗ trợ:
  1. stats all : Xem thông số tài nguyên của tất cả các container đang chạy trên hệ thống.
  2. stats instance [instanceapp] : Xem thông số tổng hợp của toàn bộ các container thuộc Instance ứng dụng đó.
  3. stats container [container] : Xem thông số của duy nhất một container chỉ định." + nodeNote + @"

Cú pháp đầy đủ: stats all instance [instanceapp] container [container] node [nodeid|allnode]",

                "df" => @"💾 DF
Tác dụng: Kiểm tra dung lượng phân đĩa hệ thống, bộ nhớ đệm (Cache), và Volumes đang bị chiếm dụng bởi Docker." + nodeNote + @"

Cấu trúc cú pháp hỗ trợ:
  1. df : Kiểm tra dung lượng Docker Disk Space tại Node hiện tại.

Cú pháp đầy đủ: df node [nodeid|allnode]",

                "health" => @"❤️ HEALTH
Tác dụng: Kiểm tra trạng thái phản hồi (Health Check), độ trễ và mức độ ổn định của toàn bộ các container thuộc cụm Instance ứng dụng.

Cấu trúc cú pháp hỗ trợ:
  1. health instance [instanceapp] : Quét trạng thái sống/chết (Sức khỏe) của cụm Instance chỉ định.

Cú pháp đầy đủ: health instance [instanceapp]",

                "instances" => @"📦 INSTANCES
Tác dụng: Liệt kê toàn bộ các Instance ứng dụng (Ví dụ: shopmysql, shopmysql_trial) đang hoạt động trên hệ thống.

Cấu trúc cú pháp hỗ trợ:
  1. instances : Hiển thị danh sách các thực thể instance ứng dụng được tạo ra từ lệnh deploy-compose hoặc deploy-git.

Cú pháp đầy đủ: instances",

                "appservices" => @"📦 APPSERVICES
Tác dụng: Liệt kê danh sách các dịch vụ ứng dụng nền tảng gốc đã được khởi tạo trong hệ thống.

Cấu trúc cú pháp hỗ trợ:
  1. appservices : Tra cứu danh sách định danh của các dịch vụ gốc (App Service) trước khi khởi chạy thành Instance.

Cú pháp đầy đủ: appservices",

                "imageservices" => @"📦 IMAGESERVICES
Tác dụng: Tra cứu danh mục các Docker Image Blueprints được cấu hình sẵn trong lõi quản trị. Đây là danh sách các Image mẫu hợp lệ được hệ thống hỗ trợ và cho phép triển khai (Deploy). Các Image ngoài danh mục này sẽ bị từ chối deploy.

Cấu trúc cú pháp hỗ trợ:
  1. imageservices : Kết xuất danh sách Image Blueprint kèm theo thông tin định loại (db, tool, web), cổng trong (Inport), mạng đích và biến môi trường nền tảng (Base Env).

Cú pháp đầy đủ: imageservices",

                _ => string.Empty
            };
        }
        private static string GetHelpDevOps(string action)
        {
            return action switch
            {
                "scale" => @"⚙️ SCALE
Tác dụng: Chủ động điều chỉnh co giãn số lượng các bản sao (Replicas) vận hành của các thành phần bên trong cụm Instance ứng dụng bằng tay.

Cấu trúc cú pháp hỗ trợ:
  1. scale instance [instanceapp] type [type] n [n] : Thay đổi số lượng tài nguyên của phân hệ (Type như: backend, web, lb) thuộc Instance chỉ định lên mức mong muốn (n).

Cú pháp đầy đủ: scale instance [instanceapp] type [type] n [n]",

                "set-alert" => @"🚨 SET-ALERT
Tác dụng: Thiết lập hệ thống giám sát hiệu năng tự động (Thu thập thông số phần trăm CPU) để kích hoạt cơ chế tự động co giãn (Auto Scale Up / Scale Down) khi Instance xảy ra tình trạng quá tải hoặc dư thừa tài nguyên.

Cấu trúc cú pháp hỗ trợ:
  1. set-alert instance [instanceapp] : Khởi tạo cấu hình và kích hoạt chính sách giám sát CPU, tự động co giãn tài nguyên cho Instance chỉ định.

Cú pháp đầy đủ: set-alert instance [instanceapp]",

                "rm-alert" => @"❌ RM-ALERT
Tác dụng: Hủy bỏ giám sát, ngừng theo dõi thông số CPU và tắt hoàn toàn cơ chế tự động co giãn (Auto Scale) trên cụm Instance ứng dụng.

Cấu trúc cú pháp hỗ trợ:
  1. rm-alert instance [instanceapp] : Gỡ bỏ toàn bộ quy tắc giám sát hiệu năng và chính sách tự động co giãn của Instance.

Cú pháp đầy đủ: rm-alert instance [instanceapp]",

                _ => string.Empty
            };
        }
        private static string GetHelpCleanup(string action)
        {
            string nodeNote = "\n\nCơ chế điều phối Node (Tham số mở rộng):\n" +
                              "  • Mặc định (Không truyền node): Thực thi dọn dẹp tài nguyên rác tại Node hiện hành.\n" +
                              "  • node [nodeid] : Hệ thống tự động chuyển tiếp gói tin và kích hoạt dọn dẹp tại Node hạ tầng mục tiêu.\n" +
                              "  • node allnode : Đồng loạt kích hoạt lệnh dọn dẹp trên toàn bộ hệ thống Cluster.";

            return action switch
            {
                "prune" => @"🧹 PRUNE
Tác dụng: Thu hồi và giải phóng dung lượng ổ đĩa phân bổ bằng cách quét sạch tài nguyên rác không sử dụng của Docker Engine trên các Node hạ tầng.

Cấu trúc cú pháp hỗ trợ:
  1. prune : Thực hiện dọn dẹp an toàn (Safe Cleanup). Chỉ quét và xóa bỏ các đối tượng mồ côi (Dangling) bao gồm: container đã dừng, network không dùng, các image mồ côi và dangling volumes.
  2. prune all : Kích hoạt chế độ dọn dẹp triệt để (Aggressive Cleanup). Hệ thống sẽ xóa sạch mọi container bị dừng, network không dùng, tất cả các image không có container tham chiếu (kể cả image có tag thông thường) và toàn bộ các volume." + nodeNote + @"

Cấu trúc đầy đủ: prune all node [nodeid|allnode]",

                _ => string.Empty
            };
        }
        private static string GetHelpSupport(string action)
        {
            string nodeNote = "\n\nCơ chế điều phối Node (Tham số mở rộng):\n" +
                              "  • Mặc định (Không truyền node): Tra cứu dữ liệu trực tiếp tại Node hiện tại.\n" +
                              "  • node [nodeid] : Hệ thống tự động chuyển tiếp truy vấn sang Node mục tiêu chỉ định.\n" +
                              "  • node allnode : Tổng hợp thông tin lưu trữ từ tất cả các Node hiển thị ra màn hình.";

            return action switch
            {
                "images" => @"📦 IMAGES
Tác dụng: Tra cứu danh sách, kích thước và các nhãn thẻ (Tags) của các Docker Image đang lưu trữ cục bộ tại máy chủ Node.

Cấu trúc cú pháp hỗ trợ:
  1. images : Xem danh sách các Docker Image hiện có trên Node hiện hành." + nodeNote + @"

Cấu trúc: images node [nodeid|allnode]",

                "ports" => @"🌐 PORTS
Tác dụng: Thực hiện quét trạng thái Socket mạng (ss -ltn) để liệt kê danh sách toàn bộ các cổng mạng TCP đang mở và đang ở trạng thái lắng nghe (Listening) trên máy chủ Node.

Cấu trúc cú pháp hỗ trợ:
  1. ports : Quét và hiển thị các Port đang mở tại Node hiện hành." + nodeNote + @"

Cấu trúc: ports node [nodeid|allnode]",

                "nodes" => @"🖥️ NODES
Tác dụng: Hiển thị danh sách tổng quan, địa chỉ IP và trạng thái kết nối mạng (Online/Offline) của tất cả các Node máy chủ thuộc hạ tầng Cluster ChatOps.

Cấu trúc cú pháp hỗ trợ:
  1. nodes : Trích xuất danh bạ thông tin và trạng thái hoạt động của toàn bộ cụm hạ tầng.

Cấu trúc: nodes",

                "history" => @"📜 HISTORY
Tác dụng: Tra cứu và xem lại danh sách lịch sử các câu lệnh tương tác kèm số thứ tự (ID) mà tài khoản của bạn đã thực thi trên hệ thống ChatOps.

Cấu trúc cú pháp hỗ trợ:
  1. history : Hiển thị danh sách dòng lệnh đã gõ trong phiên làm việc.

Cấu trúc: history",

                "clear" => @"🧹 CLEAR
Tác dụng: Xóa sạch toàn bộ nội dung hiển thị và lịch sử dòng chat cũ trên giao diện màn hình Frontend ChatOps để làm thoáng không gian làm việc.

Cấu trúc cú pháp hỗ trợ:
  1. clear : Làm sạch màn hình dòng lệnh hiện tại.

Cấu trúc: clear",

                "help" => @"📚 HELP
Tác dụng: Xem danh sách menu hướng dẫn tổng quan phân chia theo nhóm vai trò hoặc tra cứu chi tiết tác dụng, kịch bản cú pháp của một lệnh bất kỳ.

Cấu trúc cú pháp hỗ trợ:
  1. help : Hiển thị bảng tra cứu nhanh toàn bộ danh mục mã lệnh hệ thống hỗ trợ.
  2. help command [command] : Xem tài liệu hướng dẫn chi tiết, cấu trúc phân rã của một lệnh cụ thể (Ví dụ: help command ps).

Cấu trúc: help command [command]",

                _ => string.Empty
            };
        }
        public static string Help(string role)
        {
            return role switch
            {
                "admin" => HelpAdmin(),
                "manager" => HelpManager(),
                "dev" => HelpDev(),
                "ops" => HelpOps(),
                "user" => HelpUser(),
                _ => "Unknown role"
            };
        }
        public static string HelpAdmin() => @"
🔴 ADMIN - FULL ACCESS

USER
whoami, setupasswd, register, setuname, setuemail, seturole, deluser, users

DEPLOY
deploy, attach-db, detach-db, deploy-git, release, list-release, unrelease, deploy-compose

CONTAINER
ps, start, stop, kill, restart, rm, rmi, setcname, setcusername, setcdomain, inspect

ACCESS
openweb, opentool, editweb

DEBUG
logs

BACKUP & DATA
backup, list-backup, rollback, delete-backup

MONITOR
stats, df, health, instances, appservices, imageservices

DEVOPS
scale, set-alert, rm-alert

CLEANUP
prune

SUPPORT
images, ports, nodes, history, clear, help
";
        public static string HelpManager() => @"
🟠 MANAGER - MONITORING & USER CONTROL

USER
whoami, setupasswd, register, setuname, setuemail, seturole, deluser, users

DEPLOY
list-release

SYSTEM & CONTAINER
ps, inspect

ACCESS
openweb, opentool

DEBUG
logs

MONITORING
stats, df, health, instances, appservices, imageservices

SUPPORT
images, ports, nodes, history, clear, help
";
        public static string HelpDev() => @"
🟡 DEV - DEVELOPMENT (TEST ENV)
USER
whoami, setupasswd, users

DEPLOY
deploy, attach-db, detach-db, deploy-git, release, list-release, unrelease

CONTAINER
ps, start, stop, kill, restart, rm, rmi, setcname, setcusername, setcdomain, inspect

ACCESS
openweb, opentool, editweb

DEBUG
logs

BACKUP & DATA
backup, list-backup, rollback, delete-backup

MONITORING
stats, df, health, instances, appservices, imageservices

DEVOPS
scale, set-alert, rm-alert

CLEANUP
prune

SUPPORT
images, ports, nodes, history, clear, help
";
        public static string HelpOps() => @"
🔵 OPS - PRODUCTION
USER
whoami, setupasswd, users

DEPLOY
deploy, attach-db, detach-db, deploy-compose, list-release

CONTAINER
ps, start, stop, kill, restart, rm, rmi, setcname, setcusername, setcdomain, inspect

ACCESS
openweb, opentool, editweb

DEBUG
logs

BACKUP & DATA
backup, list-backup, rollback, delete-backup

MONITORING
stats, df, health, instances, appservices, imageservices

DEVOPS
scale, set-alert, rm-alert

CLEANUP
prune

SUPPORT
images, ports, nodes, history, clear, help
";
        public static string HelpUser() => @"
🟢 USER - VIEW ONLY
USER
whoami, setupasswd

SYSTEM & CONTAINER
ps, inspect

ACCESS
openweb, opentool

DEBUG
logs

MONITORING
stats, health

SUPPORT
history, clear, help
";
    }
}
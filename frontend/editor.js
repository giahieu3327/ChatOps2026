// Phân tích tham số "name" từ chuỗi URL (?name=targetContainer)
const fileTargetName = new URLSearchParams(location.search).get("name");
document.getElementById("display-id").innerText = fileTargetName || "Unknown";
const statusText = document.getElementById("status");

// Tự động nhận diện Domain/IP và Cổng từ môi trường chạy hiện tại (Cổng 880)
const API_BASE = window.location.origin; 

// Lấy Token kiểm soát phiên làm việc từ vùng lưu trữ cục bộ
const token = localStorage.getItem("chatops_token");

// Khởi tạo trình soạn thảo CodeMirror cấu hình tối ưu giao diện
const editor = CodeMirror.fromTextArea(document.getElementById("t"), {
    mode: "htmlmixed",
    theme: "dracula",
    lineNumbers: true,
    autoCloseTags: true,
    tabSize: 4,
    indentUnit: 4,
    lineWrapping: true
});

// Hàm xử lý đồng bộ tải tệp tin HTML từ Hệ thống/Container đích
async function load() {
    if (!fileTargetName) {
        statusText.innerText = "Lỗi: Không tìm thấy tham số tên file target (?name=...)";
        return;
    }

    statusText.innerText = "Đang tải dữ liệu...";
    try {
        // Thực thi yêu cầu kéo dữ liệu cấu hình đính kèm chuỗi JWT Token xác thực
        let r = await fetch(`${API_BASE}/api/file/get?name=${fileTargetName}`, {
            method: "GET",
            headers: {
                "Authorization": token ? `Bearer ${token}` : ""
            }
        });
        
        if (r.status === 401) {
            statusText.innerText = "Lỗi: 401 Unauthorized - Hết hạn phiên!";
            alert("Phiên đăng nhập đã hết hiệu lực hoặc chưa được cấp quyền. Vui lòng kiểm tra lại trạng thái Terminal.");
            return;
        }

        if (r.status === 403) {
            statusText.innerText = "Lỗi: 403 Forbidden - Không có quyền xem!";
            alert("Tài khoản của bạn bị hạn chế quyền đọc mã nguồn tệp tin này.");
            return;
        }

        let resData = await r.json();

        if (!r.ok) {
            throw new Error(resData.message || "Lỗi xảy ra trong quá trình kết nối tải tệp.");
        }

        // Bóc tách chính xác chuỗi HTML thuần nằm trong thuộc tính data
        if (resData && resData.data !== undefined) {
            editor.setValue(resData.data);
            statusText.innerText = "Đã tải xong";
        } else {
            editor.setValue(JSON.stringify(resData, null, 2));
            statusText.innerText = "Cảnh báo: Định dạng dữ liệu không chuẩn.";
        }
    } catch (err) {
        statusText.innerText = "Lỗi tải file!";
        console.error(err);
    }
}

// Hàm xử lý lưu đồng bộ mã nguồn chỉnh sửa đè vào hệ thống Container
async function save() {
    if (!fileTargetName) {
        alert("Không xác định được tên cấu hình file hợp lệ để lưu.");
        return;
    }

    statusText.innerText = "Đang lưu...";
    try {
        // Thực hiện đẩy chuỗi dữ liệu đã soạn thảo lên API qua Gateway điều hướng
        let r = await fetch(`${API_BASE}/api/file/save?name=${fileTargetName}`, {
            method: "POST",
            headers: { 
                "Content-Type": "application/json",
                "Authorization": token ? `Bearer ${token}` : ""
            },
            body: JSON.stringify(editor.getValue())
        });
        
        if (r.status === 401) {
            statusText.innerText = "Lỗi: 401 Unauthorized!";
            alert("Không thể thực hiện tác vụ: Phiên đăng nhập yêu cầu xác thực lại.");
            return;
        }

        if (r.status === 403) {
            statusText.innerText = "Lỗi: 403 Forbidden!";
            alert("Tác vụ bị từ chối: Nhóm quyền của bạn không được phân quyền chỉnh sửa cấu hình.");
            return;
        }

        let resData = await r.json();

        if (!r.ok) {
            throw new Error(resData.message || "Gặp sự cố không xác định khi đồng bộ ghi.");
        }

        statusText.innerText = resData.message || "Đã lưu thành công!";
        setTimeout(() => statusText.innerText = "Ready", 3000);
    } catch (err) {
        statusText.innerText = "Lỗi khi lưu!";
        console.error(err);
        alert(err.message || "Đường truyền API Gateway gặp lỗi. Không thể lưu tệp dữ liệu.");
    }
}

// Khởi tạo các biểu tượng Vector SVG và tự động gọi kích hoạt tải tệp ban đầu
lucide.createIcons();
load();
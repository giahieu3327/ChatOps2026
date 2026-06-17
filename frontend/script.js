const chat = document.getElementById("chat");
const cmdInput = document.getElementById("cmd");

const API_BASE = window.location.origin; 

let cmdHistory = [];
let historyIndex = -1;
let token = localStorage.getItem("chatops_token");
let hubConnection = null;
let currentConnectionId = null; 

// Biến trạng thái Debug điều chỉnh hiển thị Terminal
let isDebugMode = true; 
let currentUserRole = "user"; // Mặc định khởi tạo role thấp nhất để bảo mật

// ==========================================
// CẤU HÌNH ICON KẾT QUẢ CUỐI CÙNG (USER TERMINAL)
// ==========================================
const AppIcons = {
    AllowedIcons: new Set([
        "✅", // Kết quả thực thi thành công từ Node.
        "❌"  // Lỗi hệ thống, lỗi cú pháp hoặc Unauthorized.
    ])
};
// ==========================================

// Phân tích Token để cấu hình quyền hạn hiển thị hệ thống
function parseTokenAndSetRole(t) {
    if (!t) return;
    try {
        const payload = JSON.parse(atob(t.split('.')[1]));
        
        // Trích xuất claim Role chuẩn của .NET Core API
        currentUserRole = payload["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"] || "user";
        
        // Nếu là phân hệ user bình thường, ép buộc tắt Debug toàn cục
        if (currentUserRole.trim().toLowerCase() === "user") {
            isDebugMode = false;
            console.log("🔒 Đã khóa cứng giao diện: Phân hệ [User] chỉ hiển thị kết quả cuối cùng.");
        } else {
            isDebugMode = true; // Admin/Developer mặc định mở debug
        }
    } catch (e) {
        currentUserRole = "user";
        isDebugMode = false;
    }
}

function isTokenExpired(t) {
    try {
        const payload = JSON.parse(atob(t.split('.')[1]));
        return payload.exp < Math.floor(Date.now() / 1000);
    } catch (e) { return true; }
}

if (token && isTokenExpired(token)) {
    localStorage.removeItem("chatops_token");
    token = null;
}

if (token) {
    document.getElementById("login-overlay").classList.add("hidden");
    parseTokenAndSetRole(token); // Cập nhật quyền ngay khi tải lại trang
    initSignalR();
    loadHistory();    
}

function scrollToBottom() {
    chat.scrollTo({ top: chat.scrollHeight, behavior: "smooth" });
}

function initSignalR() {
    if (hubConnection) return;

    hubConnection = new signalR.HubConnectionBuilder()
        .withUrl(`${API_BASE}/chatHub`, {
            accessTokenFactory: () => token
        })
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .build();

    hubConnection.on("ReceiveMessage", (message) => {
        console.log("📩 Log mới từ Backend:", message);
        renderOutput(message);
    });

    hubConnection.onreconnecting((error) => {
        console.warn("⚠️ SignalR đang mất kết nối, tiến hành kết nối lại...", error);
        currentConnectionId = null; 
    });

    hubConnection.onreconnected((connectionId) => {
        console.log("✅ Kết nối lại SignalR Hub thành công. ID:", connectionId);
        currentConnectionId = connectionId; 
    });

    hubConnection.onclose((error) => {
        console.error("❌ Kết nối SignalR Hub đã bị đóng:", error);
        currentConnectionId = null;
        if (error && error.message.includes("401")) {
            logout();
        }
    });

    hubConnection.start()
        .then(() => {
            console.log("🚀 Đã thiết lập kết nối Real-time qua SignalR thành công.");
            currentConnectionId = hubConnection.connectionId; 
            setInterval(() => {
                if (currentConnectionId) {
                    hubConnection.invoke("RefreshSession").catch(err => console.error(err));
                }
            }, 15000); 
        })
        .catch(err => {
            console.error("❌ Lỗi khởi động kết nối SignalR:", err);
        });
}

function renderOutput(rawContent) {
    if (!rawContent) return;
    
    let displayContent = rawContent;
    let themeClass = "text-green-400 border-gray-800 bg-gray-900/40"; 
    const upperMsg = rawContent.toUpperCase();

    // Tách chuỗi xử lý emoji chuẩn mã hóa theo mảng Code Points để lấy kí tự icon chính xác
    const codePoints = Array.from(rawContent);
    let firstIcon = codePoints.length > 0 ? codePoints[0] : "";

    // CHỈ QUÉT ĐÚNG 2 ICON CHO PHÉP: ✅ HOẶC ❌
    const matchIcon = rawContent.match(/([✅❌])/);
    if (matchIcon) {
        firstIcon = matchIcon[1];
    }

    // Thực hiện lọc nghiêm ngặt nếu client đang tắt chế độ debug
    if (!isDebugMode) {
        // Chỉ cho phép hiển thị nếu dòng log đại diện cho thành công hoặc thất bại
        if (!AppIcons.AllowedIcons.has(firstIcon)) {
            return; // Chặn đứng toàn bộ log tiến trình khác
        }
    }

    // Phân định cấu trúc màu sắc hiển thị (Theme) trên Terminal giao diện dựa trên Icon và Từ khóa
    if (upperMsg.includes("[ERROR]") || upperMsg.includes("FAILED") || upperMsg.includes("EXCEPTION") || upperMsg.includes("ERROR:") || firstIcon === "❌") {
        themeClass = "text-red-400 border-red-950/50 bg-red-950/10";
    } else if (upperMsg.includes("[WARNING]") || upperMsg.includes("WARN") || upperMsg.includes("TIMEOUT")) {
        themeClass = "text-yellow-500 border-yellow-950/50 bg-yellow-950/10";
    } else if (upperMsg.includes("[INFO]") || upperMsg.includes("PROCESSING")) {
        themeClass = "text-blue-400 border-blue-950/50 bg-blue-950/10";
    } else if (upperMsg.includes("[SUCCESS]") || upperMsg.includes("SUCCESSFULLY") || firstIcon === "✅") {
        themeClass = "text-emerald-400 border-emerald-950/50 bg-emerald-950/10";
    }

    // =====================================================
    // LOGIC XỬ LÝ LỆNH ĐIỀU HƯỚNG GOTO BẰNG CÁCH TÁCH DÒNG (STARTSWITH)
    // =====================================================
    const lines = displayContent.split("\n");
    let hasGoto = false;
    let rawUrl = "";

    for (let i = 0; i < lines.length; i++) {
        let line = lines[i].trim();
        if (line.startsWith("GOTO|")) {
            hasGoto = true;
            rawUrl = line.replace("GOTO|", "").trim();
            lines.splice(i, 1); // Loại bỏ dòng GOTO thô ra khỏi danh sách hiển thị
            break;
        }
    }

    if (hasGoto) {
        const finalUrl = processUrl(rawUrl);
        if (finalUrl) {
            window.open(finalUrl, "_blank");
        }

        let cleanedContent = lines.join("\n");
        if (rawUrl) {
            const htmlLink = `<a href="${finalUrl}" target="_blank" class="text-cyan-400 underline font-bold hover:text-cyan-300 break-all">${finalUrl}</a>`;
            cleanedContent = cleanedContent.replaceAll(rawUrl, htmlLink);
        }

        chat.innerHTML += `
        <div class="p-4 rounded border ${themeClass} ml-6 mb-4 transition-all duration-200">
            <pre class="font-mono leading-relaxed whitespace-pre-wrap text-sm md:text-base">${cleanedContent}</pre>
        </div>`;

        scrollToBottom();
        return; // Ngắt luồng xử lý tại đây để không chạy xuống logic quét URL mặc định ở dưới
    }
    // =====================================================

    // VÒNG LẶP PHÂN TÍCH VÀ BIẾN ĐỔI LINK CÔNG KHAI ĐA CỔNG CHUẨN XÁC (Dành cho Log thông thường)
    const currentHost = window.location.hostname;
    const isCurrentHostIp = /^(\d{1,3}\.){3}\d{1,3}$/.test(currentHost);

    for (let i = 0; i < lines.length; i++) {
        let line = lines[i].trim();
        
        if (line.includes("http")) {
            const httpIndex = line.indexOf("http");
            const prefix = httpIndex !== -1 ? line.substring(0, httpIndex) : "";

            const urlRegex = /(https?:\/\/[^\s|]+)/g;
            const urls = line.match(urlRegex) || [];

            let processedLinks = [];

            for (let url of urls) {
                let cleanUrl = url.trim();
                if (cleanUrl.endsWith("/")) {
                    cleanUrl = cleanUrl.slice(0, -1);
                }

                const finalUrl = processUrl(cleanUrl);
                const isIpUrl = /^https?:\/\/(\d{1,3}\.){3}\d{1,3}/.test(finalUrl);

                if (isCurrentHostIp) {
                    processedLinks.push(`<a href="${finalUrl}" target="_blank" class="text-cyan-400 underline font-bold hover:text-cyan-300 break-all">${finalUrl}</a>`);
                } else {
                    if (!isIpUrl) {
                        processedLinks.push(`<a href="${finalUrl}" target="_blank" class="text-cyan-400 underline font-bold hover:text-cyan-300 break-all">${finalUrl}</a>`);
                    }
                }
            }

            if (processedLinks.length > 0) {
                lines[i] = prefix + processedLinks.join(" | ");
            } else {
                lines[i] = line;
            }
        }
    }
    
    displayContent = lines.join("\n");

    chat.innerHTML += `
    <div class="p-4 rounded border ${themeClass} ml-6 mb-4 transition-all duration-200">
        <pre class="font-mono leading-relaxed whitespace-pre-wrap text-sm md:text-base">${displayContent}</pre>
    </div>`;

    scrollToBottom();
}

async function loadHistory() {
    if (!token || isTokenExpired(token)) return;

    try {
        let r = await fetch(`${API_BASE}/api/chat/history`, {
            method: "GET",
            headers: { "Authorization": `Bearer ${token}` }
        });

        if (r.ok) {
            let data = await r.json(); 
            if (Array.isArray(data) && data.length > 0) {
                cmdHistory = data;
                console.log("📜 Đã khôi phục đồng bộ lịch sử câu lệnh từ Redis:", cmdHistory.length);
            }
        }
    } catch (e) {
        console.error("❌ Không thể đồng bộ lịch sử câu lệnh:", e);
    }
}

function processUrl(rawUrl) {
    try {
        const parsed = new URL(rawUrl);
        const currentHost = window.location.hostname;
        const currentProtocol = window.location.protocol;
        
        const isUrlAnIp = /^(\d{1,3}\.){3}\d{1,3}$/.test(parsed.hostname);
        const isTargetSystem = parsed.hostname.includes("nt113q22nhom12.ddns.net");

        if (isUrlAnIp || isTargetSystem) {
            const isCurrentHostIp = /^(\d{1,3}\.){3}\d{1,3}$/.test(currentHost);
            const portStr = parsed.port ? `:${parsed.port}` : '';

            if (isCurrentHostIp) {
                return `${currentProtocol}//${currentHost}${portStr}${parsed.pathname}${parsed.search}`;
            } else {
                return `${currentProtocol}//${parsed.hostname}${portStr}${parsed.pathname}${parsed.search}`;
            }
        }
        return rawUrl;
    } catch { 
        return rawUrl; 
    }
}

// ✅ HÀM LOGOUT: Gửi API dọn session lặng lẽ, hiển thị kết quả API trả về rồi tự reload sau 5s
async function logout() {
    console.log("🔄 Đang tiến hành thủ tục logout an toàn...");
    
    if (hubConnection) {
        await hubConnection.stop().catch(() => {});
        hubConnection = null;
    }
    
    if (token) {
        try {
            const controller = new AbortController();
            const timeoutId = setTimeout(() => controller.abort(), 3000);

            let r = await fetch(`${API_BASE}/api/logout`, {
                method: "POST",
                headers: { 
                    "Authorization": `Bearer ${token}`,
                    "Content-Type": "application/json"
                },
                signal: controller.signal
            });
            clearTimeout(timeoutId);

            let Msg = await r.json();
            if (Msg && Msg.message) {
                renderOutput(Msg.message); // Hiện kết quả trả về từ API Logout lên terminal
            }
        } catch (e) { 
            console.error("❌ Lỗi xử lý dọn session khi logout:", e); 
            renderOutput(`[ERROR] Logout Failed: ${e.message}`);
        }
    }

    localStorage.removeItem("chatops_token");
    token = null;
    cmdHistory = [];
    currentConnectionId = null;

    // Im lặng đợi đúng 5 giây rồi reload trang sạch sẽ về form Login
    setTimeout(() => {
        location.reload();
    }, 5000);
}

async function login() {
    let u = document.getElementById("user").value.trim();
    let p = document.getElementById("pass").value.trim();
    let err = document.getElementById("login-err");
    err.classList.add("hidden");

    try {
        let r = await fetch(`${API_BASE}/api/login`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ Username: u, Password: p })
        });
        
        let data = await r.json();
        if (!r.ok) {
            err.classList.remove("hidden");
            err.innerText = data.message;
            return;
        }

        localStorage.setItem("chatops_token", data.token);
        token = data.token;
        document.getElementById("login-overlay").classList.add("hidden");
        
        parseTokenAndSetRole(token); 
        initSignalR();
        await loadHistory();
        cmdInput.focus();
    } catch (e) {
        err.classList.remove("hidden");
        err.innerText = "Lỗi kết nối Gateway điều hướng (880)";
    }
}

async function send() {
    let c = cmdInput.value.trim();
    if (!c) return;

    const lowerCmd = c.toLowerCase();

    // =====================================================
    // LOGIC LỆNH CLEAR: Chỉ in câu lệnh, gửi API, hiện kết quả và tự reload sau 5s
    // =====================================================
    if (lowerCmd === "clear") {
        chat.innerHTML += `
        <div class="flex items-start space-x-2 mb-2">
            <span class="text-blue-500 font-bold">❯</span>
            <span class="text-white font-medium">${escapeHtml(c)}</span>
        </div>`;
        
        cmdInput.value = "";
        scrollToBottom();

        if (!token || isTokenExpired(token)) {
            chat.innerHTML += `<div class="text-red-500 ml-6">❌ Phiên đăng nhập hết hạn</div>`;
            logout();
            return;
        }

        try {
            let r = await fetch(`${API_BASE}/api/chat`, {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "Authorization": `Bearer ${token}`
                },
                body: JSON.stringify({ 
                    command: c, 
                    connectionId: currentConnectionId,
                    debug: isDebugMode
                })
            });

            if (r.status === 401) {
                chat.innerHTML += `<div class="text-red-500 ml-6">❌ Unauthorized</div>`;
                logout();
                return;
            }

            let Msg = await r.json();
            if (!r.ok) {
                renderOutput(`[ERROR] Clear Command Execution Failed: ${Msg.message}`);
            } else {
                if (Msg && Msg.message) {
                    renderOutput(Msg.message); // Hiện kết quả trả về từ API Clear lên terminal
                }
                
                // Im lặng đợi đúng 5 giây rồi reload trang sạch sẽ
                setTimeout(() => {
                    location.reload();
                }, 5000);
            }

        } catch (error) {
            chat.innerHTML += `<div class="text-red-500 ml-6 mb-4">❌ Lỗi hệ thống khi gửi lệnh clear: ${escapeHtml(error.message)}</div>`;
            scrollToBottom();
        }
        return; 
    }
    // =====================================================

    if (lowerCmd === "debug true" || lowerCmd === "debug false") {
        if (currentUserRole.trim().toLowerCase() === "user") {
            chat.innerHTML += `<div class="text-red-400 ml-6 mb-2">❌ [Security] Bạn không có quyền thay đổi chế độ debug của hệ thống.</div>`;
            cmdInput.value = "";
            scrollToBottom();
            return;
        }
        
        isDebugMode = (lowerCmd === "debug true");
        const statusText = isDebugMode ? "BẬT hiển thị tiến trình chi tiết" : "TẮT hiển thị tiến trình. Chỉ hiển thị kết quả cuối cùng";
        chat.innerHTML += `<div class="text-yellow-400 ml-6 mb-2">⚙️ [System] Đã ${statusText}.</div>`;
        cmdInput.value = "";
        scrollToBottom();
        return;
    }
    
    // ✅ GỌI LỆNH LOGOUT QUA ĐƯỜNG TERMINAL GÕ CHỮ: Chỉ hiện câu lệnh đã gõ rồi chạy logout ngầm
    if (lowerCmd === "logout") { 
        chat.innerHTML += `
        <div class="flex items-start space-x-2 mb-2">
            <span class="text-blue-500 font-bold">❯</span>
            <span class="text-white font-medium">${escapeHtml(c)}</span>
        </div>`;
        cmdInput.value = "";
        logout(); 
        return; 
    }

    if (cmdHistory[0] !== c) {
        cmdHistory.unshift(c);
        if (cmdHistory.length > 50) cmdHistory.pop();
    }
    historyIndex = -1;

    chat.innerHTML += `
    <div class="flex items-start space-x-2 mb-2">
        <span class="text-blue-500 font-bold">❯</span>
        <span class="text-white font-medium">${escapeHtml(c)}</span>
    </div>`;

    cmdInput.value = "";
    scrollToBottom();

    if (!token || isTokenExpired(token)) {
        chat.innerHTML += `<div class="text-red-500 ml-6">❌ Phiên đăng nhập hết hạn</div>`;
        logout();
        return;
    }

    if (!currentConnectionId) {
        chat.innerHTML += `<div class="text-yellow-500 ml-6 mb-2">⚠️ Đường truyền real-time đang thiết lập lại, vui lòng gõ lại lệnh sau giây lát...</div>`;
        initSignalR();
        return;
    }

    try {
        let r = await fetch(`${API_BASE}/api/chat`, {
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                "Authorization": `Bearer ${token}`
            },
            body: JSON.stringify({ 
                command: c, 
                connectionId: currentConnectionId,
                debug: isDebugMode
            })
        });

        if (r.status === 401) {
            chat.innerHTML += `<div class="text-red-500 ml-6">❌ Unauthorized</div>`;
            logout();
            return;
        }

        let Msg = await r.json();
        if (!r.ok) {
            renderOutput(`[ERROR] HTTP Command Execution Failed: ${Msg.message}`);
        } else {
            if (Msg && Msg.message) {
                renderOutput(Msg.message);
            }
        }

    } catch (error) {
        chat.innerHTML += `<div class="text-red-500 ml-6 mb-4">❌ Lỗi hệ thống hoặc mất kết nối: ${escapeHtml(error.message)}</div>`;
    }

    scrollToBottom();
}

function escapeHtml(text) {
    const div = document.createElement("div");
    div.innerText = text;
    return div.innerHTML;
}

cmdInput.addEventListener("keydown", (e) => {
    if (e.key === "Enter") send();
    if (e.key === "ArrowUp" && cmdHistory.length > 0) {
        if (historyIndex < cmdHistory.length - 1) {
            historyIndex++;
            cmdInput.value = cmdHistory[historyIndex];
        }
        e.preventDefault();
    }
    if (e.key === "ArrowDown") {
        if (historyIndex > 0) {
            historyIndex--;
            cmdInput.value = cmdHistory[historyIndex];
        } else if (historyIndex === 0) {
            historyIndex = -1;
            cmdInput.value = "";
        }
        e.preventDefault();
    }
});

document.getElementById("pass").addEventListener("keydown", (e) => { if (e.key === "Enter") login(); });
window.onload = () => cmdInput.focus();
chat.onclick = () => cmdInput.focus();
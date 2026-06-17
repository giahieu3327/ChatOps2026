using Microsoft.EntityFrameworkCore;
using ChatOps.Models;
using ChatOps.Data;
using ChatOps.Services.RedisService;
using System.Text.Json;
using AppContext = ChatOps.Data.AppContext;

namespace ChatOps.Services.StartupService
{
    public class SystemStartupService
    {
        private readonly IServiceProvider _serviceProvider;

        public SystemStartupService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task RunStartupJobsAsync()
        {
            Console.WriteLine("🚀 Chạy chuỗi tác vụ khởi tạo hệ thống (Startup Jobs)...");
            try
            {
                await InitializeDatabaseAsync();
                await SeedDefaultAdminAsync();
                await SeedDefaultImageServicesAsync();
                await SeedDefaultAppServicesAsync();
                Console.WriteLine("✅ Hoàn thành các tác vụ cấu hình nền.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Lỗi nghiêm trọng khi khởi động ứng dụng: {ex.Message}");
                throw;
            }
        }

        public async Task InitializeDatabaseAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Console.WriteLine("📦 Kiểm tra trạng thái và thực hiện Migrate Database...");
            await db.Database.MigrateAsync();
            Console.WriteLine("✅ Cấu trúc cơ sở dữ liệu đã sẵn sàng.");
        }

        public async Task SeedDefaultAdminAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            bool adminExists = await db.Users.AnyAsync(x => x.Username == "admin");
            if (adminExists)
            {
                Console.WriteLine("✔ Tài khoản quản trị gốc (admin) đã tồn tại.");
                return;
            }

            db.Users.Add(new User
            {
                Username = "admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin123"),
                Role = "admin"
            });

            await db.SaveChangesAsync();
            Console.WriteLine("✔ Đã nạp tài khoản admin mặc định thành công (admin/admin123).");
        }
        public static async Task SeedDefaultImageServicesAsync()
        {
            Console.WriteLine("🌐 Kiểm tra và đồng bộ cấu hình Image mặc định lên Redis...");
            try
            {
                // 1. Lấy toàn bộ dữ liệu hiện tại đang có trên Redis Hash
                (bool getSuccess, Dictionary<string, string> redisMap) = await RedisImageService.GetImageAsync(imageName: null, isGetAll: true);
                
                // Nếu Redis chưa khởi tạo cấu trúc dữ liệu chính, tiến hành tạo mới
                if (!getSuccess || redisMap == null)
                {
                    await RedisImageService.InsertImageAsync();
                    await RedisImageService.InsertImageCountAsync();
                    redisMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                
                redisMap.Remove("init:placeholder");

                var localServices = ImageCategories.ImageServices;
                bool isAnyUpdated = false;

                // 2. Duyệt qua từng Image gốc ở Local để so khớp cấu hình
                foreach (var local in localServices)
                {
                    string localJson = JsonSerializer.Serialize(new
                    {
                        local.Value.Image,
                        local.Value.Network,
                        local.Value.InPort,
                        local.Value.Type,
                        local.Value.Env,
                        local.Value.neededCount,
                        local.Value.Path
                    });

                    // Trường hợp 1: Redis chưa có Image dịch vụ này
                    if (!redisMap.ContainsKey(local.Key))
                    {
                        Console.WriteLine($"➕ [Startup Seed] Thiếu cấu hình '{local.Key}' trên Redis. Đang thêm mới...");
                        (bool updateSuccess, _) = await RedisImageService.UpdateImageValueAsync(local.Key, localJson);
                        if (updateSuccess)
                        {
                            await RedisImageService.UpdateImageCountValueAsync(isIncrement: true);
                            isAnyUpdated = true;
                        }
                    }
                    // Trường hợp 2: Đã có trên Redis nhưng chuỗi thông số JSON bị sai lệch so với Local gốc
                    else if (redisMap[local.Key] != localJson)
                    {
                        Console.WriteLine($"⚠️ [Startup Seed] Phát hiện sai lệch thông số của '{local.Key}' trên Redis. Tiến hành cập nhật lại...");
                        await RedisImageService.UpdateImageValueAsync(local.Key, localJson);
                        isAnyUpdated = true;
                    }
                }

                if (!isAnyUpdated)
                {
                    Console.WriteLine("✔ Tất cả cấu hình Image gốc đã tồn tại và hoàn toàn chính xác trên Redis. Không cần nạp lại.");
                }
                else
                {
                    Console.WriteLine("✅ Đã hoàn tất bổ sung/sửa đổi các cấu hình Image mặc định thiếu sót.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ [Startup Seed Error] Không thể hoàn thành tác vụ đồng bộ Image lên Redis: {ex.Message}");
            }
        }
        public static async Task SeedDefaultAppServicesAsync()
        {
            Console.WriteLine("🌐 Bắt đầu kiểm tra và cập nhật trạng thái IsReleased cho AppServices trên Redis...");
            try
            {
                var db = AppContext.RedisDB;
                string hashKey = RedisAppService.GetRedisKey();

                // Nếu Key trên Redis chưa từng được tạo, khởi tạo cấu trúc trống và kết thúc sớm
                if (!await db.KeyExistsAsync(hashKey))
                {
                    Console.WriteLine("✔ Không tìm thấy HashKey của App trên Redis. Khởi tạo cấu trúc mặc định...");
                    await RedisAppService.InsertAppAsync();
                    await RedisAppService.InsertAppCountAsync();
                    return;
                }

                // 1. Đọc toàn bộ danh sách App đang lưu trực tiếp trên Redis
                var redisEntries = await db.HashGetAllAsync(hashKey);
                var redisMap = redisEntries
                    .ToDictionary(e => e.Name.ToString(), e => e.Value.ToString(), StringComparer.OrdinalIgnoreCase);

                redisMap.Remove("init:placeholder");

                if (redisMap.Count == 0)
                {
                    Console.WriteLine("✔ Không có dữ liệu App nào trên Redis để kiểm tra.");
                    return;
                }

                bool isAnyUpdated = false;

                // 2. Duyệt qua từng App lấy từ Redis ra để validate trạng thái với DockerHub
                foreach (var item in redisMap)
                {
                    string redisKey = item.Key;
                    string rawJson = item.Value;

                    try
                    {
                        var redisData = JsonSerializer.Deserialize<JsonElement>(rawJson);

                        string url = redisData.TryGetProperty("Url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                        string serviceType = redisData.TryGetProperty("ServiceType", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                        bool currentIsReleased = redisData.TryGetProperty("IsReleased", out var r) && r.GetBoolean();

                        bool calculatedIsReleased = currentIsReleased;

                        // Tách ServiceType để check sự tồn tại của Image trên Docker Hub
                        if (!string.IsNullOrEmpty(serviceType))
                        {
                            string[] serviceTypes = serviceType.Split(',', StringSplitOptions.RemoveEmptyEntries);
                            if (serviceTypes.Length > 0)
                            {
                                List<string> tags = await DockerHubService.DockerHubService.GetImageAsync(redisKey, serviceTypes[0]);

                                // Nếu Docker Hub có tag -> IsReleased phải là true, ngược lại false
                                calculatedIsReleased = tags != null && tags.Count != 0;
                            }
                        }

                        // Nếu phát hiện trạng thái IsReleased thực tế lệch pha so với trạng thái cũ đang lưu trên Redis
                        if (currentIsReleased != calculatedIsReleased)
                        {
                            Console.WriteLine($"⚠️ [Fix App Released] Phát hiện sai lệch cho '{redisKey}'. Redis đang là: {currentIsReleased} | Thực tế DockerHub: {calculatedIsReleased}. Tiến hành đồng bộ lại...");

                            // Khởi tạo lại chuỗi JSON chuẩn với IsReleased đã được đính chính
                            string correctedJson = JsonSerializer.Serialize(new
                            {
                                Url = url,
                                ServiceType = serviceType,
                                IsReleased = calculatedIsReleased
                            });

                            // Ghi đè lại giá trị chính xác vào Hash field của Redis
                            await db.HashSetAsync(hashKey, redisKey, correctedJson);
                            isAnyUpdated = true;
                        }
                    }
                    catch (Exception parseEx)
                    {
                        Console.WriteLine($"❌ [Fix App Error] Không thể parse JSON của App '{redisKey}' trên Redis để kiểm tra: {parseEx.Message}");
                    }
                }

                if (isAnyUpdated)
                {
                    // Cập nhật lại tổng số lượng thực tế đếm được trong Hash sau khi fix
                    long currentRealCount = await db.HashLengthAsync(hashKey);
                    if (await db.HashExistsAsync(hashKey, "init:placeholder")) currentRealCount--;
                    await RedisAppService.UpdateAppCountValueAsync(exactCount: currentRealCount);

                    Console.WriteLine("✅ Đã đính chính và cập nhật xong trạng thái IsReleased cho các App trên Redis.");
                }
                else
                {
                    Console.WriteLine("✔ Kiểm tra hoàn tất. Trạng thái IsReleased của toàn bộ App trên Redis đã trùng khớp với Docker Hub.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Startup Seed Error] Gặp sự cố khi quét và đính chính dữ liệu App trên Redis: {ex.Message}");
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ChatOps.Data;
using AppContext = ChatOps.Data.AppContext;
using ChatOps.Models;
using Microsoft.EntityFrameworkCore;
using ChatOps.Services.RedisService;
using ChatOps.Services.FileService;

namespace ChatOps.Services.ChatService
{
    public static class ChatDeployServiceDeployRelease
    {
        private static async Task SendLogWithDelayAsync(bool debug, string connectionId, string message)
        {
            await Task.Delay(100);
            await RedisChannelService.SendMessageToClientAsync(debug, connectionId, message);
        }

        public static async Task<string> Release(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động Pipeline phát hành ứng dụng (Release)...");

            string service = parsed.GetValueOrDefault("app", "").Trim().ToLower();
            string tag = parsed.GetValueOrDefault("tag", "").Trim();

            if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(tag))
            {
                return "❌ Thiếu tham số 'app' hoặc 'tag' bắt buộc.";
            }

            if (!Regex.IsMatch(service, "^[a-zA-Z0-9_]+$"))
                return "❌ Tên ứng dụng (app) không hợp lệ. Chỉ được chứa chữ cái (hoa/thường), số và dấu gạch dưới (_).";

            string cleanTag = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;
            if (!Version.TryParse(cleanTag, out Version? newVersion))
            {
                return $"❌ Định dạng tag `{tag}` không hợp lệ. Vui lòng sử dụng chuẩn Semantic Versioning (Ví dụ: 1.0.0, 2.1.4).";
            }

            // ĐỘNG HÓA ĐƯỜNG DẪN THƯ MỤC KHÔNG GIAN LƯU TRỮ
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string trialPath = Path.Combine(userHome, "ChatOps", "services", "Trial", service);
            string finalPath = Path.Combine(userHome, "ChatOps", "services", "Final", service);

            if (!Directory.Exists(trialPath))
            {
                return $"❌ Không tìm thấy không gian kiểm thử Project Trial tại: Trial/{service}";
            }

            string sourceCompose = Path.Combine(trialPath, "docker-git.yml");
            if (!File.Exists(sourceCompose))
            {
                return "❌ Không tìm thấy tệp cấu hình gốc docker-git.yml để phân tích cấu trúc dịch vụ.";
            }

            (bool success, var targetServices, string errorMessage) = GetTargetServices(service);
            if (!success)
            {
                return errorMessage;
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Docker Hub] Đang kiểm tra lịch sử phiên bản của {targetServices.Count} thành phần...");

            var checkTagsTasks = targetServices.Select(async comp =>
            {
                var existingTags = await DockerHubService.DockerHubService.GetImageAsync(service, comp);
                return new { Component = comp, Tags = existingTags };
            });
            var componentsTagsResults = await Task.WhenAll(checkTagsTasks);

            foreach (var result in componentsTagsResults)
            {
                if (result.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    return $"❌ Tag `{tag}` đã tồn tại trên Docker Hub cho thành phần `{result.Component}`. Không được phép ghi đè.";
                }

                var versions = result.Tags
                    .Select(t => t.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? t.Substring(1) : t)
                    .Select(t => Version.TryParse(t, out var v) ? v : null)
                    .Where(v => v != null)
                    .ToList();

                if (versions.Count > 0)
                {
                    Version? maxVersion = versions.Max();
                    if (maxVersion != null && newVersion <= maxVersion)
                    {
                        return $"❌ Phiên bản yêu cầu `{tag}` phải lớn hơn phiên bản mới nhất hiện tại (`{maxVersion}`) của thành phần `{result.Component}`.";
                    }
                }
            }

            Directory.CreateDirectory(finalPath);
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🚀 Bắt đầu quy trình biên dịch và đóng gói Docker cục bộ...");

            foreach (var comp in targetServices)
            {
                string imgName = $"{AppContext.dockerUser}/{service}-{comp}:{tag}";
                string contextPath = Path.Combine(trialPath, comp);

                await SendLogWithDelayAsync(session.Debug, connectionId, $"📦 [Docker Build] Đang đóng gói Image cho thành phần `{comp}`...");
                string buildRes = await DockerService.Create.DockerCreateImage.BuildImageAsync(imgName, buildContextPath: contextPath);

                if (buildRes.Contains("error", StringComparison.OrdinalIgnoreCase))
                {
                    return $"❌ Biên dịch thành phần {comp} thất bại!\n\n{buildRes}";
                }

                await SendLogWithDelayAsync(session.Debug, connectionId, $"📤 [Docker Push] Đang đẩy Image thành phần `{comp}` lên Registry...");
                await DockerHubService.DockerHubService.PushImageAsync(service, comp, tag);
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"💾 Cập nhật trạng thái hệ thống và đồng bộ tệp cấu hình `docker-registry.yml`...");

            if (AppCategories.AppServices.TryGetValue(service, out var appConfig))
            {
                AppCategories.AppServices[service] = (appConfig.Url, appConfig.ServiceType, true);
                await RedisAppService.UpdateAppValueAsync(service, appConfig.Url, appConfig.ServiceType, true);
            }

            string destRegistryCompose = Path.Combine(finalPath, "docker-registry.yml");
            string transformResult = DockerComposeFileTransformer.TransformGitToRegistry(sourceCompose, destRegistryCompose);
            if (transformResult != "SUCCESS")
            {
                return transformResult;
            }

            string displayFinalPath = finalPath.Replace('\\', '/');
            return $"✅ Phát hành ứng dụng '{service}' phiên bản `{tag}` thành công!\n📂 Thư mục sản xuất: `{displayFinalPath}`";
        }

        public static async Task<string> ListRelease(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 Đang kết nối Registry API để quét lịch sử phiên bản...");

            string service = parsed.GetValueOrDefault("app", "").Trim().ToLower();
            if (string.IsNullOrWhiteSpace(service))
                return "❌ Vui lòng cung cấp tham số tên ứng dụng 'app'";

            if (!Regex.IsMatch(service, "^[a-zA-Z0-9_]+$"))
                return "❌ Tên ứng dụng (app) không hợp lệ. Chỉ được chứa chữ cái (hoa/thường), số và dấu gạch dưới (_).";

            (bool success, var targetServices, string errorMessage) = GetTargetServices(service);
            if (!success)
            {
                return errorMessage;
            }

            var getTagsTasks = targetServices.Select(async subService => await DockerHubService.DockerHubService.GetImageAsync(service, subService));
            var componentsTags = await Task.WhenAll(getTagsTasks);

            List<string>? commonTags = null;
            foreach (var tags in componentsTags)
            {
                if (commonTags == null)
                {
                    commonTags = tags.ToList();
                }
                else
                {
                    commonTags = commonTags.Intersect(tags, StringComparer.OrdinalIgnoreCase).ToList();
                }

                if (commonTags.Count == 0) break;
            }

            if (commonTags == null || commonTags.Count == 0)
            {
                return $"📋 Không tìm thấy phiên bản release đồng bộ hợp lệ nào cho dịch vụ '{service}' trên Docker Hub Registry.";
            }

            var sortedTags = commonTags
                .Select(t => new { Original = t, Clean = t.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? t.Substring(1) : t })
                .Select(t => new { t.Original, ValidVersion = Version.TryParse(t.Clean, out var v) ? v : new Version(0, 0, 0) })
                .OrderByDescending(t => t.ValidVersion)
                .Select(t => t.Original)
                .ToList();

            var summary = $"📋 DANH SÁCH PHIÊN BẢN RELEASE TRÊN REGISTRY: {service.ToUpper()}\n";
            summary += new string('=', 50) + "\n";

            foreach (var tag in sortedTags)
            {
                summary += $"🔹 Version Tag: `{tag}`\n";
            }

            return summary;
        }

        public static async Task<string> Unrelease(Dictionary<string, string> parsed, UserSession session, string connectionId)
        {
            await SendLogWithDelayAsync(session.Debug, connectionId, $"⏳ [Node {AppContext.ServerID}] Khởi động Pipeline gỡ bỏ phiên bản phát hành (Unrelease)...");

            string service = parsed.GetValueOrDefault("app", "").Trim().ToLower();
            string tagToDelete = parsed.GetValueOrDefault("tag", "").Trim();

            if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(tagToDelete))
                return "❌ Thiếu tham số 'app' hoặc 'tag' bắt buộc.";

            if (!Regex.IsMatch(service, "^[a-zA-Z0-9_]+$"))
                return "❌ Tên ứng dụng (app) không hợp lệ. Chỉ được chứa chữ cái (hoa/thường), số và dấu gạch dưới (_).";

            (bool success, var targetServices, string errorMessage) = GetTargetServices(service);
            if (!success)
            {
                return errorMessage;
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🔍 [Validation] Kiểm tra sự tồn tại của tag `{tagToDelete}` trên Cloud Registry...");

            var checkExistTasks = targetServices.Select(async comp =>
            {
                bool exists = await DockerHubService.DockerHubService.IsTagExistAsync(service, comp, tagToDelete);
                return new { Component = comp, Exists = exists };
            });
            var checkExistResults = await Task.WhenAll(checkExistTasks);

            foreach (var check in checkExistResults)
            {
                if (!check.Exists)
                {
                    return $"❌ Thực thi thất bại. Không tìm thấy Image Tag `{tagToDelete}` của thành phần `{check.Component}` trên Docker Hub.";
                }
            }

            await SendLogWithDelayAsync(session.Debug, connectionId, $"🗑️ Tiến hành gỡ bỏ các thực thể Image Tag trên Docker Hub Registry Cloud...");

            foreach (var comp in targetServices)
            {
                bool isDeleted = await DockerHubService.DockerHubService.DeleteImageAsync(service, comp, tagToDelete);
                if (!isDeleted)
                {
                    await SendLogWithDelayAsync(session.Debug, connectionId, $"⚠️ Lỗi phản hồi từ Registry API khi xóa thành phần `{comp}`.");
                }
            }

            var checkRemainingTasks = targetServices.Select(async comp => await DockerHubService.DockerHubService.GetImageAsync(service, comp));
            var remainingResults = await Task.WhenAll(checkRemainingTasks);

            bool isAnyTagLeft = remainingResults.Any(tags => tags != null && tags.Count > 0);
            string finalStatusCleanupMessage = "";

            if (!isAnyTagLeft)
            {
                await SendLogWithDelayAsync(session.Debug, connectionId, $"🧹 Không còn phiên bản nào tồn tại. Giải phóng không gian và hạ trạng thái hệ thống...");
                finalStatusCleanupMessage = $"\nℹ️ Phát hiện dịch vụ '{service}' không còn phiên bản nào. Hệ thống tự động chuyển trạng thái vận hành về chế độ thử nghiệm (IsReleased = false).";

                if (AppCategories.AppServices.TryGetValue(service, out var appConfig))
                {
                    AppCategories.AppServices[service] = (appConfig.Url, appConfig.ServiceType, false);
                    await RedisAppService.UpdateAppValueAsync(service, appConfig.Url, appConfig.ServiceType, false);
                }

                string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string finalPath = Path.Combine(userHome, "ChatOps", "services", "Final", service);
                if (Directory.Exists(finalPath))
                {
                    try
                    {
                        Directory.Delete(finalPath, true);
                        finalStatusCleanupMessage += $"\n🧹 Đã dọn dẹp cấu trúc thư mục Production cục bộ.";
                    }
                    catch (Exception ex)
                    {
                        finalStatusCleanupMessage += $"\n⚠️ Lỗi dọn dẹp thư mục Final: {ex.Message}";
                    }
                }
            }

            List<string>? commonBackupTags = null;
            foreach (var tags in remainingResults)
            {
                if (commonBackupTags == null)
                {
                    commonBackupTags = tags?.ToList() ?? new List<string>();
                }
                else
                {
                    commonBackupTags = commonBackupTags.Intersect(tags ?? new List<string>(), StringComparer.OrdinalIgnoreCase).ToList();
                }
            }

            string rollbackMessage = "⚠️ Hệ thống trống: Không tìm thấy phiên bản dự phòng đồng bộ nào khác trên Cloud Registry.";
            if (commonBackupTags != null && commonBackupTags.Count > 0)
            {
                var newestTag = commonBackupTags
                    .Select(t => new { Original = t, Clean = t.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? t.Substring(1) : t })
                    .Select(t => new { t.Original, ValidVersion = Version.TryParse(t.Clean, out var v) ? v : new Version(0, 0, 0) })
                    .OrderByDescending(t => t.ValidVersion)
                    .Select(t => t.Original)
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(newestTag))
                {
                    rollbackMessage = $"💡 Gợi ý: Bạn có thể sử dụng Tag lớn nhất còn lại `{newestTag}` để Rollback môi trường Production.";
                }
            }

            return $"✅ Gỡ bỏ phiên bản `{tagToDelete}` của dịch vụ '{service}' hoàn tất.{finalStatusCleanupMessage}\n\n{rollbackMessage}";
        }

        public static (bool success, List<string> services, string errorMessage) GetTargetServices(string service)
        {
            if (!AppCategories.AppServices.TryGetValue(service, out var appConfig))
            {
                return (false, new List<string>(), $"❌ Ứng dụng '{service}' không tồn tại trong hệ thống cấu hình cấu trúc.");
            }

            var targetServices = appConfig.ServiceType
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToLower())
                .ToList();

            if (targetServices.Count == 0)
            {
                return (false, new List<string>(), $"❌ Ứng dụng '{service}' không định nghĩa thành phần service hợp lệ nào để quét.");
            }

            return (true, targetServices, string.Empty);
        }
    }
}

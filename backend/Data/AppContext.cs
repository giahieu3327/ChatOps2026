using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using StackExchange.Serialization;
using StackExchange.Redis;
using ChatOps.Models;

namespace ChatOps.Data
{
    public static class AppContext
    {
        // Tự động lấy từ môi trường hệ thống, nếu null hoặc trống thì dùng giá trị mặc định
        public static string ServerDomain { get; set; } = GetEnv("CHATOPS_SERVER_DOMAIN", "nt113q22nhom12.ddns.net");
        public static string ServerIP { get; set; } = GetEnv("CHATOPS_SERVER_IP", "192.168.1.8");
        public static string ServerID { get; set; } = GetEnv("CHATOPS_SERVER_ID", "1");
        
        // Giải quyết triệt để phần IP máy chủ Redis
        public static string RedisIP { get; set; } = GetEnv("CHATOPS_REDIS_IP", "192.168.1.8");
        public static string RedisHost { get; set; } = GetEnv("CHATOPS_REDIS_HOST", "192.168.1.8:6379");
        
        public static string NodeComponentType { get; set; } = GetEnv("CHATOPS_NODE_TYPE", "both");

        #region Danh Sách Icon Điều Khiển Hiển Thị Terminal (Debug Mode)
        public static readonly HashSet<string> AllowedIcons = new() 
        { 
            "✅", 
            "❌"  
        };
        #endregion
        
        private static readonly Lazy<ConnectionMultiplexer> LazyConnection = new Lazy<ConnectionMultiplexer>(() =>
        {
            var config = ConfigurationOptions.Parse(RedisHost);
            config.AbortOnConnectFail = false; 
            return ConnectionMultiplexer.Connect(config);
        });

        public static ConnectionMultiplexer Redis => LazyConnection.Value;
        public static IDatabase RedisDB => Redis.GetDatabase();
        public static ISubscriber RedisPubSub => Redis.GetSubscriber();
        public static string dockerUser = "giahieu33271";
        public static string dockerPass = "dckr_pat_Aw0Gwp4jq7uDIvghSe37wqVXSBA";

        public static readonly HashSet<string> AlertTargets = new();

        // Hàm bổ trợ đọc biến môi trường ngắn gọn
        private static string GetEnv(string variableName, string defaultValue)
        {
            string value = Environment.GetEnvironmentVariable(variableName);
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
        }
    }
}

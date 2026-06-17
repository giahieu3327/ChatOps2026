using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using StackExchange.Redis;
using ChatOps.Models;

namespace ChatOps.Data
{
    public static class AppContext
    {
        public static string ServerDomain { get; set; } = "nt113q22nhom12.ddns.net";
        public static string ServerIP { get; set; } = "192.168.1.8";
        public static string ServerID { get; set; } = "1";
        public static string RedisIP { get; set; } = "192.168.1.8";
        public static string RedisHost { get; set; } = "192.168.1.8:6379";
        public static string NodeComponentType { get; set; } = "both"; // Giá trị mặc định là "both", có thể là "backend", "frontend" hoặc "both"

        #region Danh Sách Icon Điều Khiển Hiển Thị Terminal (Debug Mode)
        
        /// <summary>
        /// Các icon kết quả cuối cùng luôn được phép hiển thị trên giao diện Terminal.
        /// </summary>
        public static readonly HashSet<string> AllowedIcons = new() 
        { 
            "✅", // Kết quả thực thi thành công từ Node.
            "❌"  // Lỗi hệ thống, lỗi cú pháp hoặc Unauthorized.
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
    }
}
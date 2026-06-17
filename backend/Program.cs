using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using ChatOps.Data;
using ChatOps.Services.BackGroundService;
using ChatOps.Services.StartupService;
using ChatOps.Hubs;
using ChatOps.Services.RedisService;

var builder = WebApplication.CreateBuilder(args);

// =====================================================
// DATABASE
// =====================================================
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection")));

// =====================================================
// SERVICES
// =====================================================
// FIX: Thêm .AddControllersAsServices() để có thể lôi Controller từ tầng DI ServiceProvider ra dùng
builder.Services.AddControllers().AddControllersAsServices();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();

// SignalR
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
});

// Startup Service
builder.Services.AddScoped<SystemStartupService>();

// Background Service
builder.Services.AddHostedService<ChatOpsBackgroundService>();

// Redis
builder.Services.AddSingleton<RedisService>();

// =====================================================
// CORS
// =====================================================
builder.Services.AddCors(options =>
{
    options.AddPolicy("all", policy =>
    {
        policy
            .SetIsOriginAllowed(_ => true)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// =====================================================
// JWT AUTH
// =====================================================
string jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new Exception("Jwt:Key is missing");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,

            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],

            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtKey)),

            ClockSkew = TimeSpan.Zero
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.Request.Path;

                if (!string.IsNullOrEmpty(accessToken)
                    && path.StartsWithSegments("/chatHub"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// =====================================================
// BUILD APP
// =====================================================
var app = builder.Build();

// =====================================================
// INIT REDIS CHANNELS
// =====================================================
var hubContext = app.Services.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<ChatHub>>();

RedisChannelService.RegisterInternalChannels(hubContext);

// force create singleton
app.Services.GetRequiredService<RedisService>();

// =====================================================
// STARTUP JOBS
// =====================================================
using (var scope = app.Services.CreateScope())
{
    var startupService = scope.ServiceProvider.GetRequiredService<SystemStartupService>();
    await startupService.RunStartupJobsAsync();
}

// =====================================================
// MIDDLEWARE PIPELINE
// =====================================================
app.UseSwagger();
app.UseSwaggerUI();

app.UseCors("all");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapHub<ChatHub>("/chatHub");

// =====================================================
// RUN SERVER
// =====================================================
app.Run();
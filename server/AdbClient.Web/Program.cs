using AdbClient.Data.Data;
using AdbClient.Data.Models.Internal;
using AdbClient.Service;
using AdbClient.Service.Middleware;
using AdbClient.Service.Services;
using AdbClient.Web;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Bind AppSettings
var appSettings = new AppSettings();
builder.Configuration.Bind(appSettings);
appSettings.NormalizeAndValidate(builder.Environment.ContentRootPath,
                                 Environment.GetEnvironmentVariable("BASE_PATH"));
builder.Services.AddSingleton(appSettings);

// Configure URLs
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(appSettings.Port);
});

var fileLogging = appSettings.Logging!.File!;
var logPath = fileLogging.Path!;

Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

builder.Host.UseSerilog((_, lc) => lc.Enrich.FromLogContext()
                                     .WriteTo.File(logPath,
                                                   rollOnFileSizeLimit: true,
                                                   fileSizeLimitBytes: fileLogging.FileSizeLimitBytes,
                                                   retainedFileCountLimit: fileLogging.MaxRollingFiles,
                                                   outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                                                   restrictedToMinimumLevel: LogEventLevel.Verbose)
                                     .WriteTo.Console()
                                     .MinimumLevel.ControlledBy(Settings.LoggingLevelSwitch)
                                     .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                                     .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning));

Serilog.Debugging.SelfLog.Enable(TextWriter.Synchronized(Console.Error));

Log.Information("Starting AllDebrid Client host");

builder.Services.AddControllers();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
       .AddCookie(options =>
       {
           options.SlidingExpiration = true;
       });


builder.Services.AddAuthorizationBuilder().AddPolicy("AuthSetting", policyCorrectUser =>
{
    policyCorrectUser.Requirements.Add(new AuthSettingRequirement());
});


builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
       {
           options.User.RequireUniqueEmail = false;
           options.Password.RequiredLength = 5;
           options.Password.RequireUppercase = false;
           options.Password.RequireLowercase = false;
           options.Password.RequireNonAlphanumeric = false;
           options.Password.RequiredUniqueChars = 3;
       })
       .AddEntityFrameworkStores<DataContext>()
       .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Events.OnRedirectToLogin = AuthenticationRedirects.HandleLogin;
    options.Cookie.Name = "SID";
});

builder.Services.Configure<HostOptions>(hostOptions =>
{
    hostOptions.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

// Configure development cors.
builder.Services.AddCors(options =>
{
    options.AddPolicy("Dev",
                      corsBuilder => corsBuilder.AllowAnyMethod()
                                                .AllowAnyHeader()
                                                .AllowCredentials());
});

// Configure misc services.
builder.Services.AddResponseCaching();
builder.Services.AddMemoryCache();
builder.Services.AddDistributedMemoryCache();
builder.Services.RegisterHttpClients();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSession();

builder.Services.AddSignalR(hubOptions =>
{
    hubOptions.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.Services.AddHealthChecks();

builder.Host.UseWindowsService();

AdbClient.Data.DiConfig.Config(builder.Services, appSettings);
builder.Services.RegisterAdbServices();

try
{
    // Build the app
    var app = builder.Build();

    if (builder.Environment.IsDevelopment())
    {
        app.UseCors("Dev");
        app.UseDeveloperExceptionPage();
    }

    app.ConfigureExceptionHandler();

    app.Use(async (context, next) =>
    {
        await next.Invoke();

        if (context.Response.StatusCode != 200)
        {
            Log.Warning("{StatusCode}: {Value}", context.Response.StatusCode, context.Request.Path.Value);
        }
    });

    if (appSettings.BasePath is { } basePath)
    {
        app.UseMiddleware<BaseHrefMiddleware>(basePath);
        app.UsePathBase($"/{basePath}");
    }

    app.UseMiddleware<RequestLoggingMiddleware>();

    app.UseStaticFiles();

    app.UseRouting();

    app.UseAuthentication();

    app.UseAuthorization();

    app.MapHub<AdbHub>("/hub");

    app.MapControllers();

    app.MapHealthChecks("/health");

    app.MapFallbackToFile("index.html");

    // Run the app
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

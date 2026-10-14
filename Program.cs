using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Hosting.WindowsServices;
using MenuRightsAdmin;

// As a Windows service the working folder is System32, so settings and wwwroot are read
// from the exe's own folder.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : default
});
builder.Host.UseWindowsService(o => o.ServiceName = "MenuRightsAdmin");

builder.Services.AddSingleton<MenuStore>();
builder.Services.AddHttpClient("login").ConfigurePrimaryHttpMessageHandler(sp =>
{
    var handler = new HttpClientHandler();
    if (sp.GetRequiredService<IConfiguration>().GetValue<bool>("Auth:AcceptAnyServerCertificate"))
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    return handler;
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "MenuRightsAdmin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });

// Admin list is re-read on every request, so removing a name from appsettings.json
// locks that user out without a restart.
builder.Services.AddAuthorization(o => o.AddPolicy("Admin", p => p
    .RequireAuthenticatedUser()
    .RequireAssertion(ctx => IsAdmin(builder.Configuration, ctx.User.Identity?.Name))));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// UserError = a message for the screen (400); anything else is logged and reported as 500.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (UserError ex)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { message = ex.Message });
    }
    catch (Exception ex) when (!ctx.Response.HasStarted)
    {
        app.Logger.LogError(ex, "Request failed");
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { message = ex.Message });
    }
});

// ---- Login -------------------------------------------------------------------------------
// Credentials are checked by ClassicERPCoreAPI (/Users/authenticate, same as the ERP login);
// this app only decides whether the user is on the admin list.
app.MapPost("/auth/login", async (LoginRequest req, HttpContext ctx, IHttpClientFactory http, IConfiguration cfg) =>
{
    string user = (req.Username ?? "").Trim().ToUpperInvariant();
    if (user == "" || string.IsNullOrEmpty(req.Password))
        return Results.BadRequest(new { message = "Enter user name and password." });
    if (!IsAdmin(cfg, user))
        return Results.Json(new { message = "This user is not on the admin list." }, statusCode: 403);

    var url = cfg["Auth:LoginUrl"];
    if (string.IsNullOrWhiteSpace(url))
        return Results.Json(new { message = "Auth:LoginUrl is not configured." }, statusCode: 500);

    var client = http.CreateClient("login");
    client.Timeout = TimeSpan.FromSeconds(30);
    var msg = new HttpRequestMessage(HttpMethod.Post, url)
    {
        Content = JsonContent.Create(new { username = user, password = req.Password })
    };
    msg.Headers.Add("x-dbtype", cfg["Auth:DbType"] ?? "ORACLE");
    HttpResponseMessage resp;
    try { resp = await client.SendAsync(msg); }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Login API unreachable");
        return Results.Json(new { message = "Login service is not reachable: " + ex.Message }, statusCode: 502);
    }
    if (!resp.IsSuccessStatusCode)
        return Results.Json(new { message = "User name or password is incorrect." }, statusCode: 401);

    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    Audit.Write(user, "LOGIN");
    return Results.Ok(new { user });
});

app.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

app.MapGet("/auth/me", (ClaimsPrincipal u, IConfiguration cfg) =>
    u.Identity?.IsAuthenticated == true && IsAdmin(cfg, u.Identity.Name)
        ? Results.Ok(new
        {
            user = u.Identity.Name,
            defaultGrantUsers = cfg.GetSection("Defaults:GrantUsers").Get<string[]>() ?? [],
            defaultDays = cfg.GetValue("Defaults:Days", 365)
        })
        : Results.Unauthorized());

// ---- API (admins only) -------------------------------------------------------------------
var api = app.MapGroup("/api").RequireAuthorization("Admin");

api.MapGet("/modules", (MenuStore s) => s.GetModulesAsync());
api.MapGet("/modules/{id:int}/menu", (int id, MenuStore s) => s.GetMenuAsync(id));
api.MapGet("/modules/{id:int}/suggest", (int id, string? parent, MenuStore s) => s.SuggestAsync(id, parent));
api.MapPost("/modules/{id:int}/menu", (int id, NewMenuItem req, MenuStore s, ClaimsPrincipal u, IConfiguration cfg) =>
    s.CreateMenuAsync(id, req, u.Identity!.Name!, cfg.GetValue("Defaults:Days", 365)));
api.MapPut("/modules/{id:int}/menu/{orderCode}", async (int id, string orderCode, MenuUpdate req, MenuStore s, ClaimsPrincipal u) =>
{
    await s.UpdateMenuAsync(id, orderCode, req, u.Identity!.Name!);
    return Results.Ok();
});
api.MapGet("/modules/{id:int}/users", (int id, MenuStore s) => s.GetUsersAsync(id));
api.MapGet("/modules/{id:int}/rights", (int id, string user, string schema, MenuStore s) => s.GetRightsAsync(id, user, schema));
api.MapPut("/modules/{id:int}/rights", (int id, RightsSave req, MenuStore s, ClaimsPrincipal u, IConfiguration cfg) =>
    s.SaveRightsAsync(id, req, u.Identity!.Name!, cfg.GetValue("Defaults:Days", 365)));

app.Run();

static bool IsAdmin(IConfiguration cfg, string? user) =>
    !string.IsNullOrWhiteSpace(user) &&
    (cfg.GetSection("Admin:Users").Get<string[]>() ?? [])
        .Any(a => string.Equals(a.Trim(), user.Trim(), StringComparison.OrdinalIgnoreCase));

record LoginRequest(string? Username, string? Password);

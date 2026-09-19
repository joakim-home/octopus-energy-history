using System.Security.Claims;
using JoakimHomeDashboard.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace JoakimHomeDashboard.Web.Pages.Account;

[EnableRateLimiting("login")]
public sealed class LoginModel(LocalAdminAuthService auth) : PageModel
{
    [BindProperty] public LoginInput Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await auth.IsConfiguredAsync(HttpContext.RequestAborted))
            return RedirectToPage("/Account/Setup", new { ReturnUrl });

        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect(SafeReturnUrl(ReturnUrl));

        return Page();
    }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!await auth.IsConfiguredAsync(HttpContext.RequestAborted))
            return RedirectToPage("/Account/Setup", new { ReturnUrl });

        var user = await auth.VerifyAsync(Input.Username ?? "", Input.Password ?? "", HttpContext.RequestAborted);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "Invalid username or password.");
            return Page();
        }

        await SignInAsync(user);
        return LocalRedirect(SafeReturnUrl(ReturnUrl));
    }

    private async Task SignInAsync(LocalAdminUser user)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, user.Username)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = Input.RememberMe });
    }
    private static string SafeReturnUrl(string? returnUrl)
        => !string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
            ? returnUrl
            : "/";

    public sealed class LoginInput
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool RememberMe { get; set; }
    }
}

using System.Security.Claims;
using JoakimHomeDashboard.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JoakimHomeDashboard.Web.Pages.Account;

public sealed class SetupModel(LocalAdminAuthService auth) : PageModel
{
    [BindProperty] public SetupInput Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await auth.IsConfiguredAsync(HttpContext.RequestAborted))
            return RedirectToPage("/Account/Login", new { ReturnUrl });
        return Page();
    }
    public async Task<IActionResult> OnPostAsync()
    {
        if (await auth.IsConfiguredAsync(HttpContext.RequestAborted))
            return RedirectToPage("/Account/Login", new { ReturnUrl });

        if (!string.Equals(Input.Password, Input.ConfirmPassword, StringComparison.Ordinal))
            ModelState.AddModelError(nameof(Input.ConfirmPassword), "Passwords do not match.");

        if (!ModelState.IsValid)
            return Page();

        try
        {
            await auth.CreateAsync(Input.Username ?? "", Input.Password ?? "", HttpContext.RequestAborted);
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, Input.Username!.Trim())],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        var destination = !string.IsNullOrWhiteSpace(ReturnUrl) && ReturnUrl.StartsWith('/') && !ReturnUrl.StartsWith("//")
            ? ReturnUrl
            : "/Setup";
        return LocalRedirect(destination);
    }

    public sealed class SetupInput
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? ConfirmPassword { get; set; }
    }
}

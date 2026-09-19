using System.Text;
using JoakimHomeDashboard.Infrastructure;

namespace JoakimHomeDashboard.Tests;

internal sealed class TestSecretProtector : ISecretProtector
{
    private const string Prefix = "test:";
    public string Protect(string value) => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    public string Unprotect(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value[Prefix.Length..]));
}

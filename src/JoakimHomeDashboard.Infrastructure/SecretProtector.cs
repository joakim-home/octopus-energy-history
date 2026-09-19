namespace JoakimHomeDashboard.Infrastructure;

public interface ISecretProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

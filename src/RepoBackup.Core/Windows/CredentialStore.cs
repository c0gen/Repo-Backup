using System.Security.Cryptography;
using System.Text;
using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Core.Windows;

public sealed class CredentialStore(AppPaths paths)
{
    private string FilePath(string id)
    {
        if (!Guid.TryParse(id, out _)) throw new ArgumentException("Invalid destination identity.");
        return Path.Combine(paths.CredentialsDirectory, id + ".key");
    }
    public void Save(string id, string password)
    {
        if (password.Length < 16) throw new ArgumentException("Recovery key must contain at least 16 characters.");
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, Encoding.UTF8.GetBytes("RepoBackup:v1:" + id), DataProtectionScope.CurrentUser);
            var target = FilePath(id); var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, encrypted); File.Move(temporary, target, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public string Get(string id)
    {
        var encrypted = File.ReadAllBytes(FilePath(id));
        var bytes = ProtectedData.Unprotect(encrypted, Encoding.UTF8.GetBytes("RepoBackup:v1:" + id), DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Delete(string id) => File.Delete(FilePath(id));
    public bool Exists(string id) => File.Exists(FilePath(id));
    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

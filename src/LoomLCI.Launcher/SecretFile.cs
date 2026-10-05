using System.Security.AccessControl;
using System.Security.Principal;

namespace LoomLCI.Launcher;

public static class SecretFile
{
    public static void WriteUserOnly(string path, string secret)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("La runtime key no puede estar vacía.", nameof(secret));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, secret.Trim());

        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException(
                "No se pudo resolver el SID del usuario actual.");

        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(
            new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                AccessControlType.Allow));

        new FileInfo(path).SetAccessControl(security);
        VerifyUserOnly(path, user);
    }

    public static void VerifyUserOnly(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException(
                "No se pudo resolver el SID del usuario actual.");

        VerifyUserOnly(path, user);
    }

    private static void VerifyUserOnly(string path, SecurityIdentifier currentUser)
    {
        var security = new FileInfo(path).GetAccessControl();
        if (!security.AreAccessRulesProtected)
        {
            throw new InvalidOperationException(
                "El archivo secreto conserva herencia ACL.");
        }

        var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();

        if (rules.Any(rule => rule.IsInherited))
        {
            throw new InvalidOperationException(
                "El archivo secreto contiene reglas ACL heredadas.");
        }

        var unexpectedAllow = rules.Any(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            !Equals(rule.IdentityReference, currentUser));

        if (unexpectedAllow)
        {
            throw new InvalidOperationException(
                "El archivo secreto concede acceso a otra identidad.");
        }

        var userHasFullControl = rules.Any(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            Equals(rule.IdentityReference, currentUser) &&
            (rule.FileSystemRights & FileSystemRights.FullControl) ==
                FileSystemRights.FullControl);

        if (!userHasFullControl)
        {
            throw new InvalidOperationException(
                "El usuario actual no conserva FullControl sobre el secreto.");
        }
    }
}

using System.Security.Cryptography;

namespace LoomLCI.Launcher;

public static class UpdateTrust
{
    public const int SupportedProtocol = 2;
    public const string DefaultChannel = "stable";

    public static readonly Uri DefaultManifestUri = new(
        "https://github.com/IvanLeiCas22/LoomLCI/releases/latest/download/loomlci-update.json");

    public static readonly Uri DefaultSignatureUri = new(
        "https://github.com/IvanLeiCas22/LoomLCI/releases/latest/download/loomlci-update.sig");

    private const string PublicKeyBase64 =
        "RUNTMSAAAAAzYFAAkG1RyW1P+uegjK/1kj2JmJH02j1wVRqzOqiVZ0BcHx9sc/fuUSHmTGAJPUL+eayDDZ8Q7GWRQowq1QEe";

    public static IUpdateSignatureVerifier CreateVerifier() =>
        new CngUpdateSignatureVerifier(
            Convert.FromBase64String(PublicKeyBase64));
}

public interface IUpdateSignatureVerifier
{
    bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature);
}

public sealed class CngUpdateSignatureVerifier : IUpdateSignatureVerifier
{
    private readonly byte[] _publicKeyBlob;

    public CngUpdateSignatureVerifier(byte[] publicKeyBlob)
    {
        ArgumentNullException.ThrowIfNull(publicKeyBlob);
        _publicKeyBlob = publicKeyBlob.ToArray();
    }

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        using var key = CngKey.Import(
            _publicKeyBlob,
            CngKeyBlobFormat.EccPublicBlob);
        using var ecdsa = new ECDsaCng(key);

        return ecdsa.VerifyData(
            data,
            signature,
            HashAlgorithmName.SHA256);
    }
}

public sealed record UpdateFeedOptions(
    Uri ManifestUri,
    Uri SignatureUri)
{
    public static UpdateFeedOptions Default { get; } = new(
        UpdateTrust.DefaultManifestUri,
        UpdateTrust.DefaultSignatureUri);

    public static UpdateFeedOptions FromEnvironmentOrDefault()
    {
        var manifest = Environment.GetEnvironmentVariable(
            "LOOMLCI_UPDATE_MANIFEST_URL");
        var signature = Environment.GetEnvironmentVariable(
            "LOOMLCI_UPDATE_SIGNATURE_URL");

        if (string.IsNullOrWhiteSpace(manifest) &&
            string.IsNullOrWhiteSpace(signature))
        {
            return Default;
        }

        if (string.IsNullOrWhiteSpace(manifest) ||
            string.IsNullOrWhiteSpace(signature))
        {
            throw new InvalidOperationException(
                "Defina juntas LOOMLCI_UPDATE_MANIFEST_URL y LOOMLCI_UPDATE_SIGNATURE_URL.");
        }

        return new UpdateFeedOptions(
            ParseFeedUri(manifest),
            ParseFeedUri(signature));
    }

    private static Uri ParseFeedUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !(string.Equals(
                  uri.Scheme,
                  Uri.UriSchemeHttps,
                  StringComparison.OrdinalIgnoreCase) ||
              (string.Equals(
                   uri.Scheme,
                   Uri.UriSchemeHttp,
                   StringComparison.OrdinalIgnoreCase) &&
               uri.IsLoopback)))
        {
            throw new InvalidOperationException(
                "El feed de update debe usar HTTPS; HTTP sólo se admite en loopback para validación local.");
        }

        return uri;
    }
}

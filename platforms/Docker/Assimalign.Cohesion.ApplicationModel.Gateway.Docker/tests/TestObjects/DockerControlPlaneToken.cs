using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

internal sealed class DockerControlPlaneToken : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _keyId;

    public DockerControlPlaneToken()
    {
        ECParameters parameters = _key.ExportParameters(includePrivateParameters: false);
        string x = Base64Url.EncodeToString(parameters.Q.X!);
        string y = Base64Url.EncodeToString(parameters.Q.Y!);
        string canonical = $$"""{"crv":"P-256","kty":"EC","x":"{{x}}","y":"{{y}}"}""";
        _keyId = Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        using JsonDocument document = JsonDocument.Parse(
            $$"""{"kty":"EC","crv":"P-256","x":"{{x}}","y":"{{y}}","kid":"{{_keyId}}","alg":"ES256","use":"sig"}""");
        PublicKey = document.RootElement.Clone();
    }

    public JsonElement PublicKey { get; }

    public string Create(string audience, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        string header = $$"""{"typ":"JWT","alg":"ES256","kid":"{{_keyId}}"}""";
        string payload = $$"""{"iss":"peer","sub":"developer","aud":"{{audience}}","iat":{{issuedAt.ToUnixTimeSeconds()}},"nbf":{{issuedAt.ToUnixTimeSeconds()}},"exp":{{expiresAt.ToUnixTimeSeconds()}},"jti":"{{Guid.NewGuid():N}}"}""";
        string signingInput = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header)) + "."
            + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
        byte[] signature = _key.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    public void Dispose() => _key.Dispose();
}

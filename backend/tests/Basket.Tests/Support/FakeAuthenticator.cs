using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Basket.Tests.Support;

/// <summary>
/// A software WebAuthn authenticator (ES256, "none" attestation) standing in for the phone, so the real
/// registration and sign-in endpoints run in tests exactly as a browser would drive them.
/// </summary>
public sealed class FakeAuthenticator(string origin)
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _credentialId = RandomNumberGenerator.GetBytes(32);
    private string? _userHandle;
    private uint _signCount;

    public string RpId { get; } = new Uri(origin).Host;

    /// <summary>The credential JSON a browser's <c>credential.toJSON()</c> returns after <c>create()</c>.</summary>
    public string Create(string creationOptionsJson, string? originOverride = null)
    {
        var options = JsonNode.Parse(creationOptionsJson)!;
        var challenge = options["challenge"]!.GetValue<string>();
        _userHandle = options["user"]!["id"]!.GetValue<string>();

        var clientData = ClientData("webauthn.create", challenge, originOverride ?? origin);
        var authData = AuthenticatorData(flags: 0x45, includeCredential: true);

        var attestation = new CborWriter(CborConformanceMode.Ctap2Canonical);
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt");
        attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt");
        attestation.WriteStartMap(0);
        attestation.WriteEndMap();
        attestation.WriteTextString("authData");
        attestation.WriteByteString(authData);
        attestation.WriteEndMap();

        return new JsonObject
        {
            ["id"] = B64(_credentialId),
            ["rawId"] = B64(_credentialId),
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = B64(clientData),
                ["attestationObject"] = B64(attestation.Encode()),
                ["transports"] = new JsonArray("internal"),
            },
            ["clientExtensionResults"] = new JsonObject(),
        }.ToJsonString();
    }

    /// <summary>The credential JSON after <c>get()</c> against the given request options.</summary>
    public string Get(string requestOptionsJson, string? originOverride = null)
    {
        var options = JsonNode.Parse(requestOptionsJson)!;
        var challenge = options["challenge"]!.GetValue<string>();
        _signCount++;

        var clientData = ClientData("webauthn.get", challenge, originOverride ?? origin);
        var authData = AuthenticatorData(flags: 0x05, includeCredential: false);
        var signed = authData.Concat(SHA256.HashData(clientData)).ToArray();
        var signature = _key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return new JsonObject
        {
            ["id"] = B64(_credentialId),
            ["rawId"] = B64(_credentialId),
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = B64(clientData),
                ["authenticatorData"] = B64(authData),
                ["signature"] = B64(signature),
                ["userHandle"] = _userHandle,
            },
            ["clientExtensionResults"] = new JsonObject(),
        }.ToJsonString();
    }

    private byte[] ClientData(string type, string challenge, string clientOrigin) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["type"] = type,
            ["challenge"] = challenge,
            ["origin"] = clientOrigin,
            ["crossOrigin"] = false,
        });

    private byte[] AuthenticatorData(byte flags, bool includeCredential)
    {
        using var ms = new MemoryStream();
        ms.Write(SHA256.HashData(Encoding.UTF8.GetBytes(RpId)));
        ms.WriteByte(flags);
        var count = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(count, _signCount);
        ms.Write(count);
        if (includeCredential)
        {
            ms.Write(new byte[16]); // AAGUID
            var length = new byte[2];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)_credentialId.Length);
            ms.Write(length);
            ms.Write(_credentialId);
            ms.Write(CoseKey());
        }

        return ms.ToArray();
    }

    private byte[] CoseKey()
    {
        var p = _key.ExportParameters(false);
        var cose = new CborWriter(CborConformanceMode.Ctap2Canonical);
        cose.WriteStartMap(5);
        cose.WriteInt32(1);
        cose.WriteInt32(2); // kty EC2
        cose.WriteInt32(3);
        cose.WriteInt32(-7); // alg ES256
        cose.WriteInt32(-1);
        cose.WriteInt32(1); // crv P-256
        cose.WriteInt32(-2);
        cose.WriteByteString(p.Q.X!);
        cose.WriteInt32(-3);
        cose.WriteByteString(p.Q.Y!);
        cose.WriteEndMap();
        return cose.Encode();
    }

    private static string B64(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

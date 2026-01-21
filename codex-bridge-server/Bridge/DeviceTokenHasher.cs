// DeviceTokenHasher：生成/校验设备令牌哈希，避免明文令牌落盘。
using System.Security.Cryptography;
using System.Text;

namespace codex_bridge_server.Bridge;

internal static class DeviceTokenHasher
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string ComputeBase64Hash(string token)
    {
        var bytes = Utf8NoBom.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }

    public static bool VerifyBase64Hash(string token, string expectedBase64Hash)
    {
        if (string.IsNullOrWhiteSpace(expectedBase64Hash))
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(expectedBase64Hash);
        }
        catch (FormatException)
        {
            return false;
        }

        var bytes = Utf8NoBom.GetBytes(token);
        var actual = SHA256.HashData(bytes);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}


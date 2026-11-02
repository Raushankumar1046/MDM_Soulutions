namespace EndpointManager.Server.Services;

using System.Security.Cryptography;
using System.Text;

public class TotpService
{
    private const int TimeStepSeconds = 30;
    private const int Digits = 6;
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Generates a cryptographically secure Base32-encoded secret key for 2FA.
    /// Default length: 20 bytes (160 bits) -> 32 characters Base32.
    /// </summary>
    public string GenerateSecret(int byteLength = 20)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return ToBase32String(bytes);
    }

    /// <summary>
    /// Generates the standard otpauth URI used by Authenticator apps (Google, Microsoft, Authy).
    /// </summary>
    public string GenerateOtpAuthUri(string issuer, string userEmailOrName, string base32Secret)
    {
        var cleanIssuer = Uri.EscapeDataString(issuer.Trim());
        var cleanUser = Uri.EscapeDataString(userEmailOrName.Trim());
        var cleanSecret = base32Secret.Trim().Replace(" ", "").ToUpperInvariant();

        return $"otpauth://totp/{cleanIssuer}:{cleanUser}?secret={cleanSecret}&issuer={cleanIssuer}&algorithm=SHA1&digits={Digits}&period={TimeStepSeconds}";
    }

    /// <summary>
    /// Validates a 6-digit TOTP code against the Base32 secret.
    /// Allows +/- 1 time step drift (90-second total window) to tolerate client phone clock differences.
    /// </summary>
    public bool ValidateCode(string base32Secret, string code, int allowedDriftSteps = 1)
    {
        if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        string cleanCode = code.Trim().Replace(" ", "").Replace("-", "");
        if (cleanCode.Length != Digits || !int.TryParse(cleanCode, out _))
        {
            return false;
        }

        byte[] secretBytes;
        try
        {
            secretBytes = FromBase32String(base32Secret);
        }
        catch
        {
            return false;
        }

        long currentStep = GetCurrentTimeStep();

        // Check current, previous, and next time steps
        for (int i = -allowedDriftSteps; i <= allowedDriftSteps; i++)
        {
            long stepToCheck = currentStep + i;
            string expectedCode = ComputeTotpForStep(secretBytes, stepToCheck);
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(cleanCode), Encoding.UTF8.GetBytes(expectedCode)))
            {
                return true;
            }
        }

        return false;
    }

    private static long GetCurrentTimeStep()
    {
        long secondsSinceEpoch = (long)(DateTime.UtcNow - UnixEpoch).TotalSeconds;
        return secondsSinceEpoch / TimeStepSeconds;
    }

    private static string ComputeTotpForStep(byte[] secretBytes, long step)
    {
        // 8-byte big-endian counter
        byte[] counterBytes = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counterBytes);
        }

        using var hmac = new HMACSHA1(secretBytes);
        byte[] hash = hmac.ComputeHash(counterBytes);

        // Dynamic truncation (RFC 4226 / RFC 6238)
        int offset = hash[^1] & 0x0F;
        int binaryCode = ((hash[offset] & 0x7F) << 24)
                       | ((hash[offset + 1] & 0xFF) << 16)
                       | ((hash[offset + 2] & 0xFF) << 8)
                       | (hash[offset + 3] & 0xFF);

        int otp = binaryCode % 1_000_000;
        return otp.ToString("D6");
    }

    #region Base32 Encoding & Decoding (RFC 4648)
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string ToBase32String(byte[] data)
    {
        if (data == null || data.Length == 0) return string.Empty;

        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int currentByte = 0;
        int digit = 0;

        foreach (byte b in data)
        {
            currentByte = (currentByte << 8) | b;
            digit += 8;
            while (digit >= 5)
            {
                digit -= 5;
                int index = (currentByte >> digit) & 0x1F;
                sb.Append(Base32Alphabet[index]);
            }
        }

        if (digit > 0)
        {
            int index = (currentByte << (5 - digit)) & 0x1F;
            sb.Append(Base32Alphabet[index]);
        }

        return sb.ToString();
    }

    public static byte[] FromBase32String(string base32)
    {
        if (string.IsNullOrWhiteSpace(base32)) return Array.Empty<byte>();

        string clean = base32.Trim().TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>();

        int currentByte = 0;
        int bitsRemaining = 0;

        foreach (char c in clean)
        {
            int val = Base32Alphabet.IndexOf(c);
            if (val < 0) continue; // Skip invalid or whitespace

            currentByte = (currentByte << 5) | val;
            bitsRemaining += 5;

            if (bitsRemaining >= 8)
            {
                bitsRemaining -= 8;
                output.Add((byte)((currentByte >> bitsRemaining) & 0xFF));
            }
        }

        return output.ToArray();
    }
    #endregion
}


using CL.TwoFactorAuth.Models;
using CL.TwoFactorAuth.Services;
using Xunit;

namespace TwoFactorAuth.Tests;

// Offline unit tests for CL.TwoFactorAuth. The TOTP + QR services are pure crypto with
// no network or runtime dependency, so they are instantiated directly with a config model.

public class TwoFactorAuthenticatorTests
{
    private static TwoFactorAuthenticator NewAuth() => new(new TwoFactorAuthConfig());

    [Fact]
    public void GenerateSecretKey_returns_nonempty_base32_and_two_calls_differ()
    {
        var auth = NewAuth();
        var a = auth.GenerateSecretKey();
        var b = auth.GenerateSecretKey();

        Assert.False(string.IsNullOrWhiteSpace(a));
        // Base32 alphabet: A-Z and 2-7 (no padding from KeyGeneration output).
        Assert.All(a, c => Assert.True((c >= 'A' && c <= 'Z') || (c >= '2' && c <= '7'), $"unexpected char '{c}'"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void GenerateNewKey_produces_key_with_secret_and_provisioning_uri()
    {
        var auth = NewAuth();
        var key = auth.GenerateNewKey("MyApp", "alice@example.com");

        Assert.False(string.IsNullOrWhiteSpace(key.SecretKey));
        Assert.StartsWith("otpauth://", key.ProvisioningUri);
        Assert.Contains("secret=" + key.SecretKey, key.ProvisioningUri);
    }

    [Fact]
    public void Totp_round_trip_validates()
    {
        var auth = NewAuth();
        var secret = auth.GenerateSecretKey();

        var code = auth.GenerateTotpCode(secret);
        Assert.True(code.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(code.Value));

        var result = auth.ValidateTotp(code.Value!, secret);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Wrong_code_is_rejected()
    {
        var auth = NewAuth();
        var secret = auth.GenerateSecretKey();

        var code = auth.GenerateTotpCode(secret);
        Assert.True(code.IsSuccess);

        // Pick a code guaranteed to differ from the real one.
        var wrong = code.Value == "000000" ? "111111" : "000000";
        var result = auth.ValidateTotp(wrong, secret);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Code_from_one_secret_does_not_validate_against_another()
    {
        var auth = NewAuth();
        var secretA = auth.GenerateSecretKey();
        var secretB = auth.GenerateSecretKey();

        var codeA = auth.GenerateTotpCode(secretA);
        Assert.True(codeA.IsSuccess);

        var result = auth.ValidateTotp(codeA.Value!, secretB);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void GetSecondsUntilExpiry_is_within_time_step()
    {
        var config = new TwoFactorAuthConfig();
        var auth = new TwoFactorAuthenticator(config);

        var seconds = auth.GetSecondsUntilExpiry();
        Assert.True(seconds > 0, $"got {seconds}");
        Assert.True(seconds <= config.TimeStepSeconds, $"got {seconds}");
    }

    [Fact]
    public void GetCurrentTimeWindow_is_positive()
    {
        var auth = NewAuth();
        Assert.True(auth.GetCurrentTimeWindow() > 0);
    }
}

/// <summary>Deterministic TOTP checks against the RFC 6238 SHA-1 test vectors.</summary>
public class TotpWindowAndProvisioningTests
{
    // RFC 6238 appendix B secret "12345678901234567890" (ASCII) in Base32.
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    // At T = 1111111109 the 8-digit code is 07081804, so the 6-digit code is 081804; step = T / 30.
    private static readonly DateTime RfcTime = DateTime.UnixEpoch.AddSeconds(1111111109);
    private const long RfcStep = 1111111109 / 30;

    [Fact]
    public void Matched_window_is_the_absolute_time_step_and_offset_is_zero_in_the_current_step()
    {
        var auth = new TwoFactorAuthenticator(new TwoFactorAuthConfig());
        var result = auth.ValidateTotp("081804", RfcSecret, RfcTime);

        Assert.True(result.IsValid);
        Assert.Equal(RfcStep, result.MatchedWindow);
        Assert.Equal(0, result.WindowOffset);
    }

    [Theory]
    [InlineData(30, -1)]   // validated one step later: the code is from the previous step
    [InlineData(-30, 1)]   // validated one step earlier: the code is from the next step
    public void Offset_reports_drift_while_matched_window_stays_absolute(int shiftSeconds, int expectedOffset)
    {
        var auth = new TwoFactorAuthenticator(new TwoFactorAuthConfig { WindowSize = 1 });
        var result = auth.ValidateTotp("081804", RfcSecret, RfcTime.AddSeconds(shiftSeconds));

        Assert.True(result.IsValid);
        Assert.Equal(RfcStep, result.MatchedWindow);
        Assert.Equal(expectedOffset, result.WindowOffset);
    }

    [Fact]
    public void Outside_the_window_is_rejected_with_no_window()
    {
        var auth = new TwoFactorAuthenticator(new TwoFactorAuthConfig { WindowSize = 0 });
        var result = auth.ValidateTotp("081804", RfcSecret, RfcTime.AddSeconds(30));

        Assert.False(result.IsValid);
        Assert.Null(result.MatchedWindow);
        Assert.Null(result.WindowOffset);
    }

    [Fact]
    public void Matched_window_does_not_overflow_with_a_short_period()
    {
        // With a 1 s step in 2100 the step number is above int.MaxValue; it was narrowed to int before.
        var at = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var code = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(RfcSecret), step: 1).ComputeTotp(at);
        var auth = new TwoFactorAuthenticator(new TwoFactorAuthConfig { TimeStepSeconds = 1, WindowSize = 0 });

        var result = auth.ValidateTotp(code, RfcSecret, at);

        Assert.True(result.IsValid);
        var expected = new DateTimeOffset(at).ToUnixTimeSeconds();
        Assert.True(expected > int.MaxValue);
        Assert.Equal(expected, result.MatchedWindow);
        Assert.Equal(0, result.WindowOffset);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void Provisioning_uri_carries_the_configured_period(int period)
    {
        var auth = new TwoFactorAuthenticator(new TwoFactorAuthConfig { TimeStepSeconds = period });
        var key = auth.GenerateNewKey("MyApp", "alice@example.com");

        Assert.Equal(period, key.PeriodSeconds);
        Assert.EndsWith($"&period={period}", key.ProvisioningUri);
    }

    [Fact]
    public void Provisioning_uri_is_exact_for_a_known_key()
    {
        var key = new TwoFactorKey(RfcSecret, "My App", "a@b.c", 45);
        Assert.Equal($"otpauth://totp/My%20App:a%40b.c?secret={RfcSecret}&issuer=My%20App&period=45", key.ProvisioningUri);
    }
}

public class TwoFactorAuthConfigValidationTests
{
    [Fact]
    public void Defaults_and_documented_limits_are_valid()
    {
        Assert.True(new TwoFactorAuthConfig().Validate().IsValid);
        Assert.True(new TwoFactorAuthConfig { TimeStepSeconds = 300, WindowSize = 10, QrCodeModuleSize = 100 }.Validate().IsValid);
        Assert.True(new TwoFactorAuthConfig { TimeStepSeconds = 1, WindowSize = 0, QrCodeModuleSize = 1 }.Validate().IsValid);
    }

    [Theory]
    [InlineData(0, 1, 20)]
    [InlineData(301, 1, 20)]
    [InlineData(30, -1, 20)]
    [InlineData(30, 11, 20)]
    [InlineData(30, 1, 0)]
    [InlineData(30, 1, 101)]
    public void Values_outside_the_documented_limits_are_invalid(int step, int window, int module)
    {
        var config = new TwoFactorAuthConfig { TimeStepSeconds = step, WindowSize = window, QrCodeModuleSize = module };
        Assert.False(config.Validate().IsValid);
    }
}

public class QrCodeGeneratorTests
{
    private static readonly byte[] PngSignature = { 0x89, (byte)'P', (byte)'N', (byte)'G' };

    private static (TwoFactorAuthenticator auth, QrCodeGenerator qr, TwoFactorKey key) Setup()
    {
        var config = new TwoFactorAuthConfig();
        var auth = new TwoFactorAuthenticator(config);
        var qr = new QrCodeGenerator(config);
        var key = auth.GenerateNewKey("MyApp", "bob@example.com");
        return (auth, qr, key);
    }

    [Fact]
    public void GenerateQrCodePng_returns_png_bytes()
    {
        var (_, qr, key) = Setup();

        var result = qr.GenerateQrCodePng(key);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.NotEmpty(result.Value!);
        Assert.Equal(PngSignature, result.Value!.Take(4).ToArray());
    }

    [Fact]
    public void GenerateQrCodeBase64_returns_nonempty()
    {
        var (_, qr, key) = Setup();

        var result = qr.GenerateQrCodeBase64(key);
        Assert.True(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.Value));
    }

    [Fact]
    public void GenerateQrCodeDataUri_has_png_data_uri_prefix()
    {
        var (_, qr, key) = Setup();

        var result = qr.GenerateQrCodeDataUri(key);
        Assert.True(result.IsSuccess);
        Assert.StartsWith("data:image/png;base64,", result.Value!);
    }
}

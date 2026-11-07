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

    [Fact]
    public void Provisioning_uri_escapes_every_component()
    {
        // A secret with padding or a hostile value must not add or override query parameters.
        var key = new TwoFactorKey("ABC=&issuer=Evil#x", "A&B:C", "u?v#w");
        var uri = new Uri(key.ProvisioningUri);

        Assert.Equal("otpauth://totp/A%26B%3AC:u%3Fv%23w?secret=ABC%3D%26issuer%3DEvil%23x&issuer=A%26B%3AC&period=30",
            key.ProvisioningUri);
        Assert.Equal(string.Empty, uri.Fragment);
        Assert.Equal(3, uri.Query.TrimStart('?').Split('&').Length);
    }

    [Fact]
    public void Validation_error_does_not_leak_exception_text()
    {
        var auth = new TwoFactorAuthenticator(new TwoFactorAuthConfig());
        var result = auth.ValidateTotp("123456", "not-base32!!");

        Assert.False(result.IsValid);
        Assert.Equal(TotpValidationResult.ValidationErrorMessage, result.ErrorMessage);
    }

    [Fact]
    public void Generate_error_does_not_leak_exception_text()
    {
        var auth = new TwoFactorAuthenticator(new TwoFactorAuthConfig());
        var result = auth.GenerateTotpCode("not-base32!!");

        Assert.True(result.IsFailure);
        Assert.DoesNotContain("Base32", result.Error!.Message);
        Assert.Null(result.Error.Details);
    }
}

/// <summary>Replay protection through <see cref="ITotpUsedStepStore"/> and code normalisation, on the RFC 6238 vectors.</summary>
public class TotpReplayAndNormalisationTests
{
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    private static readonly DateTime RfcTime = DateTime.UnixEpoch.AddSeconds(1111111109);
    private const string RfcCode = "081804";
    // One step later the code is 301504 (T = 1111111139 is in step 37037038? no: 1111111109/30 = 37037036, +30 s = step 37037037).
    private static readonly DateTime NextStepTime = RfcTime.AddSeconds(30);

    private static TwoFactorAuthenticator Auth(ITotpUsedStepStore? store = null, int window = 1, CodeLogic.Core.Events.IEventBus? events = null) =>
        new(new TwoFactorAuthConfig { WindowSize = window }, null, events, store);

    private static string CodeAt(DateTime at) => new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(RfcSecret)).ComputeTotp(at);

    [Fact]
    public void Second_use_of_the_same_code_for_the_same_user_is_refused()
    {
        var auth = Auth();

        var first = auth.ValidateTotp(RfcCode, RfcSecret, RfcTime, userId: "alice");
        var second = auth.ValidateTotp(RfcCode, RfcSecret, RfcTime, userId: "alice");

        Assert.True(first.IsValid);
        Assert.False(second.IsValid);
        Assert.Equal(TotpValidationResult.ReplayedCodeMessage, second.ErrorMessage);
        Assert.Null(second.MatchedWindow);
    }

    [Fact]
    public void Replay_within_the_drift_window_is_refused_too()
    {
        var auth = Auth(window: 1);
        Assert.True(auth.ValidateTotp(RfcCode, RfcSecret, RfcTime, "alice").IsValid);
        // Same code, presented 20 s later: still inside the window, same step, refused.
        var again = auth.ValidateTotp(RfcCode, RfcSecret, RfcTime.AddSeconds(20), "alice");
        Assert.False(again.IsValid);
        Assert.Equal(TotpValidationResult.ReplayedCodeMessage, again.ErrorMessage);
    }

    [Fact]
    public void A_different_user_is_unaffected()
    {
        var auth = Auth();
        Assert.True(auth.ValidateTotp(RfcCode, RfcSecret, RfcTime, "alice").IsValid);
        Assert.True(auth.ValidateTotp(RfcCode, RfcSecret, RfcTime, "bob").IsValid);
        Assert.False(auth.ValidateTotp(RfcCode, RfcSecret, RfcTime, "bob").IsValid);
    }

    [Fact]
    public void Older_step_than_the_last_accepted_is_refused()
    {
        var auth = Auth(window: 1);
        var nextCode = CodeAt(NextStepTime);

        // Accept the newer step first (validated at its own time), then the previous step's code within the window.
        Assert.True(auth.ValidateTotp(nextCode, RfcSecret, NextStepTime, "alice").IsValid);
        var older = auth.ValidateTotp(RfcCode, RfcSecret, NextStepTime, "alice");

        Assert.False(older.IsValid);
        Assert.Equal(TotpValidationResult.ReplayedCodeMessage, older.ErrorMessage);
    }

    [Fact]
    public void Newer_step_is_accepted_after_an_older_one()
    {
        var auth = Auth(window: 1);
        Assert.True(auth.ValidateTotp(RfcCode, RfcSecret, RfcTime, "alice").IsValid);
        var next = auth.ValidateTotp(CodeAt(NextStepTime), RfcSecret, NextStepTime, "alice");
        Assert.True(next.IsValid);
        Assert.Equal(1111111109 / 30 + 1, next.MatchedWindow);
    }

    [Fact]
    public void Without_a_user_id_the_same_code_validates_again()
    {
        var auth = Auth();
        Assert.True(auth.ValidateTotp(RfcCode, RfcSecret, RfcTime).IsValid);
        Assert.True(auth.ValidateTotp(RfcCode, RfcSecret, RfcTime).IsValid);
    }

    [Fact]
    public void A_wrong_code_does_not_consume_a_step()
    {
        var auth = Auth();
        Assert.False(auth.ValidateTotp("000000", RfcSecret, RfcTime, "alice").IsValid);
        Assert.True(auth.ValidateTotp(RfcCode, RfcSecret, RfcTime, "alice").IsValid);
    }

    [Fact]
    public void Replay_publishes_a_failed_validation_event()
    {
        var bus = new CodeLogic.Core.Events.EventBus();
        var events = new List<CL.TwoFactorAuth.Events.TotpValidatedEvent>();
        bus.Subscribe<CL.TwoFactorAuth.Events.TotpValidatedEvent>(events.Add);
        var auth = Auth(events: bus);

        auth.ValidateTotp(RfcCode, RfcSecret, RfcTime, "alice");
        auth.ValidateTotp(RfcCode, RfcSecret, RfcTime, "alice");

        Assert.Equal([true, false], events.Select(e => e.IsValid));
        Assert.Equal(1111111109 / 30, events[0].MatchedWindow);
        Assert.Null(events[1].MatchedWindow);
    }

    [Fact]
    public void A_shared_store_is_honoured_across_authenticators()
    {
        var store = new InMemoryTotpUsedStepStore();
        var a = Auth(store);
        var b = Auth(store);

        Assert.True(a.ValidateTotp(RfcCode, RfcSecret, RfcTime, "alice").IsValid);
        Assert.False(b.ValidateTotp(RfcCode, RfcSecret, RfcTime, "alice").IsValid);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void In_memory_store_is_monotonic_per_user_and_forgets_after_retention()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryTotpUsedStepStore(retention: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryTotpUsedStepStore(retention: TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryTotpUsedStepStore(sweepInterval: TimeSpan.Zero));
        var keep = new InMemoryTotpUsedStepStore();
        Assert.True(keep.TryUse("u", 10));
        Assert.False(keep.TryUse("u", 10));
        Assert.False(keep.TryUse("u", 9));
        Assert.True(keep.TryUse("u", 11));
        Assert.True(keep.TryUse("v", 1));
        Assert.Equal(TimeSpan.FromHours(2), new InMemoryTotpUsedStepStore().Retention);
    }

    [Theory]
    [InlineData("081 804")]
    [InlineData(" 081804 ")]
    [InlineData("081\t804")]
    [InlineData("0 8 1 8 0 4")]
    [InlineData("081804\n")]
    public void Whitespace_in_a_typed_code_is_ignored(string typed)
    {
        var auth = Auth();
        var result = auth.ValidateTotp(typed, RfcSecret, RfcTime);
        Assert.True(result.IsValid, typed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("08180")]
    public void Empty_or_short_codes_are_invalid_without_throwing(string? typed)
    {
        var auth = Auth();
        Assert.False(auth.ValidateTotp(typed!, RfcSecret, RfcTime).IsValid);
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

    [Fact]
    public void Generation_errors_carry_a_generic_message_and_only_the_exception_type()
    {
        var (_, qr, _) = Setup();

        var png = qr.GenerateQrCodePng(null!);
        var bmp = qr.GenerateQrCodeBmp(null!);

        foreach (var error in new[] { png.Error!, bmp.Error! })
        {
            Assert.Equal("The QR code could not be generated.", error.Message);
            Assert.Equal(nameof(ArgumentNullException), error.Details);
            Assert.DoesNotContain("Parameter", error.ToString());
        }
        Assert.Equal("2fa.qr_png_failed", png.Error!.Code);
        Assert.Equal("2fa.qr_bmp_failed", bmp.Error!.Code);
    }

    [Theory]
    [InlineData("qrcode.png")]                // relative
    [InlineData("sub/qrcode.png")]
    [InlineData("")]
    [InlineData("   ")]
    public void Save_refuses_a_path_that_is_not_fully_qualified(string path)
    {
        var (_, qr, key) = Setup();
        var result = qr.SaveQrCodeToFile(key, path);
        Assert.IsType<CodeLogic.Core.Results.ValidationError>(result.Error);
        Assert.Equal("2fa.qr_invalid_path", result.Error!.Code);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("qr.jpg")]
    [InlineData("qr.png.exe")]
    [InlineData("qr")]
    [InlineData("qr.png\0.exe")]
    public void Save_refuses_a_path_without_the_png_extension(string name)
    {
        var (_, qr, key) = Setup();
        var path = Path.Combine(Path.GetTempPath(), "cl_2fa_" + Guid.NewGuid().ToString("N") + "_" + name);
        var result = qr.SaveQrCodeToFile(key, path);
        Assert.Equal("2fa.qr_invalid_path", result.Error!.Code);
        Assert.False(File.Exists(path.Replace("\0", "")));
    }

    [Fact]
    public void Save_writes_a_png_to_a_fully_qualified_path()
    {
        var (_, qr, key) = Setup();
        var path = Path.Combine(Path.GetTempPath(), "cl_2fa_" + Guid.NewGuid().ToString("N") + ".PNG");
        try
        {
            var result = qr.SaveQrCodeToFile(key, path);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(PngSignature, File.ReadAllBytes(path).Take(4).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_failure_does_not_echo_the_path_or_exception_text()
    {
        var (_, qr, key) = Setup();
        var missingDir = Path.Combine(Path.GetTempPath(), "cl_2fa_missing_" + Guid.NewGuid().ToString("N"), "qr.png");

        var result = qr.SaveQrCodeToFile(key, missingDir);

        Assert.True(result.IsFailure);
        Assert.Equal("2fa.qr_save_failed", result.Error!.Code);
        Assert.Equal("The QR code could not be saved.", result.Error.Message);
        Assert.DoesNotContain("cl_2fa_missing_", result.Error.ToString());
        Assert.Equal(nameof(DirectoryNotFoundException), result.Error.Details);
    }
}

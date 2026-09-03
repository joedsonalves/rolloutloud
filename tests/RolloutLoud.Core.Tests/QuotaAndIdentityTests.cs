using RolloutLoud.Core.Watchdog;
using RolloutLoud.Core.Workspace;
using Xunit;

namespace RolloutLoud.Core.Tests;

/// <summary>
/// Running out of allowance is not running out of ideas, and the two look identical from outside.
/// </summary>
public class QuotaDetectorTests
{
    [Theory]
    [InlineData("Claude usage limit reached. Your limit will reset at 3pm.")]
    [InlineData("5-hour limit reached ∙ resets 10pm")]
    [InlineData("You've hit your usage limit for this month.")]
    [InlineData("Error: 429 Too Many Requests")]
    [InlineData("rate limit exceeded, please try again later")]
    [InlineData("Insufficient credits on this account.")]
    [InlineData("Limite de uso atingido.")]
    public void A_spent_allowance_is_recognised(string output)
    {
        Assert.True(QuotaDetector.Inspect(output).Exhausted);
    }

    [Theory]
    [InlineData("Ran the suite; 3 tests failed on the fixture path.")]
    [InlineData("I was unable to reach the host. Trying the IP directly.")]
    [InlineData("Declared attempt 14 and running it now.")]
    public void Ordinary_work_is_not_mistaken_for_a_spent_allowance(string output)
    {
        Assert.False(QuotaDetector.Inspect(output).Exhausted);
    }

    [Fact]
    public void A_relative_retry_is_read_out_of_the_message()
    {
        var signal = QuotaDetector.Inspect("Rate limited. Please try again in 42 minutes.");

        Assert.True(signal.Exhausted);
        Assert.NotNull(signal.ResetsAt);

        var wait = QuotaDetector.WaitFor(signal, DateTimeOffset.Now);

        // 42 minutes plus the grace minute, give or take the moment the test took to run.
        Assert.InRange(wait.TotalMinutes, 42, 44);
    }

    [Fact]
    public void A_clock_time_already_past_today_is_read_as_tomorrow()
    {
        // "resets at 3pm" said at 4pm means tomorrow. Reading it as today computes a negative
        // wait and sends the agent straight back into the wall.
        var now = new DateTimeOffset(2026, 9, 3, 16, 0, 0, TimeSpan.Zero);
        var signal = QuotaDetector.Inspect("usage limit reached — resets at 3pm");

        Assert.True(signal.Exhausted);
        Assert.True(QuotaDetector.WaitFor(signal, now) > TimeSpan.Zero);
    }

    [Fact]
    public void No_reset_time_falls_back_to_a_blind_wait_rather_than_zero()
    {
        var signal = QuotaDetector.Inspect("quota exceeded");

        Assert.True(signal.Exhausted);
        Assert.Null(signal.ResetsAt);
        Assert.Equal(QuotaDetector.BlindWait, QuotaDetector.WaitFor(signal, DateTimeOffset.Now));
    }

    [Fact]
    public void A_reset_time_in_the_past_still_waits()
    {
        // Stale message, or clocks that disagree. Neither is a reason to hammer the wall again.
        var signal = new QuotaSignal(true, "limit", DateTimeOffset.Now.AddHours(-2));

        Assert.True(QuotaDetector.WaitFor(signal, DateTimeOffset.Now) > TimeSpan.Zero);
    }
}

/// <summary>
/// The operator lends an identity by writing a file. No file is the answer, not a missing setting.
/// </summary>
public class AttachedIdentityTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    [Fact]
    public void No_file_means_no_identity()
    {
        Assert.Null(AttachedIdentity.Load(Path.Combine(Path.GetTempPath(), "no-such-identity.json")));
    }

    [Fact]
    public void An_unreadable_file_is_treated_as_absent()
    {
        // Failing closed here means an agent is told there is no identity, which is the safe
        // misreading of a broken file.
        var file = TempFile();
        File.WriteAllText(file, "{ not json");

        try
        {
            Assert.Null(AttachedIdentity.Load(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_file_with_no_allowed_sites_grants_nothing()
    {
        // Empty-means-none, not empty-means-all: a file somebody created and did not finish
        // filling in must not be a wider grant than one they never created at all.
        var identity = new AttachedIdentity
        {
            Fields = new Dictionary<string, string> { ["email"] = "a@b.c" },
        };

        Assert.False(identity.IsUsable);
        Assert.False(identity.AllowsSite("anything.com"));
    }

    [Fact]
    public void A_site_is_matched_on_its_host_however_it_is_written()
    {
        var identity = new AttachedIdentity
        {
            AllowedSites = ["app.staging.example.com"],
            Fields = new Dictionary<string, string> { ["email"] = "a@b.c" },
        };

        Assert.True(identity.AllowsSite("app.staging.example.com"));
        Assert.True(identity.AllowsSite("https://App.Staging.Example.com/signup"));
        Assert.True(identity.AllowsSite("app.staging.example.com:8443"));

        Assert.False(identity.AllowsSite("example.com"));
        Assert.False(identity.AllowsSite("evil.com"));
    }

    [Fact]
    public void A_wildcard_covers_subdomains_but_not_a_different_domain()
    {
        var identity = new AttachedIdentity
        {
            AllowedSites = ["*.staging.example.com"],
            Fields = new Dictionary<string, string> { ["email"] = "a@b.c" },
        };

        Assert.True(identity.AllowsSite("app.staging.example.com"));
        Assert.False(identity.AllowsSite("staging.example.com.evil.com"));
        Assert.False(identity.AllowsSite("example.com"));
    }

    [Fact]
    public void An_unnamed_site_is_refused()
    {
        var identity = new AttachedIdentity
        {
            AllowedSites = ["app.example.com"],
            Fields = new Dictionary<string, string> { ["email"] = "a@b.c" },
        };

        Assert.False(identity.AllowsSite(null));
        Assert.False(identity.AllowsSite("  "));
    }
}

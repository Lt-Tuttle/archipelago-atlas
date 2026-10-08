using AP_Atlas.Core.EngineSetup;

namespace AP_Atlas.Core.Tests;

/// <summary>The engine setup's one number: phases weighted by their time, never going down, a download moving within its phase.</summary>
public sealed class SetupProgressPlanTests
{
    [Fact]
    public void Only_the_needed_phases_count_and_they_sum_to_one()
    {
        var plan = SetupProgressPlan.For(new[] { "runtime", "pip", "archipelago", "packages", "tracker", "bridge", "check" });
        Assert.Equal(7, plan.Steps);
        Assert.Equal(0, plan.Step);
        Assert.Equal(1, plan.Enter("runtime"));
        Assert.Equal(0f, plan.Fraction);
        Assert.Equal(7, plan.Enter("check"));
        Assert.Equal((4f + 3f + 6f + 9f + 0.5f + 0.5f) / 32f, plan.Fraction, 3);
        Assert.Equal(1f, plan.Complete());
    }

    [Fact]
    public void A_download_moves_within_its_phase_and_nothing_goes_down()
    {
        var plan = SetupProgressPlan.For(new[] { "runtime", "archipelago" }); // 4 + 6
        plan.Enter("runtime");
        Assert.Equal(0.2f, plan.Within(0.5f), 3); // half of runtime's 0.4 share
        Assert.Equal(0.2f, plan.Within(0.1f), 3); // a lower report leaves it where it is
        Assert.Equal(0.2f, plan.Within(-1f), 3); // unknown size: no change
        Assert.Equal(0.4f, plan.Within(1.5f), 3); // capped at the phase's end
        plan.Enter("archipelago");
        Assert.Equal(0.4f, plan.Fraction, 3);
        Assert.Equal(2, plan.Step);
    }

    [Fact]
    public void The_second_health_check_is_its_own_phase_and_unknown_ids_change_nothing()
    {
        var plan = SetupProgressPlan.For(new[] { "check", "world-packages", "check-2" });
        Assert.Equal(1, plan.Enter("check"));
        Assert.Equal(2, plan.Enter("world-packages"));
        Assert.Equal(3, plan.Enter("check")); // entered again: the "-2" phase
        Assert.Equal((9f + 8f) / 20f, plan.Fraction, 3);
        Assert.Equal(0, plan.Enter("no-such-phase"));
        Assert.Equal(3, plan.Step);
    }

    [Fact]
    public void An_existing_install_runs_only_the_short_phases()
    {
        var plan = SetupProgressPlan.For(new[] { "tracker", "bridge", "check" });
        Assert.Equal(3, plan.Steps);
        plan.Enter("bridge");
        Assert.Equal(0.5f / 10f, plan.Fraction, 3);
    }
}

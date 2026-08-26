using Sirocco.Scenarios;

namespace Sirocco.UnitTests.Scenarios;

public sealed class EnvironmentAccessPolicyTests
{
    [Fact]
    public void Denied_allows_nothing() =>
        Assert.False(EnvironmentAccessPolicy.Denied.Allows("SIROCCO_TEST_TOKEN"));

    [Fact]
    public void A_name_in_the_allowlist_is_allowed()
    {
        EnvironmentAccessPolicy policy = new(["SIROCCO_TEST_TOKEN"], allowAll: false);

        Assert.True(policy.Allows("SIROCCO_TEST_TOKEN"));
    }

    [Fact]
    public void A_name_outside_the_allowlist_is_rejected()
    {
        EnvironmentAccessPolicy policy = new(["SIROCCO_TEST_TOKEN"], allowAll: false);

        Assert.False(policy.Allows("SOME_OTHER_NAME"));
    }

    [Fact]
    public void Allow_all_permits_any_name_even_with_an_empty_list()
    {
        EnvironmentAccessPolicy policy = new([], allowAll: true);

        Assert.True(policy.Allows("ANYTHING"));
    }

    [Fact]
    public void A_null_list_is_rejected() =>
        Assert.Throws<ArgumentNullException>(() => new EnvironmentAccessPolicy(null!, allowAll: false));
}
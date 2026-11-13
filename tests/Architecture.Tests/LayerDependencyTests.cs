using System.Reflection;
using NetArchTest.Rules;

namespace Architecture.Tests;

/// <summary>Enforces the dependency rule: Domain ← Application ← Infrastructure ← Api.</summary>
public sealed class LayerDependencyTests
{
    private static readonly Assembly Domain = typeof(global::Domain.Entity).Assembly;
    private static readonly Assembly Application = typeof(global::Application.AssemblyMarker).Assembly;
    private static readonly Assembly Infrastructure = typeof(global::Infrastructure.DependencyInjection).Assembly;
    private static readonly Assembly Api = typeof(Program).Assembly;

    private static void AssertNoDependency(Assembly from, params string[] forbiddenNamespaces)
    {
        var result = Types.InAssembly(from).That().ResideInNamespaceStartingWith(from.GetName().Name!)
            .ShouldNot().HaveDependencyOnAny(forbiddenNamespaces)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"{from.GetName().Name} must not depend on {string.Join(", ", forbiddenNamespaces)}. Offenders: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Positive_control_rules_can_detect_a_real_dependency()
    {
        // If this stops holding, the "must not depend on" rules below would pass vacuously.
        var result = Types.InAssembly(Infrastructure).That().HaveName("RedisRateLimiter")
            .Should().HaveDependencyOn("StackExchange.Redis")
            .GetResult();

        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public void Domain_depends_on_no_other_layer() =>
        AssertNoDependency(Domain, "Application", "Infrastructure", "Api");

    [Fact]
    public void Domain_has_no_framework_or_persistence_dependencies() =>
        AssertNoDependency(Domain, "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "StackExchange.Redis", "Npgsql");

    [Fact]
    public void Application_depends_only_on_Domain() =>
        AssertNoDependency(Application, "Infrastructure", "Api", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "StackExchange.Redis", "Npgsql");

    [Fact]
    public void Infrastructure_does_not_depend_on_Api() =>
        AssertNoDependency(Infrastructure, "Api");

    [Fact]
    public void Api_endpoints_do_not_reach_into_Redis_directly()
    {
        // Endpoints and middleware talk to abstractions (IRateLimiter, IIdempotencyStore), not StackExchange.Redis.
        AssertNoDependency(Api, "StackExchange.Redis");
    }
}

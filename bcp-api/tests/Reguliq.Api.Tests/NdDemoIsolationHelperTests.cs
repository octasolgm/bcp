using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdDemoIsolationHelperTests
{
  [Theory]
  [InlineData("demo@example.com", true)]
  [InlineData("Demo.User@bank.com", true)]
  [InlineData("maker@bank.com", false)]
  [InlineData(null, false)]
  public void IsDemoEmail_detects_demo_substring(string? email, bool expected) =>
    Assert.Equal(expected, NdDemoIsolationHelper.IsDemoEmail(email));

  [Theory]
  [InlineData("Demo Admin", true)]
  [InlineData("Maker One", false)]
  [InlineData(null, false)]
  public void IsDemoName_detects_demo_substring(string? name, bool expected) =>
    Assert.Equal(expected, NdDemoIsolationHelper.IsDemoName(name));

  [Fact]
  public void ShouldSimulateAi_when_viewer_is_demo()
  {
    var demoId = Guid.NewGuid();
    var ctx = new NdDemoIsolationContext(
      true,
      true,
      new HashSet<Guid> { demoId },
      new JwtUser(demoId, "demo@test.com"));

    Assert.True(NdDemoIsolationHelper.ShouldSimulateAi(ctx, Guid.NewGuid()));
    Assert.True(NdDemoIsolationHelper.ShouldSimulateAi(ctx, null));
  }

  [Fact]
  public void ShouldSimulateAi_when_resource_owner_is_demo()
  {
    var ownerId = Guid.NewGuid();
    var ctx = new NdDemoIsolationContext(
      true,
      false,
      new HashSet<Guid> { ownerId },
      new JwtUser(Guid.NewGuid(), "admin@bank.com"));

    Assert.True(NdDemoIsolationHelper.ShouldSimulateAi(ctx, ownerId));
    Assert.False(NdDemoIsolationHelper.ShouldSimulateAi(ctx, Guid.NewGuid()));
  }

  [Fact]
  public void ForbidDemoAiOperations_returns_403_for_demo_viewer()
  {
    var ctx = new NdDemoIsolationContext(
      true,
      true,
      new HashSet<Guid>(),
      new JwtUser(Guid.NewGuid(), "demo@test.com"));

    var result = NdDemoIsolationHelper.ForbidDemoAiOperations(ctx);
    Assert.NotNull(result);
    Assert.Equal(403, result!.StatusCode);
  }

  [Fact]
  public void ForbidDemoAiOperations_returns_null_for_non_demo_viewer()
  {
    var ctx = new NdDemoIsolationContext(
      true,
      false,
      new HashSet<Guid>(),
      new JwtUser(Guid.NewGuid(), "maker@bank.com"));

    Assert.Null(NdDemoIsolationHelper.ForbidDemoAiOperations(ctx));
  }

  [Fact]
  public void ShouldSimulateAi_false_when_demo_mode_disabled()
  {
    var demoId = Guid.NewGuid();
    var ctx = new NdDemoIsolationContext(
      false,
      true,
      new HashSet<Guid> { demoId },
      new JwtUser(demoId, "demo@test.com"));

    Assert.False(NdDemoIsolationHelper.ShouldSimulateAi(ctx, demoId));
    Assert.False(NdDemoIsolationHelper.ShouldSimulateAi(ctx, null));
  }

  /// <summary>
  /// Regulation/internal parse+extract controllers branch on ShouldSimulateAi before live AI services.
  /// Demo viewers must always hit simulation (never ThrowIfLiveAiForbidden live path).
  /// </summary>
  [Fact]
  public void Demo_viewer_always_uses_simulation_branch()
  {
    var viewerId = Guid.NewGuid();
    var ownerId = Guid.NewGuid();
    var ctx = new NdDemoIsolationContext(
      true,
      true,
      new HashSet<Guid> { viewerId },
      new JwtUser(viewerId, "demo@bank.com"));

    Assert.True(NdDemoIsolationHelper.ShouldSimulateAi(ctx, ownerId));
    Assert.True(NdDemoIsolationHelper.ShouldSimulateAi(ctx, viewerId));
    Assert.True(NdDemoIsolationHelper.ShouldSimulateAi(ctx, null));
  }

  [Fact]
  public void ShouldSimulateAi_when_invited_user_in_demo_profile_set()
  {
    var demoAdminId = Guid.NewGuid();
    var invitedMakerId = Guid.NewGuid();
    var ctx = new NdDemoIsolationContext(
      true,
      true,
      new HashSet<Guid> { demoAdminId, invitedMakerId },
      new JwtUser(invitedMakerId, "tester4@arena.com"));

    Assert.True(NdDemoIsolationHelper.ShouldSimulateAi(ctx, invitedMakerId));
    Assert.True(NdDemoIsolationHelper.ShouldSimulateAi(ctx, null));
  }
}

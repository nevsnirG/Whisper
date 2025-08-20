using System.Runtime.CompilerServices;

namespace Hermes.Core.UnitTests;

public class DomainEventTrackerTests
{
    [Fact(DisplayName = "Domain events raised inside of a scope are accessible inside of that scope")]
    public async Task DomainEventsAreTrackedInsideOfScopes()
    {
        using var scope = await DomainEventTracker.CreateScope();
        DomainEventTracker.RaiseDomainEvent(new TestEvent());
        var createdEvents = DomainEventTracker.GetAndClearEvents();
        createdEvents.Should().NotBeEmpty();
    }

    [Fact(DisplayName = "Domain events raised inside of a scope are not accessible outside of that scope")]
    public async Task DomainEventsAreNotTrackedOutsideOfScopes()
    {
        using (var scope = await DomainEventTracker.CreateScope())
        {
            DomainEventTracker.RaiseDomainEvent(new TestEvent());
        }

        var createdEvents = DomainEventTracker.GetAndClearEvents();
        createdEvents.Should().BeEmpty();
    }

    [Fact(DisplayName = "Domain events raised inside of a nested scope are not accessible in the parent scope.")]
    public async Task DomainEventsRaisedInNestedScopesAreAccessibleInTheParentScope()
    {
        using (var scope = await DomainEventTracker.CreateScope())
        {
            DomainEventTracker.RaiseDomainEvent(new TestEvent("I was raised in the top scope."));
            using (var nestedScope = await DomainEventTracker.CreateScope())
            {
                DomainEventTracker.RaiseDomainEvent(new TestEvent("I was raised in the nested scope."));
                using (var evenMoreNestedScope = await DomainEventTracker.CreateScope())
                {
                    DomainEventTracker.RaiseDomainEvent(new TestEvent("I was raised in the deepest scope."));
                    DomainEventTracker.Peek().Should().HaveCount(1)
                        .And.Subject.Single().Should().BeOfType<TestEvent>()
                        .Which.Value.Should().Be("I was raised in the deepest scope.");
                }

                DomainEventTracker.RaiseDomainEvent(new TestEvent("I was also raised in the nested scope."));
                DomainEventTracker.Peek().Should().HaveCount(2)
                        .And.Subject.First().Should().BeOfType<TestEvent>()
                        .Which.Value.Should().Be("I was raised in the nested scope.");
                DomainEventTracker.Peek().Should().HaveCount(2)
                        .And.Subject.Last().Should().BeOfType<TestEvent>()
                        .Which.Value.Should().Be("I was also raised in the nested scope.");
            }

            DomainEventTracker.Peek().Should().HaveCount(1)
                        .And.Subject.Single().Should().BeOfType<TestEvent>()
                        .Which.Value.Should().Be("I was raised in the top scope.");
        }

        DomainEventTracker.Peek().Should().BeEmpty();
    }

    [Fact(DisplayName = "A higher scope disposed in a deeper scope will cascade disposing of all deeper scopes.")]
    public async Task DisposingAHigherScopeInADeeperScopeDisposesAllDeeperScope()
    {
        using var topScope = await DomainEventTracker.CreateScope();
        topScope.RaiseDomainEvent(new TestEvent("I was raised in the top scope."));

        using var deeperScope = await DomainEventTracker.CreateScope();
        deeperScope.RaiseDomainEvent(new TestEvent("I was raised in the deeper scope."));

        using var evenDeeperScope = await DomainEventTracker.CreateScope();
        evenDeeperScope.RaiseDomainEvent(new TestEvent("I was raised in the even deeper scope."));

        using var deepestScope = await DomainEventTracker.CreateScope();
        deepestScope.RaiseDomainEvent(new TestEvent("I was raised in the deepest scope."));

        deeperScope.Dispose();

        DomainEventTracker.Peek().Should().HaveCount(1)
                    .And.Subject.Single().Should().BeOfType<TestEvent>()
                    .Which.Value.Should().Be("I was raised in the top scope.");
    }

    [Fact(DisplayName = "Domain events raised inside a scope are not available outside of the scope in which they were raised in.")]
    public async Task DomainEventsInScopeAreNotAvailableOutsideOfScope()
    {
        await SubMethodThatRaisesDomainEventInOwnScope("I was raised in a deeper but synchronous method.");

        DomainEventTracker.Peek().Should().BeEmpty();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task SubMethodThatRaisesDomainEventInOwnScope(string contents)
    {
        var topScope = await DomainEventTracker.CreateScope();
        topScope.RaiseDomainEvent(new TestEvent(contents));
    }

    private sealed record class TestEvent(string Value = "") : IDomainEvent;
}
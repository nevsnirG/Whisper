using System.Runtime.CompilerServices;

namespace Whisper.UnitTests;

public class WhispersTests
{
    [Fact(DisplayName = "Peeking does not reset the current active implicit scope.")]
    public void PeekDoesNotClearDomainEvents()
    {
        Whispers.About(new TestEvent());
        Whispers.Peek();
        Whispers.Peek().Should().HaveCount(1);
    }

    [Fact(DisplayName = "GetAndClearEvents resets the current active implicit scope")]
    public void GetAndClearEventsClearsDomainEvents()
    {
        Whispers.About(new TestEvent());
        Whispers.GetAndClearEvents().Should().HaveCount(1);
        Whispers.GetAndClearEvents().Should().BeEmpty();
    }

    [Fact(DisplayName = "Domain events can be tracked even after clearing the scope")]
    public void GetAndClearEventsResetsScopeButScopeStaysAvailable()
    {
        Whispers.About(new TestEvent());
        Whispers.GetAndClearEvents();
        Whispers.About(new TestEvent());
        Whispers.GetAndClearEvents().Should().HaveCount(1);
    }

    [Fact(DisplayName = "Domain events raised inside of a scope are accessible inside of that scope")]
    public void DomainEventsAreTrackedInsideOfScopes()
    {
        using var scope = Whispers.CreateScope();
        Whispers.About(new TestEvent());
        var createdEvents = Whispers.GetAndClearEvents();
        createdEvents.Should().NotBeEmpty();
    }

    [Fact(DisplayName = "Domain events raised inside of a scope are not accessible outside of that scope")]
    public void DomainEventsAreNotTrackedOutsideOfScopes()
    {
        using (var scope = Whispers.CreateScope())
        {
            Whispers.About(new TestEvent());
        }

        var createdEvents = Whispers.GetAndClearEvents();
        createdEvents.Should().BeEmpty();
    }

    [Fact(DisplayName = "Domain events raised inside of a nested scope are not accessible in the parent scope.")]
    public void DomainEventsRaisedInNestedScopesAreNotAccessibleInTheParentScope()
    {
        using (var scope = Whispers.CreateScope())
        {
            Whispers.About(new TestEvent("I was raised in the top scope."));
            using (var nestedScope = Whispers.CreateScope())
            {
                Whispers.About(new TestEvent("I was raised in the nested scope."));
                using (var evenMoreNestedScope = Whispers.CreateScope())
                {
                    Whispers.About(new TestEvent("I was raised in the deepest scope."));
                    Whispers.Peek().Should().HaveCount(1)
                        .And.Subject.Single().Should().BeOfType<TestEvent>()
                        .Which.Value.Should().Be("I was raised in the deepest scope.");
                }

                Whispers.About(new TestEvent("I was also raised in the nested scope."));
                Whispers.Peek().Should().HaveCount(2)
                        .And.Subject.First().Should().BeOfType<TestEvent>()
                        .Which.Value.Should().Be("I was raised in the nested scope.");
                Whispers.Peek().Should().HaveCount(2)
                        .And.Subject.Last().Should().BeOfType<TestEvent>()
                        .Which.Value.Should().Be("I was also raised in the nested scope.");
            }

            Whispers.Peek().Should().HaveCount(1)
                        .And.Subject.Single().Should().BeOfType<TestEvent>()
                        .Which.Value.Should().Be("I was raised in the top scope.");
        }

        Whispers.Peek().Should().BeEmpty();
    }

    [Fact(DisplayName = "Disposing an outer scope while deeper scopes are open restores the collector that was active when it was created.")]
    public void DisposingAnOuterScopeRestoresTheCollectorActiveWhenItWasCreated()
    {
        using var topScope = Whispers.CreateScope();
        Whispers.About(new TestEvent("I was raised in the top scope."));

        using var deeperScope = Whispers.CreateScope();
        Whispers.About(new TestEvent("I was raised in the deeper scope."));

        using var evenDeeperScope = Whispers.CreateScope();
        Whispers.About(new TestEvent("I was raised in the even deeper scope."));

        using var deepestScope = Whispers.CreateScope();
        Whispers.About(new TestEvent("I was raised in the deepest scope."));

        deeperScope.Dispose();

        Whispers.Peek().Should().HaveCount(1)
                    .And.Subject.Single().Should().BeOfType<TestEvent>()
                    .Which.Value.Should().Be("I was raised in the top scope.");
    }

    [Fact(DisplayName = "A null domain event is rejected when raised instead of failing later at dispatch.")]
    public void AboutRejectsNullDomainEvent()
    {
        using var scope = Whispers.CreateScope();

        var act = () => Whispers.About(null!);

        act.Should().Throw<ArgumentNullException>();
        Whispers.Peek().Should().BeEmpty();
    }

    [Fact(DisplayName = "Raising a domain event while domain events are being dispatched throws and names the event.")]
    public void AboutThrowsWhileDispatching()
    {
        using var scope = Whispers.CreateScope();
        using var dispatching = Whispers.BeginDispatch();
        var raisedByAHandler = new TestEvent("I was raised by a handler.");

        var act = () => Whispers.About(raisedByAHandler);

        act.Should().Throw<DomainEventRaisedDuringDispatchException>()
            .Which.DomainEvent.Should().BeSameAs(raisedByAHandler);
        Whispers.Peek().Should().BeEmpty();
        Whispers.GetAndClearEvents().Should().BeEmpty();
    }

    [Fact(DisplayName = "A scope created while dispatching is a new use case and collects events again.")]
    public void ScopeCreatedWhileDispatchingCollectsEvents()
    {
        using var dispatching = Whispers.BeginDispatch();

        using (Whispers.CreateScope())
        {
            Whispers.About(new TestEvent("I was raised by a new use case."));

            Whispers.GetAndClearEvents().Should().ContainSingle();
        }

        var act = () => Whispers.About(new TestEvent("I was raised back in the dispatch."));
        act.Should().Throw<DomainEventRaisedDuringDispatchException>();
    }

    [Fact(DisplayName = "Ending the dispatch restores the scope it started in, with the events that scope already held.")]
    public void EndingTheDispatchRestoresTheSurroundingScope()
    {
        using var scope = Whispers.CreateScope();
        Whispers.About(new TestEvent("I was raised before the dispatch."));

        using (Whispers.BeginDispatch())
        {
        }

        Whispers.About(new TestEvent("I was raised after the dispatch."));
        Whispers.Peek().Should().HaveCount(2);
    }

    [Fact(DisplayName = "Domain events raised inside a scope are not available higher in the synchronous callstack outside of the scope in which they were raised.")]
    public void DomainEventsInScopeAreNotAvailableOutsideOfScope()
    {
        SubMethodThatRaisesDomainEventInOwnScope("I was raised in a deeper but synchronous method.");

        Whispers.GetAndClearEvents().Should().BeEmpty();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SubMethodThatRaisesDomainEventInOwnScope(string contents)
    {
        using var _ = Whispers.CreateScope();
        Whispers.About(new TestEvent(contents));
    }

    private sealed record class TestEvent(string Value = "") : IDomainEvent;
}
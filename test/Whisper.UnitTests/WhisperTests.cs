using System.Runtime.CompilerServices;

namespace Whisper.UnitTests;

public class WhisperTests
{
    [Fact(DisplayName = "Peeking does not reset the current active implicit scope.")]
    public void PeekDoesNotClearDomainEvents()
    {
        Whisper.About(new TestEvent());
        Whisper.Peek();
        Whisper.Peek().Should().HaveCount(1);
    }

    [Fact(DisplayName = "GetAndClearEvents resets the current active implicit scope")]
    public void GetAndClearEventsClearsDomainEvents()
    {
        Whisper.About(new TestEvent());
        Whisper.GetAndClearEvents().Should().HaveCount(1);
        Whisper.GetAndClearEvents().Should().BeEmpty();
    }

    [Fact(DisplayName = "Domain events can be tracked even after clearing the scope")]
    public void GetAndClearEventsResetsScopeButScopeStaysAvailable()
    {
        Whisper.About(new TestEvent());
        Whisper.GetAndClearEvents();
        Whisper.About(new TestEvent());
        Whisper.GetAndClearEvents().Should().HaveCount(1);
    }

    [Fact(DisplayName = "Domain events raised inside of a scope are accessible inside of that scope")]
    public void DomainEventsAreTrackedInsideOfScopes()
    {
        using var scope = Whisper.CreateScope();
        Whisper.About(new TestEvent());
        var createdEvents = Whisper.GetAndClearEvents();
        createdEvents.Should().NotBeEmpty();
    }

    [Fact(DisplayName = "Domain events raised inside of a scope are not accessible outside of that scope")]
    public void DomainEventsAreNotTrackedOutsideOfScopes()
    {
        using (var scope = Whisper.CreateScope())
        {
            Whisper.About(new TestEvent());
        }

        var createdEvents = Whisper.GetAndClearEvents();
        createdEvents.Should().BeEmpty();
    }

    [Fact(DisplayName = "Domain events raised inside of a nested scope are not accessible in the parent scope.")]
    public void DomainEventsRaisedInNestedScopesAreAccessibleInTheParentScope()
    {
        using (var scope = Whisper.CreateScope())
        {
            Whisper.About(new TestEvent("I was raised in the top scope."));
            using (var nestedScope = Whisper.CreateScope())
            {
                Whisper.About(new TestEvent("I was raised in the nested scope."));
                using (var evenMoreNestedScope = Whisper.CreateScope())
                {
                    Whisper.About(new TestEvent("I was raised in the deepest scope."));
                    Whisper.Peek().Should().HaveCount(1)
                        .And.Subject.Single().Should().BeOfType<TestEvent>()
                        .Which.Value.Should().Be("I was raised in the deepest scope.");
                }

                Whisper.About(new TestEvent("I was also raised in the nested scope."));
                Whisper.Peek().Should().HaveCount(2)
                        .And.Subject.First().Should().BeOfType<TestEvent>()
                        .Which.Value.Should().Be("I was raised in the nested scope.");
                Whisper.Peek().Should().HaveCount(2)
                        .And.Subject.Last().Should().BeOfType<TestEvent>()
                        .Which.Value.Should().Be("I was also raised in the nested scope.");
            }

            Whisper.Peek().Should().HaveCount(1)
                        .And.Subject.Single().Should().BeOfType<TestEvent>()
                        .Which.Value.Should().Be("I was raised in the top scope.");
        }

        Whisper.Peek().Should().BeEmpty();
    }

    [Fact(DisplayName = "A higher scope disposed in a deeper scope will cascade disposing of all deeper scopes.")]
    public void DisposingAHigherScopeInADeeperScopeDisposesAllDeeperScope()
    {
        using var topScope = Whisper.CreateScope();
        Whisper.About(new TestEvent("I was raised in the top scope."));

        using var deeperScope = Whisper.CreateScope();
        Whisper.About(new TestEvent("I was raised in the deeper scope."));

        using var evenDeeperScope = Whisper.CreateScope();
        Whisper.About(new TestEvent("I was raised in the even deeper scope."));

        using var deepestScope = Whisper.CreateScope();
        Whisper.About(new TestEvent("I was raised in the deepest scope."));

        deeperScope.Dispose();

        Whisper.Peek().Should().HaveCount(1)
                    .And.Subject.Single().Should().BeOfType<TestEvent>()
                    .Which.Value.Should().Be("I was raised in the top scope.");
    }

    [Fact(DisplayName = "Domain events raised inside a scope are not available higher in the synchronous callstack outside of the scope in which they were raised.")]
    public void DomainEventsInScopeAreNotAvailableOutsideOfScope()
    {
        SubMethodThatRaisesDomainEventInOwnScope("I was raised in a deeper but synchronous method.");

        Whisper.GetAndClearEvents().Should().BeEmpty();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SubMethodThatRaisesDomainEventInOwnScope(string contents)
    {
        using var _ = Whisper.CreateScope();
        Whisper.About(new TestEvent(contents));
    }

    private sealed record class TestEvent(string Value = "") : IDomainEvent;
}
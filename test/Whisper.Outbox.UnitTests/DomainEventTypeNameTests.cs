namespace Whisper.Outbox.UnitTests;

public class DomainEventTypeNameTests
{
    [Fact]
    public void Of_LeavesOutAssemblyVersionCultureAndPublicKeyToken()
    {
        var name = DomainEventTypeName.Of(typeof(PlainEvent));

        name.Should().Be("Whisper.Outbox.UnitTests.DomainEventTypeNameTests+PlainEvent, Whisper.Outbox.UnitTests");
    }

    [Fact]
    public void Of_GenericType_LeavesThemOutOfTheTypeArgumentsToo()
    {
        var name = DomainEventTypeName.Of(typeof(GenericEvent<PlainEvent>));

        name.Should().Be(
            "Whisper.Outbox.UnitTests.DomainEventTypeNameTests+GenericEvent`1" +
            "[[Whisper.Outbox.UnitTests.DomainEventTypeNameTests+PlainEvent, Whisper.Outbox.UnitTests]], Whisper.Outbox.UnitTests");
    }

    [Theory]
    [InlineData(typeof(PlainEvent))]
    [InlineData(typeof(GenericEvent<PlainEvent>))]
    [InlineData(typeof(GenericEvent<Guid>))]
    public void Resolve_NameFromOf_ReturnsTheType(Type type)
    {
        DomainEventTypeName.Resolve(DomainEventTypeName.Of(type)).Should().Be(type);
    }

    // Records written before the name lost its version carry the full assembly-qualified name.
    [Theory]
    [InlineData(typeof(PlainEvent))]
    [InlineData(typeof(GenericEvent<PlainEvent>))]
    public void Resolve_FullAssemblyQualifiedName_ReturnsTheType(Type type)
    {
        DomainEventTypeName.Resolve(type.AssemblyQualifiedName!).Should().Be(type);
    }

    // What a rollback or a rolling deployment produces: the record was written by a newer build of the assembly.
    [Fact]
    public void Resolve_NameWithANewerAssemblyVersion_ReturnsTheType()
    {
        var writtenByANewerBuild = typeof(PlainEvent).AssemblyQualifiedName!.Replace(
            $"Version={typeof(PlainEvent).Assembly.GetName().Version}", "Version=99.0.0.0");

        writtenByANewerBuild.Should().Contain("Version=99.0.0.0");
        DomainEventTypeName.Resolve(writtenByANewerBuild).Should().Be(typeof(PlainEvent));
    }

    [Theory]
    [InlineData("Whisper.Outbox.UnitTests.RenamedEvent, Whisper.Outbox.UnitTests")]
    [InlineData("Some.Event, Some.Assembly.That.Does.Not.Exist")]
    [InlineData("Some.Event, Some.Assembly.That.Does.Not.Exist, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")]
    public void Resolve_TypeThisApplicationCannotLoad_ThrowsUnknownDomainEventTypeException(string storedName)
    {
        var act = () => DomainEventTypeName.Resolve(storedName);

        act.Should().Throw<UnknownDomainEventTypeException>()
            .Which.TypeName.Should().Be(storedName);
    }

    internal sealed record PlainEvent : IDomainEvent;

    internal sealed record GenericEvent<T>(T Value) : IDomainEvent;
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Whisper.Outbox.UnitTests;

public class DomainEventSerializerTests
{
    private static DomainEventSerializer CreateSerializer(Action<OutboxJsonOptions>? configure = null)
    {
        var options = new OutboxJsonOptions();
        configure?.Invoke(options);
        return new DomainEventSerializer(Options.Create(options));
    }

    [Fact]
    public void Serialize_And_Deserialize_RoundTrips_Correctly()
    {
        var sut = CreateSerializer();
        var original = new TestEvent("hello", 42);

        var json = sut.Serialize(original);
        var result = sut.Deserialize(json, typeof(TestEvent).AssemblyQualifiedName!);

        result.Should().BeOfType<TestEvent>()
            .Which.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Deserialize_WithInvalidTypeName_ThrowsUnknownDomainEventTypeException()
    {
        var sut = CreateSerializer();

        var act = () => sut.Deserialize("{}", "NonExistent.Type, NonExistent.Assembly");

        act.Should().Throw<UnknownDomainEventTypeException>();
    }

    [Fact]
    public void Deserialize_PayloadThatIsJsonNull_Throws()
    {
        var sut = CreateSerializer();

        var act = () => sut.Deserialize("null", typeof(TestEvent).AssemblyQualifiedName!);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Deserialize_WithTypeThatDoesNotImplementIDomainEvent_ThrowsInvalidOperationException()
    {
        var sut = CreateSerializer();
        var typeName = typeof(NotADomainEvent).AssemblyQualifiedName!;

        var act = () => sut.Deserialize("{}", typeName);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{typeName}*is not assignable*");
    }

    [Fact]
    public void Serialize_PreservesPolymorphicType()
    {
        var sut = CreateSerializer();
        var original = new TestEvent("test", 99);

        var json = sut.Serialize(original);

        json.Should().Contain("\"Value\":\"test\"")
            .And.Contain("\"Number\":99");
    }

    [Fact]
    public void Serialize_And_Deserialize_WithCustomConverter_Works()
    {
        var sut = CreateSerializer(o => o.Converters.Add(new CustomValueConverter()));
        var original = new EventWithCustomValue(new CustomValue(123));

        var json = sut.Serialize(original);
        var result = sut.Deserialize(json, typeof(EventWithCustomValue).AssemblyQualifiedName!);

        result.Should().BeOfType<EventWithCustomValue>()
            .Which.Custom.Inner.Should().Be(123);
    }

    [Fact]
    public void RoundTrips_PublicFields()
    {
        var original = new EventWithFields { OrderId = Guid.NewGuid(), Note = "first" };

        var result = RoundTrip(original);

        result.OrderId.Should().Be(original.OrderId);
        result.Note.Should().Be("first");
    }

    [Fact]
    public void RoundTrips_PropertiesWithPrivateAndProtectedSetters()
    {
        var original = EventWithNonPublicSetters.Create(Guid.NewGuid(), 7);

        var result = RoundTrip(original);

        result.OrderId.Should().Be(original.OrderId);
        result.Quantity.Should().Be(7);
    }

    [Fact]
    public void RoundTrips_PrivateSetterDeclaredInBaseClass()
    {
        var original = DerivedEvent.Create(Guid.NewGuid(), "derived");

        var result = RoundTrip(original);

        result.AggregateId.Should().Be(original.AggregateId);
        result.Name.Should().Be("derived");
    }

    [Fact]
    public void RoundTrips_ConstructorParameterWithPrivateSetter()
    {
        var original = new EventWithConstructor(Guid.NewGuid());

        var result = RoundTrip(original);

        result.OrderId.Should().Be(original.OrderId);
    }

    [Fact]
    public void RoundTrips_InitOnlyProperties()
    {
        var original = new EventWithInit { OrderId = Guid.NewGuid() };

        var result = RoundTrip(original);

        result.OrderId.Should().Be(original.OrderId);
    }

    [Fact]
    public void SerializerOptions_AreUsedForSerialization()
    {
        var sut = CreateSerializer(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        var original = new TestEvent("camel", 1);

        var json = sut.Serialize(original);
        var result = sut.Deserialize(json, typeof(TestEvent).AssemblyQualifiedName!);

        json.Should().Contain("\"value\":\"camel\"");
        result.Should().BeEquivalentTo(original);
    }

    private static T RoundTrip<T>(T domainEvent) where T : IDomainEvent
    {
        var sut = CreateSerializer();
        var json = sut.Serialize(domainEvent);
        return sut.Deserialize(json, typeof(T).AssemblyQualifiedName!).Should().BeOfType<T>().Subject;
    }

    internal record TestEvent(string Value, int Number) : IDomainEvent;

    internal sealed class EventWithFields : IDomainEvent
    {
        public Guid OrderId;
        public string? Note;
    }

    internal class EventWithNonPublicSetters : IDomainEvent
    {
        public Guid OrderId { get; private set; }
        public int Quantity { get; protected set; }

        public static EventWithNonPublicSetters Create(Guid orderId, int quantity) => new() { OrderId = orderId, Quantity = quantity };
    }

    internal abstract class AggregateEvent : IDomainEvent
    {
        public Guid AggregateId { get; private set; }

        protected void SetAggregateId(Guid aggregateId) => AggregateId = aggregateId;
    }

    internal sealed class DerivedEvent : AggregateEvent
    {
        public string Name { get; private set; } = "";

        public static DerivedEvent Create(Guid aggregateId, string name)
        {
            var domainEvent = new DerivedEvent { Name = name };
            domainEvent.SetAggregateId(aggregateId);
            return domainEvent;
        }
    }

    internal sealed class EventWithConstructor(Guid orderId) : IDomainEvent
    {
        public Guid OrderId { get; private set; } = orderId;
    }

    internal sealed class EventWithInit : IDomainEvent
    {
        public Guid OrderId { get; init; }
    }

    internal class NotADomainEvent { }

    internal readonly record struct CustomValue(int Inner);

    internal record EventWithCustomValue(CustomValue Custom) : IDomainEvent;

    internal sealed class CustomValueConverter : JsonConverter<CustomValue>
    {
        public override CustomValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(reader.GetInt32());

        public override void Write(Utf8JsonWriter writer, CustomValue value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value.Inner);
    }
}

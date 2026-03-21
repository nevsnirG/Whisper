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
    public void Deserialize_WithInvalidTypeName_Throws()
    {
        var sut = CreateSerializer();

        var act = () => sut.Deserialize("{}", "NonExistent.Type, NonExistent.Assembly");

        act.Should().Throw<Exception>();
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

    internal record TestEvent(string Value, int Number) : IDomainEvent;

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

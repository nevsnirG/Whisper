using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Whisper.Outbox;

/// <summary>
/// The type name stored on an outbox record: the assembly-qualified name without assembly version, culture and
/// public key token ("Namespace.Type, Assembly"). A name with a version only loads while the deployed assembly is at
/// least that version, so records written by a newer build would fail on an older one during a rolling deployment or
/// after a rollback.
/// </summary>
internal static partial class DomainEventTypeName
{
    private static readonly ConcurrentDictionary<string, Type> Resolved = new();

    public static string Of(Type type)
    {
        return WithoutAssemblyVersions(type.AssemblyQualifiedName!);
    }

    /// <summary>
    /// Loads the type a record names. Names written by earlier versions carry assembly versions; those are ignored.
    /// </summary>
    /// <exception cref="UnknownDomainEventTypeException">The type cannot be loaded by this application.</exception>
    public static Type Resolve(string storedName)
    {
        if (Resolved.TryGetValue(storedName, out var known))
            return known;

        Type type;
        try
        {
            type = Type.GetType(WithoutAssemblyVersions(storedName), throwOnError: true)!;
        }
        catch (Exception ex) when (ex is TypeLoadException or IOException or BadImageFormatException or ArgumentException)
        {
            throw new UnknownDomainEventTypeException(storedName, ex);
        }

        return Resolved.GetOrAdd(storedName, type);
    }

    private static string WithoutAssemblyVersions(string assemblyQualifiedName)
    {
        return AssemblyVersionParts().Replace(assemblyQualifiedName, string.Empty);
    }

    // Also matches inside the brackets of a generic type's arguments, which are assembly-qualified names themselves.
    [GeneratedRegex(@", (?:Version|Culture|PublicKeyToken)=[^,\]]+")]
    private static partial Regex AssemblyVersionParts();
}

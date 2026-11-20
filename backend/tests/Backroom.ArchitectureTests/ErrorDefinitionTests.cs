using System.Reflection;
using AwesomeAssertions;
using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.ArchitectureTests;

/// <summary>
/// Error codes are validated when an <see cref="Error"/> is created. Reading every static Error field
/// and property here turns a malformed code into a failing test instead of a runtime type-load error.
/// </summary>
public sealed class ErrorDefinitionTests
{
    [Fact]
    public void StaticErrors_WhenLoaded_HaveValidCodes()
    {
        const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var failures = new List<string>();
        var count = 0;

        foreach (var type in SolutionAssemblies.All.SelectMany(assembly => assembly.GetTypes()))
        {
            var members = type.GetFields(Flags).Where(field => field.FieldType == typeof(Error)).Cast<MemberInfo>()
                .Concat(type.GetProperties(Flags).Where(property => property.PropertyType == typeof(Error) && property.GetIndexParameters().Length == 0));

            foreach (var member in members)
            {
                try
                {
                    var value = member switch
                    {
                        FieldInfo field => field.GetValue(null),
                        PropertyInfo property => property.GetValue(null),
                        _ => null,
                    };
                    count += value is Error ? 1 : 0;
                }
                catch (TargetInvocationException exception)
                {
                    failures.Add($"{type.FullName}.{member.Name}: {exception.InnerException?.Message}");
                }
                catch (TypeInitializationException exception)
                {
                    failures.Add($"{type.FullName}.{member.Name}: {exception.InnerException?.Message}");
                }
            }
        }

        failures.Should().BeEmpty();
        count.Should().BePositive("at least Result.Uninitialized is defined");
    }
}

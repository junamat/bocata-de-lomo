namespace RefereeHubApi.VisualTests.Framework;

// a method that adds steps; shows in the browser as a label with its steps under it ("Test" prefix dropped).
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute;

// the same, once per argument set: shows as Name(args).
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class TestCaseAttribute(params object?[] arguments) : Attribute
{
    public object?[] Arguments { get; } = arguments;
}

// adds steps that run before every test, base class ones first.
[AttributeUsage(AttributeTargets.Method)]
public sealed class SetUpStepsAttribute : Attribute;

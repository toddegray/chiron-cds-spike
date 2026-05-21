namespace Chiron.Cds.Engine.Primitives;

/// <summary>
/// Signature every rule implements: take an <see cref="EvaluationContext"/>,
/// return an <see cref="Alert"/> if the rule fires or <c>null</c> if it
/// doesn't apply.
/// </summary>
public delegate Alert? RuleFunc(EvaluationContext ctx);

/// <summary>
/// Registered rule metadata plus its <see cref="Evaluate"/> delegate. The
/// engine refuses to register a rule whose <see cref="Citations"/> list is
/// empty — every clinical claim must point to a source.
/// </summary>
public sealed record Rule(
    string Id,
    string Description,
    RuleFunc Evaluate,
    IReadOnlyList<Citation> Citations);

/// <summary>
/// Marks an assembly as containing rule packs. <see cref="Engine"/>'s
/// <c>RegisterPack(Assembly)</c> overload looks for static classes inside
/// these assemblies that expose static <c>IEnumerable&lt;Rule&gt; Rules</c>
/// properties.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RulePackAttribute : Attribute
{
    public string? Name { get; init; }
}

// Per-agent protocol rules. New agent = new subclass + entry Program.cs; core stays unchanged.
public abstract class AgentRules
{
    public abstract string Name { get; }

    // false → raw passthrough, no translation.
    public abstract bool ShouldTranslate(string[] args);

    // true → value and all descendants are copied verbatim.
    // parentName = member name of the enclosing object ("" at root); array elements inherit the array's name.
    public abstract bool IsOpaqueField(string name, string parentName);
}

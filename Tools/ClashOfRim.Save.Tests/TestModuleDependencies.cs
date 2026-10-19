using ClashOfRim.TestSupport;
using static SnapshotTestAssertions;

internal static class TestModuleDependencies
{
    public static void RequireIndependent(params Type[] roots)
    {
        Type Root(Type type) => type.DeclaringType is { } parent ? Root(parent) : type;
        foreach (var root in roots)
        {
            bool OtherTest(Type dependency)
            {
                var owner = Root(dependency);
                return owner.Assembly == root.Assembly && owner != root
                    && owner.Name.EndsWith("Tests", StringComparison.Ordinal);
            }
            var violations = ModuleDependencyGuard.FindViolations(root.Assembly.GetTypes().Where(type => Root(type) == root), OtherTest);
            Check(violations.Count == 0, string.Join(Environment.NewLine, violations));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace ClashOfRim.TestSupport;

// Inspect compiled references, including generic fields and compiler-generated async bodies.
// This is an architectural regression check, not a sandbox for reflection/dynamic code.
internal static class ModuleDependencyGuard
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(op => op.Value);

    public static IReadOnlyList<string> FindViolations(IEnumerable<Type> types, Func<Type, bool> forbidden)
    {
        var failures = new HashSet<string>(StringComparer.Ordinal);
        foreach (Type type in types)
        {
            void Check(Type? dependency, string member)
            {
                if (dependency is null) return;
                if (forbidden(dependency)) failures.Add($"{type.FullName}.{member} depends on {dependency.FullName}");
                if (dependency.HasElementType) Check(dependency.GetElementType(), member);
                if (dependency.IsGenericType) foreach (Type argument in dependency.GetGenericArguments()) Check(argument, member);
            }
            Check(type.BaseType, "base");
            foreach (Type contract in type.GetInterfaces()) Check(contract, "interface");
            foreach (FieldInfo field in type.GetFields(Members)) Check(field.FieldType, field.Name);
            foreach (PropertyInfo property in type.GetProperties(Members)) Check(property.PropertyType, property.Name);
            foreach (EventInfo evt in type.GetEvents(Members)) Check(evt.EventHandlerType, evt.Name);
            foreach (MethodBase method in type.GetMethods(Members).Cast<MethodBase>().Concat(type.GetConstructors(Members)))
            {
                if (method is MethodInfo info) Check(info.ReturnType, method.Name);
                foreach (ParameterInfo parameter in method.GetParameters()) Check(parameter.ParameterType, method.Name);
                MethodBody? body = method.GetMethodBody();
                if (body is null) continue;
                foreach (LocalVariableInfo local in body.LocalVariables) Check(local.LocalType, method.Name);
                foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
                    if (clause.Flags == ExceptionHandlingClauseOptions.Clause) Check(clause.CatchType, method.Name);
                foreach (MemberInfo referenced in ReadReferences(method, body.GetILAsByteArray()!))
                {
                    Check(referenced as Type ?? referenced.DeclaringType, method.Name);
                    if (referenced is FieldInfo f) Check(f.FieldType, method.Name);
                    if (referenced is MethodBase called)
                    {
                        if (called is MethodInfo callInfo) Check(callInfo.ReturnType, method.Name);
                        foreach (ParameterInfo p in called.GetParameters()) Check(p.ParameterType, method.Name);
                        if (called.IsGenericMethod) foreach (Type a in called.GetGenericArguments()) Check(a, method.Name);
                    }
                }
            }
        }
        return failures.Order(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<MemberInfo> ReadReferences(MethodBase method, byte[] il)
    {
        int at = 0;
        while (at < il.Length)
        {
            short value = il[at++];
            if (value == 0xfe) value = unchecked((short)(0xfe00 | il[at++]));
            OperandType operand = Codes[value].OperandType;
            if (operand is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineTok)
            {
                MemberInfo? member = method.Module.ResolveMember(BitConverter.ToInt32(il, at),
                    method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (member is not null) yield return member;
            }
            at += operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, at),
                _ => 4
            };
        }
    }
}

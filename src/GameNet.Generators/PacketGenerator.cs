using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace GameNet.Generators;

/// <summary>
/// Emits a version-tolerant codec for [Packet] types. Wire format is a sequence of fields:
/// [id varint][type tag][length varint][payload]. Readers skip unknown ids and default missing ones,
/// so appending a field is backward compatible and renaming a member is wire-neutral.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class PacketGenerator : IIncrementalGenerator
{
    private const string PacketAttribute = "GameNet.Core.PacketAttribute";
    private const string FieldAttribute = "GameNet.Core.FieldAttribute";
    private const string EntityAttribute = "GameNet.Core.EntityAttribute";

    private static readonly DiagnosticDescriptor UnsupportedType = new(
        "GEN002", "Unsupported packet field type",
        "Field '{0}' of '{1}' has unsupported type '{2}'", "GameNet", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor NoParameterlessCtor = new(
        "GEN003", "Packet type needs a parameterless constructor",
        "'{0}' must have a public parameterless constructor", "GameNet", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor BadFieldOrder = new(
        "GEN004", "Invalid [Field] order",
        "'{0}' has duplicate or non-positive field order {1}", "GameNet", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor TooManyFields = new(
        "GEN005", "Too many packet fields",
        "'{0}' has {1} fields; at most 64 are supported (one ulong mask)", "GameNet", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor InternalError = new(
        "GEN099", "Packet generator failed",
        "Generating codec for '{0}' failed: {1}", "GameNet", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var packets = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                PacketAttribute,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => ctx.TargetSymbol as INamedTypeSymbol)
            .Where(static s => s is not null)
            .Collect();

        var entities = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                EntityAttribute,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => ctx.TargetSymbol as INamedTypeSymbol)
            .Where(static s => s is not null)
            .Collect();

        context.RegisterSourceOutput(packets.Combine(entities), static (spc, pair) =>
        {
            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var symbol in pair.Left.Concat(pair.Right))
            {
                if (symbol is null || !seen.Add(symbol)) continue;
                try
                {
                    Emit(spc, symbol);
                }
                catch (System.Exception ex)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(InternalError, symbol.Locations.FirstOrDefault(),
                        symbol.Name, ex.GetType().Name + ": " + ex.Message));
                }
            }
        });
    }

    private sealed class FieldModel
    {
        public int Order;
        public byte Tag;
        public string Name = "";
        public string TypeName = "";
        public string Write = "";
        public string Read = "";
        public string Diff = "";
    }

    private static void Emit(SourceProductionContext context, INamedTypeSymbol symbol)
    {
        if (symbol.IsGenericType || symbol.ContainingType is not null) return;

        var members = symbol.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public && p.SetMethod is not null)
            .OrderBy(p => p.Locations.FirstOrDefault()?.SourceSpan.Start ?? int.MaxValue)
            .ToList();
        if (members.Count == 0) return;

        var explicitOrders = members
            .Select(m => m.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == FieldAttribute))
            .Select(a => a is not null && a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Value is int i ? i : (int?)null)
            .ToList();
        var allExplicit = explicitOrders.All(o => o.HasValue);

        var fields = new List<FieldModel>();
        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index];
            var model = new FieldModel
            {
                Order = allExplicit ? explicitOrders[index]!.Value : index + 1,
                Name = member.Name,
                TypeName = member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            };

            try
            {
                BuildCodec(context, symbol, model, member.Type);
            }
            catch (UnsupportedFieldException)
            {
                return;
            }
            fields.Add(model);
        }

        if (fields.Count > 64)
        {
            context.ReportDiagnostic(Diagnostic.Create(TooManyFields, symbol.Locations.FirstOrDefault(), symbol.Name, fields.Count));
            return;
        }
        if (fields.Select(f => f.Order).Distinct().Count() != fields.Count || fields.Any(f => f.Order <= 0))
        {
            var bad = fields.GroupBy(f => f.Order).First(g => g.Count() > 1).Key;
            context.ReportDiagnostic(Diagnostic.Create(BadFieldOrder, symbol.Locations.FirstOrDefault(), symbol.Name, bad));
            return;
        }

        var hasParameterlessCtor = symbol.InstanceConstructors.Any(c =>
            c.Parameters.Length == 0 && c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal);
        if (!hasParameterlessCtor && !symbol.IsValueType)
        {
            context.ReportDiagnostic(Diagnostic.Create(NoParameterlessCtor, symbol.Locations.FirstOrDefault(), symbol.Name));
            return;
        }

        fields.Sort((a, b) => a.Order.CompareTo(b.Order));
        var typeName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var codecName = symbol.Name + "PacketCodec";
        var ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? "GameNet.Generated"
            : "GameNet.Generated." + symbol.ContainingNamespace.ToDisplayString();

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("using System;");
        builder.AppendLine("using GameNet.Core;");
        builder.AppendLine($"namespace {ns};");
        builder.AppendLine();
        builder.AppendLine($"public static class {codecName}");
        builder.AppendLine("{");
        builder.AppendLine($"    public const ulong AllFields = {AllFieldsMask(fields)}UL;");
        builder.AppendLine($"    public const string SchemaHash = \"{SchemaHash(fields)}\";");
        builder.AppendLine();

        builder.AppendLine($"    public static void Write(ref ByteWriter w, {typeName} value)");
        builder.AppendLine("    {");
        builder.AppendLine("        var tmp = new ByteWriter(32);");
        foreach (var field in fields)
        {
            builder.AppendLine("        tmp.Reset();");
            builder.AppendLine("        " + field.Write.Replace("w.", "tmp."));
            builder.AppendLine($"        w.WriteVarUInt({field.Order});");
            builder.AppendLine($"        w.WriteByte({field.Tag});");
            builder.AppendLine("        w.WriteVarUInt((uint)tmp.Length);");
            builder.AppendLine("        w.WriteBytes(tmp.Written);");
        }
        builder.AppendLine("    }");
        builder.AppendLine();

        builder.AppendLine($"    public static {typeName} Read(ref ByteReader r)");
        builder.AppendLine("    {");
        builder.AppendLine(symbol.IsValueType
            ? $"        var result = default({typeName});"
            : $"        var result = new {typeName}();");
        builder.AppendLine("        while (!r.End)");
        builder.AppendLine("        {");
        builder.AppendLine("            var id = r.ReadVarUInt();");
        builder.AppendLine("            var tag = r.ReadByte();");
        builder.AppendLine("            var length = (int)r.ReadVarUInt();");
        builder.AppendLine("            var payload = r.ReadBytes(length);");
        builder.AppendLine("            switch (id)");
        builder.AppendLine("            {");
        foreach (var field in fields)
        {
            builder.AppendLine($"                case {field.Order}:");
            builder.AppendLine($"                    if (tag != {field.Tag}) throw new InvalidOperationException(\"field {field.Name} of {symbol.Name} expects tag {field.Tag} but got \" + tag);");
            builder.AppendLine("                    {");
            builder.AppendLine("                        var fr = new ByteReader(payload);");
            if (symbol.IsRecord)
                builder.AppendLine($"                        result = result with {{ {field.Name} = {field.Read.Replace("r.", "fr.")} }};");
            else
                builder.AppendLine($"                        result.{field.Name} = {field.Read.Replace("r.", "fr.")};");
            builder.AppendLine("                    }");
            builder.AppendLine("                    break;");
        }
        builder.AppendLine("                default:");
        builder.AppendLine("                    break; // unknown field from a newer writer");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("        return result;");
        builder.AppendLine("    }");
        builder.AppendLine();

        builder.AppendLine($"    public static ulong DiffMask({typeName} a, {typeName} b)");
        builder.AppendLine("    {");
        builder.AppendLine("        ulong mask = 0;");
        foreach (var field in fields) builder.AppendLine($"        if ({field.Diff}) mask |= {Bit(field.Order)};");
        builder.AppendLine("        return mask;");
        builder.AppendLine("    }");
        builder.AppendLine();

        builder.AppendLine($"    public static void WriteDelta(ref ByteWriter w, {typeName} value, ulong mask)");
        builder.AppendLine("    {");
        foreach (var field in fields)
            builder.AppendLine($"        if ((mask & {Bit(field.Order)}) != 0) {{ {field.Write} }}");
        builder.AppendLine("    }");
        builder.AppendLine();

        builder.AppendLine($"    public static {typeName} ApplyDelta(ref ByteReader r, {typeName} target, ulong mask)");
        builder.AppendLine("    {");
        if (symbol.IsRecord)
        {
            builder.AppendLine("        return target with");
            builder.AppendLine("        {");
            foreach (var field in fields)
                builder.AppendLine($"            {field.Name} = (mask & {Bit(field.Order)}) != 0 ? {field.Read} : target.{field.Name},");
            builder.AppendLine("        };");
        }
        else if (symbol.IsValueType)
        {
            builder.AppendLine("        var result = target;");
            foreach (var field in fields)
                builder.AppendLine($"        if ((mask & {Bit(field.Order)}) != 0) result.{field.Name} = {field.Read};");
            builder.AppendLine("        return result;");
        }
        else
        {
            foreach (var field in fields)
                builder.AppendLine($"        if ((mask & {Bit(field.Order)}) != 0) target.{field.Name} = {field.Read};");
            builder.AppendLine("        return target;");
        }
        builder.AppendLine("    }");
        builder.AppendLine("}");

        context.AddSource(codecName + ".g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));

        if (!IsPartial(symbol)) return;
        var wire = new StringBuilder();
        wire.AppendLine("// <auto-generated/>");
        wire.AppendLine("#nullable enable");
        if (!symbol.ContainingNamespace.IsGlobalNamespace)
            wire.AppendLine($"namespace {symbol.ContainingNamespace.ToDisplayString()};");
        wire.AppendLine();
        var modifiers = string.Empty;
        if (symbol.IsValueType && symbol.IsReadOnly) modifiers += "readonly ";
        if (!symbol.IsValueType && symbol.IsSealed) modifiers += "sealed ";
        var kind = symbol.IsRecord
            ? (symbol.IsValueType ? "record struct" : "record")
            : (symbol.IsValueType ? "struct" : "class");
        wire.AppendLine($"{modifiers}partial {kind} {symbol.Name} : global::GameNet.Core.IWireCodec<{typeName}>, global::GameNet.Core.IEntityCodec<{typeName}>");
        wire.AppendLine("{");
        wire.AppendLine($"    public static void Write(global::GameNet.Core.ByteWriter writer, {typeName} value) => {ns}.{codecName}.Write(ref writer, value);");
        wire.AppendLine($"    public static {typeName} Read(ref global::GameNet.Core.ByteReader reader) => {ns}.{codecName}.Read(ref reader);");
        wire.AppendLine($"    public static ulong DiffMask({typeName} a, {typeName} b) => {ns}.{codecName}.DiffMask(a, b);");
        wire.AppendLine($"    public static void WriteDelta(global::GameNet.Core.ByteWriter writer, {typeName} value, ulong mask) => {ns}.{codecName}.WriteDelta(ref writer, value, mask);");
        wire.AppendLine($"    public static {typeName} ApplyDelta(ref global::GameNet.Core.ByteReader reader, {typeName} target, ulong mask) => {ns}.{codecName}.ApplyDelta(ref reader, target, mask);");
        wire.AppendLine("}");
        context.AddSource(codecName + ".wire.g.cs", SourceText.From(wire.ToString(), Encoding.UTF8));
    }

    private static bool IsPartial(INamedTypeSymbol symbol) =>
        symbol.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is TypeDeclarationSyntax declaration &&
            declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));

    private sealed class UnsupportedFieldException : System.Exception
    {
    }

    private static void BuildCodec(SourceProductionContext context, INamedTypeSymbol owner, FieldModel model, ITypeSymbol type)
    {
        void Unsupported()
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedType, owner.Locations.FirstOrDefault(),
                model.Name, owner.Name, type.ToDisplayString()));
            throw new UnsupportedFieldException();
        }

        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
        {
            model.Tag = 12;
            model.Write = $"w.WriteVarUInt((uint)(value.{model.Name}?.Length ?? 0)); w.WriteBytes(value.{model.Name} ?? Array.Empty<byte>());";
            model.Read = "r.ReadBytes((int)r.ReadVarUInt()).ToArray()";
            model.Diff = $"!System.MemoryExtensions.SequenceEqual(a.{model.Name}.AsSpan(), b.{model.Name}.AsSpan())";
            return;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            model.Tag = 11;
            model.Write = $"w.WriteVarInt((int)value.{model.Name});";
            model.Read = $"({model.TypeName})r.ReadVarInt()";
            model.Diff = $"a.{model.Name} != b.{model.Name}";
            return;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
                model.Tag = 1;
                model.Write = $"w.WriteBool(value.{model.Name});";
                model.Read = "r.ReadBool()";
                model.Diff = $"a.{model.Name} != b.{model.Name}";
                return;
            case SpecialType.System_Byte:
                model.Tag = 2;
                model.Write = $"w.WriteByte(value.{model.Name});";
                model.Read = "r.ReadByte()";
                model.Diff = $"a.{model.Name} != b.{model.Name}";
                return;
            case SpecialType.System_Int16:
                model.Tag = 3;
                model.Write = $"w.WriteVarInt(value.{model.Name});";
                model.Read = "(short)r.ReadVarInt()";
                model.Diff = $"a.{model.Name} != b.{model.Name}";
                return;
            case SpecialType.System_Int32:
                model.Tag = 4;
                model.Write = $"w.WriteVarInt(value.{model.Name});";
                model.Read = "r.ReadVarInt()";
                model.Diff = $"a.{model.Name} != b.{model.Name}";
                return;
            case SpecialType.System_Int64:
                model.Tag = 5;
                model.Write = $"w.WriteUInt((uint)(value.{model.Name} >> 32)); w.WriteUInt((uint)value.{model.Name});";
                model.Read = "(long)(((ulong)r.ReadUInt() << 32) | r.ReadUInt())";
                model.Diff = $"a.{model.Name} != b.{model.Name}";
                return;
            case SpecialType.System_Single:
                model.Tag = 6;
                model.Write = $"w.WriteFloat(value.{model.Name});";
                model.Read = "r.ReadFloat()";
                model.Diff = $"BitConverter.SingleToInt32Bits(a.{model.Name}) != BitConverter.SingleToInt32Bits(b.{model.Name})";
                return;
            case SpecialType.System_Double:
                model.Tag = 7;
                model.Write = $"w.WriteUInt((uint)BitConverter.DoubleToInt64Bits(value.{model.Name})); w.WriteUInt((uint)(BitConverter.DoubleToInt64Bits(value.{model.Name}) >> 32));";
                model.Read = "BitConverter.Int64BitsToDouble((long)(((ulong)r.ReadUInt() << 32) | r.ReadUInt()))";
                model.Diff = $"BitConverter.DoubleToInt64Bits(a.{model.Name}) != BitConverter.DoubleToInt64Bits(b.{model.Name})";
                return;
            case SpecialType.System_String:
                model.Tag = 8;
                model.Write = $"w.WriteVarUInt((uint)System.Text.Encoding.UTF8.GetByteCount(value.{model.Name} ?? System.String.Empty)); w.WriteBytes(System.Text.Encoding.UTF8.GetBytes(value.{model.Name} ?? System.String.Empty));";
                model.Read = "System.Text.Encoding.UTF8.GetString(r.ReadBytes((int)r.ReadVarUInt()).ToArray())";
                model.Diff = $"!string.Equals(a.{model.Name}, b.{model.Name}, System.StringComparison.Ordinal)";
                return;
        }

        if (type.ToDisplayString() == "System.Guid")
        {
            model.Tag = 9;
            model.Write = $"w.WriteBytes(value.{model.Name}.ToByteArray());";
            model.Read = "new System.Guid(r.ReadBytes(16).ToArray())";
            model.Diff = $"a.{model.Name} != b.{model.Name}";
            return;
        }

        if (type.ToDisplayString() == "System.DateTime")
        {
            model.Tag = 10;
            model.Write = $"w.WriteUInt((uint)value.{model.Name}.ToUniversalTime().Ticks); w.WriteUInt((uint)(value.{model.Name}.ToUniversalTime().Ticks >> 32));";
            model.Read = "new System.DateTime((long)(((ulong)r.ReadUInt() << 32) | r.ReadUInt()), System.DateTimeKind.Utc)";
            model.Diff = $"a.{model.Name} != b.{model.Name}";
            return;
        }

        Unsupported();
    }

    private static string Bit(int order) => "(1UL << " + (order - 1) + ")";

    private static string AllFieldsMask(IEnumerable<FieldModel> fields) =>
        fields.Aggregate(0UL, (mask, f) => mask | (1UL << (f.Order - 1))).ToString();

    private static string SchemaHash(IEnumerable<FieldModel> fields)
    {
        // Names intentionally excluded: renaming a member must not change the wire schema.
        var payload = string.Join("|", fields.OrderBy(f => f.Order).Select(f => f.Order + ":" + f.Tag));
        uint hash = 2166136261;
        foreach (var c in payload)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash.ToString("x8");
    }
}
